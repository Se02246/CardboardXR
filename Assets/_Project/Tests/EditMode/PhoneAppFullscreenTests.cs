using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    public class PhoneAppFullscreenTests
    {
        readonly List<GameObject> m_Created = new();

        [TearDown]
        public void TearDown()
        {
            GazePointer.LockCursorToCenter = false;
            GazePointer.OverlayCanvasesOnly = false;
            GazePointer.OnEmptyTriggerClick = null;
            GazeCanvas.AllowedLayerMask = ~0;
            SettingsSummoner.SuppressForFullscreen = false;
            SettingsSummoner.FullscreenExitAction = null;

            foreach (var go in m_Created)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            m_Created.Clear();
        }

        [Test]
        public void GazePointer_LockCursorToCenterForcesForwardDirectionEvenWithAnglesSet()
        {
            var head = new GameObject("Head");
            m_Created.Add(head);
            var gaze = head.AddComponent<GazePointer>();
            gaze.CursorAnglesDeg = new Vector2(18f, -12f);

            Assert.AreNotEqual(Vector3.forward, gaze.CursorLocalDirection);

            GazePointer.LockCursorToCenter = true;
            Assert.AreEqual(Vector3.forward, gaze.CursorLocalDirection);
            Assert.AreEqual(Vector3.forward, gaze.PointerRay.direction);
        }

        [Test]
        public void GazeCanvas_AllowedLayerMaskFiltersOutNonOverlayCanvases()
        {
            var normalGo = CreateWorldCanvas("NormalCanvas", layer: 0);
            var normalCanvas = normalGo.GetComponent<GazeCanvas>();
            var overlayGo = CreateWorldCanvas("OverlayCanvas", layer: StereoRig.FullscreenOverlayLayer);
            var overlayCanvas = overlayGo.GetComponent<GazeCanvas>();

            var ray = new Ray(Vector3.zero, Vector3.forward);
            Assert.IsTrue(normalCanvas.Raycast(ray, 10f, out _, out _));
            Assert.IsTrue(overlayCanvas.Raycast(ray, 10f, out _, out _));

            GazeCanvas.AllowedLayerMask = 1 << StereoRig.FullscreenOverlayLayer;
            Assert.IsFalse(normalCanvas.Raycast(ray, 10f, out _, out _));
            Assert.IsTrue(overlayCanvas.Raycast(ray, 10f, out _, out _));
        }

        [Test]
        public void GazePointer_OverlayCanvasesOnlySkipsPhysicsCollidersAndFiresEmptyTriggerClick()
        {
            var head = new GameObject("Head");
            m_Created.Add(head);
            var gaze = head.AddComponent<GazePointer>();
            gaze.RealSurfaceMask = 0;

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m_Created.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 2f);
            Physics.SyncTransforms();

            gaze.UpdateGaze();
            Assert.AreSame(wall, gaze.Target);

            var emptyClicks = 0;
            GazePointer.OverlayCanvasesOnly = true;
            GazeCanvas.AllowedLayerMask = 1 << StereoRig.FullscreenOverlayLayer;
            GazePointer.OnEmptyTriggerClick = () => emptyClicks++;

            gaze.UpdateGaze();
            Assert.IsNull(gaze.Target);

            gaze.TriggerDown();
            gaze.TriggerUp();
            Assert.AreEqual(1, emptyClicks);
        }

        [Test]
        public void PinchRouter_RoutesExitButtonBeforeFullscreenDemoTap()
        {
            var demoCalls = 0;
            System.Func<bool> demoTakes = () =>
            {
                demoCalls++;
                return true;
            };

            // Looking at the Exit button takes priority over the fullscreen demo pinch handler.
            var exitRoute = PinchRouter.Order(false, false, onButton: true, demoTakes, canGrab: false, onHandOnlyObject: false);
            Assert.AreEqual(PinchRoute.Button, exitRoute);
            Assert.AreEqual(0, demoCalls);

            // Looking anywhere else in fullscreen hits the demo trigger handler cleanly even if canGrab were true.
            var appRoute = PinchRouter.Order(false, false, onButton: false, demoTakes, canGrab: true, onHandOnlyObject: false);
            Assert.AreEqual(PinchRoute.Demo, appRoute);
            Assert.AreEqual(1, demoCalls);
        }

        GameObject CreateWorldCanvas(string name, int layer)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = layer };
            m_Created.Add(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<GazeCanvas>();
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(400f, 400f);
            rect.localScale = Vector3.one * 0.002f;
            rect.position = new Vector3(0f, 0f, 1.5f);

            var imgGo = new GameObject("Hit", typeof(RectTransform)) { layer = layer };
            imgGo.transform.SetParent(go.transform, false);
            ((RectTransform)imgGo.transform).sizeDelta = new Vector2(300f, 300f);
            imgGo.AddComponent<Image>();
            return go;
        }
    }
}
