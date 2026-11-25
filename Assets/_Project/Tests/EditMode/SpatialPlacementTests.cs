using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo.Tests
{
    public class SpatialPlacementTests
    {
        readonly List<GameObject> m_Created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var created in m_Created)
            {
                if (created != null)
                    Object.DestroyImmediate(created);
            }
            m_Created.Clear();
        }

        [Test]
        public void DraggedScreen_FollowsUserTranslationAtConstantDistance()
        {
            var headStart = Vector3.zero;
            var headWalked = new Vector3(3f, 0.2f, -1.5f);
            const float yaw = 25f;
            const float pitch = -8f;
            const float dist = 1.85f;

            foreach (var spherical in new[] { false, true })
            {
                var startPose = WatchGrabController.ComputeDraggedPose(headStart, yaw, pitch, dist, spherical);
                var walkedPose = WatchGrabController.ComputeDraggedPose(headWalked, yaw, pitch, dist, spherical);

                var deltaPos = walkedPose.position - startPose.position;
                Assert.That((deltaPos - headWalked).magnitude, Is.LessThan(1e-4f),
                    $"Expected dragged screen to translate identically with head (spherical={spherical}).");
                Assert.AreEqual(dist, Vector3.Distance(headWalked, walkedPose.position), 1e-4f);
                Assert.AreEqual(startPose.rotation.eulerAngles.x, walkedPose.rotation.eulerAngles.x, 1e-3f);
                Assert.AreEqual(startPose.rotation.eulerAngles.y, walkedPose.rotation.eulerAngles.y, 1e-3f);
            }
        }

        [Test]
        public void SelectBestHit_PrefersPlaneOverDepthOnSameSurface()
        {
            var candidates = new List<RaycastCandidate>
            {
                new()
                {
                    Point = new Vector3(0f, -1.58f, 2.00f),
                    Normal = Vector3.up,
                    Distance = 2.00f,
                    HitType = TrackableType.PlaneWithinPolygon,
                },
                new()
                {
                    Point = new Vector3(0f, -1.57f, 1.95f),
                    Normal = Vector3.up,
                    Distance = 1.95f,
                    HitType = TrackableType.Depth,
                },
            };

            var hit = SpatialSurfaceLocator.SelectBestHit(candidates, Vector3.zero, PlacementSurfaceFilter.HorizontalAny, null);
            Assert.IsTrue(hit.Valid);
            Assert.AreEqual(SpatialHitQuality.PlanePolygon, hit.Quality);
            Assert.AreEqual(2.00f, hit.Distance, 1e-4f);
        }

        [Test]
        public void SelectBestHit_NearerDepthBeatsFartherPlane()
        {
            var candidates = new List<RaycastCandidate>
            {
                new()
                {
                    Point = new Vector3(0f, -1.60f, 2.50f),
                    Normal = Vector3.up,
                    Distance = 2.50f,
                    HitType = TrackableType.PlaneWithinPolygon,
                },
                new()
                {
                    Point = new Vector3(0f, -0.75f, 1.10f),
                    Normal = new Vector3(0.05f, 0.99f, 0f).normalized,
                    Distance = 1.10f,
                    HitType = TrackableType.Depth,
                },
            };

            var hit = SpatialSurfaceLocator.SelectBestHit(candidates, Vector3.zero, PlacementSurfaceFilter.HorizontalAny, null);
            Assert.IsTrue(hit.Valid);
            Assert.AreEqual(SpatialHitQuality.Depth, hit.Quality);
            Assert.AreEqual(1.10f, hit.Distance, 1e-4f);
            Assert.AreEqual(Vector3.up, hit.Normal);
        }

        [Test]
        public void SelectBestHit_DepthBeatsEstimatedAndRejectsCloseFeaturePoints()
        {
            var onlyCloseFeature = new List<RaycastCandidate>
            {
                new()
                {
                    Point = new Vector3(0f, -0.3f, 0.40f),
                    Normal = Vector3.up,
                    Distance = 0.40f,
                    HitType = TrackableType.FeaturePoint,
                },
            };
            Assert.IsFalse(SpatialSurfaceLocator.SelectBestHit(onlyCloseFeature, Vector3.zero,
                PlacementSurfaceFilter.HorizontalAny, null).Valid);

            var mixed = new List<RaycastCandidate>
            {
                new()
                {
                    Point = new Vector3(0f, -0.8f, 1.2f),
                    Normal = Vector3.up,
                    Distance = 1.2f,
                    HitType = TrackableType.FeaturePoint,
                },
                new()
                {
                    Point = new Vector3(0f, -0.8f, 1.3f),
                    Normal = Vector3.up,
                    Distance = 1.3f,
                    HitType = TrackableType.PlaneEstimated,
                },
                new()
                {
                    Point = new Vector3(0f, -0.8f, 1.4f),
                    Normal = Vector3.up,
                    Distance = 1.4f,
                    HitType = TrackableType.Depth,
                },
            };
            var hit = SpatialSurfaceLocator.SelectBestHit(mixed, Vector3.zero, PlacementSurfaceFilter.HorizontalAny, null);
            Assert.IsTrue(hit.Valid);
            Assert.AreEqual(SpatialHitQuality.Depth, hit.Quality);
        }

        [Test]
        public void SelectBestHit_FloorOnlyRejectsTablesWallsAndAboveKnownFloor()
        {
            var candidates = new List<RaycastCandidate>
            {
                new()
                {
                    // Desk top: too high relative to head at y=0
                    Point = new Vector3(0f, -0.70f, 1.20f),
                    Normal = Vector3.up,
                    Distance = 1.20f,
                    HitType = TrackableType.Depth,
                },
                new()
                {
                    // Wall: not flat
                    Point = new Vector3(0f, -1.50f, 1.80f),
                    Normal = Vector3.back,
                    Distance = 1.80f,
                    HitType = TrackableType.Depth,
                },
                new()
                {
                    // Chair seat: below 0.85m from eyes, yet more than 0.30m above known floor (-1.60)
                    Point = new Vector3(0f, -1.10f, 1.60f),
                    Normal = Vector3.up,
                    Distance = 1.60f,
                    HitType = TrackableType.Depth,
                },
                new()
                {
                    // Real floor
                    Point = new Vector3(0f, -1.58f, 2.20f),
                    Normal = new Vector3(0.02f, 0.99f, 0f).normalized,
                    Distance = 2.20f,
                    HitType = TrackableType.Depth,
                },
            };

            var hit = SpatialSurfaceLocator.SelectBestHit(candidates, Vector3.zero, PlacementSurfaceFilter.FloorOnly, -1.60f);
            Assert.IsTrue(hit.Valid);
            Assert.AreEqual(2.20f, hit.Distance, 1e-4f);
            Assert.AreEqual(Vector3.up, hit.Normal);
        }

        [Test]
        public void SelectBestHit_HorizontalAnyAcceptsTablesAndRejectsSteepWalls()
        {
            var candidates = new List<RaycastCandidate>
            {
                new()
                {
                    Point = new Vector3(0f, -0.50f, 1.50f),
                    Normal = new Vector3(0f, 0.30f, -0.95f).normalized,
                    Distance = 1.50f,
                    HitType = TrackableType.Depth,
                },
                new()
                {
                    Point = new Vector3(0f, -0.78f, 1.15f),
                    Normal = new Vector3(0.05f, 0.95f, 0f).normalized,
                    Distance = 1.15f,
                    HitType = TrackableType.Depth,
                },
            };

            var hit = SpatialSurfaceLocator.SelectBestHit(candidates, Vector3.zero, PlacementSurfaceFilter.HorizontalAny, null);
            Assert.IsTrue(hit.Valid);
            Assert.AreEqual(1.15f, hit.Distance, 1e-4f);
            Assert.AreEqual(Vector3.up, hit.Normal);
        }

        [Test]
        public void TryEstimateFloorHit_IntersectsKnownOrDefaultFloorWhenLookingDown()
        {
            var rayDown = new Ray(Vector3.zero, new Vector3(0f, -0.6f, 0.8f).normalized);
            Assert.IsTrue(SpatialSurfaceLocator.TryEstimateFloorHit(rayDown, Vector3.zero, -1.50f, out var hitKnown));
            Assert.AreEqual(SpatialHitQuality.EstimatedFloor, hitKnown.Quality);
            Assert.AreEqual(-1.50f, hitKnown.Point.y, 1e-4f);
            Assert.AreEqual(2.50f, hitKnown.Distance, 1e-3f);

            Assert.IsTrue(SpatialSurfaceLocator.TryEstimateFloorHit(rayDown, Vector3.zero, null, out var hitDefault));
            Assert.AreEqual(-1.45f, hitDefault.Point.y, 1e-4f);

            var rayShallow = new Ray(Vector3.zero, new Vector3(0f, -0.10f, 0.995f).normalized);
            Assert.IsFalse(SpatialSurfaceLocator.TryEstimateFloorHit(rayShallow, Vector3.zero, -1.50f, out _));

            var rayUp = new Ray(Vector3.zero, new Vector3(0f, 0.5f, 0.866f).normalized);
            Assert.IsFalse(SpatialSurfaceLocator.TryEstimateFloorHit(rayUp, Vector3.zero, -1.50f, out _));
        }

        [Test]
        public void PointInPolygon_HandlesConvexAndConcaveShapes()
        {
            var square = new List<Vector2>
            {
                new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f),
            };
            Assert.IsTrue(SpatialSurfaceLocator.PointInPolygon(Vector2.zero, square));
            Assert.IsFalse(SpatialSurfaceLocator.PointInPolygon(new Vector2(1.2f, 0f), square));

            // L-shaped concave polygon missing the upper-right quadrant (x > 0 && y > 0).
            var lShape = new List<Vector2>
            {
                new(-1f, -1f), new(1f, -1f), new(1f, 0f), new(0f, 0f), new(0f, 1f), new(-1f, 1f),
            };
            Assert.IsTrue(SpatialSurfaceLocator.PointInPolygon(new Vector2(-0.5f, 0.5f), lShape));
            Assert.IsTrue(SpatialSurfaceLocator.PointInPolygon(new Vector2(0.5f, -0.5f), lShape));
            Assert.IsFalse(SpatialSurfaceLocator.PointInPolygon(new Vector2(0.5f, 0.5f), lShape));
        }

        [Test]
        public void GazePointer_YieldsSpatialSurfaceToUiAndUpdatesNotify()
        {
            var head = new GameObject("Head");
            m_Created.Add(head);
            var pointer = head.AddComponent<GazePointer>();
            pointer.RealSurfaceMask = 1 << 30;

            var targetObj = new GameObject("PlacementTarget");
            m_Created.Add(targetObj);

            var stub = new StubSpatialProvider(targetObj);
            pointer.SpatialSurfaceOverride = stub;

            // Looking at empty air -> spatial surface wins.
            pointer.UpdateGaze();
            Assert.AreSame(targetObj, pointer.Target);
            Assert.AreEqual(1, stub.WonCount);

            // Real surface collider (layer 30) is superseded by SpatialSurfaceLocator.
            var legacyPlane = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m_Created.Add(legacyPlane);
            legacyPlane.layer = 30;
            legacyPlane.transform.position = new Vector3(0f, 0f, 1.5f);
            Physics.SyncTransforms();

            pointer.UpdateGaze();
            Assert.AreSame(targetObj, pointer.Target);
            Assert.AreEqual(2, stub.WonCount);

            // World-space UI panel in front hides spatial placement reticle cleanly.
            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            m_Created.Add(canvasGo);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<GazeCanvas>();
            var rect = (RectTransform)canvasGo.transform;
            rect.sizeDelta = new Vector2(1000f, 1000f);
            rect.localScale = Vector3.one * 0.001f;
            rect.position = new Vector3(0f, 0f, 1.2f);
            var panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(canvas.transform, false);
            ((RectTransform)panel.transform).sizeDelta = new Vector2(500f, 500f);
            panel.AddComponent<Image>();

            pointer.UpdateGaze();
            Assert.AreSame(panel, pointer.Target);
            Assert.AreEqual(1, stub.LostCount);
        }

        sealed class StubSpatialProvider : ISpatialSurfaceProvider
        {
            readonly GameObject m_Target;
            public int WonCount { get; private set; }
            public int LostCount { get; private set; }

            public StubSpatialProvider(GameObject target) => m_Target = target;

            public bool IsPlacementActive => true;

            public bool TryRaycast(Ray ray, out GameObject target, out Vector3 point, out Vector3 normal, out float distance)
            {
                target = m_Target;
                point = ray.origin + ray.direction * 2f;
                normal = Vector3.up;
                distance = 2f;
                return true;
            }

            public void NotifyHitResult(bool won)
            {
                if (won)
                    WonCount++;
                else
                    LostCount++;
            }
        }
    }
}
