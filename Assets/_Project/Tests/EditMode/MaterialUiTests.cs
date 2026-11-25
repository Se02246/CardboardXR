using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    public class MaterialUiTests
    {
        [Test]
        public void DpFollowsAndroidXrTimesTheCardboardScale()
        {
            var scale = M3Theme.CardboardScale;
            try
            {
                M3Theme.CardboardScale = 1f;
                // Android XR: 1024 dp at 1.75 m is perceived as 1556 mm.
                Assert.AreEqual(1.556f, M3Theme.DpToMeters(1024f, 1.75f), 0.002f);
                M3Theme.CardboardScale = 1.6f;
                Assert.AreEqual(1.556f * 1.6f, M3Theme.DpToMeters(1024f, 1.75f), 0.004f);
                // Angular: twice as far, twice as big.
                Assert.AreEqual(2f * M3Theme.DpToMeters(56f, 1f), M3Theme.DpToMeters(56f, 2f), 1e-6f);
            }
            finally
            {
                M3Theme.CardboardScale = scale;
            }
        }

        [Test]
        public void TextColorsAreLegibleOnTheirSurfaces()
        {
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSurface, M3Theme.SurfaceContainerHigh), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSurfaceVariant, M3Theme.SurfaceContainerHigh), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnPrimary, M3Theme.Primary), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.OnSecondaryContainer, M3Theme.SecondaryContainer), 4.5f);
            Assert.GreaterOrEqual(M3Theme.Contrast(M3Theme.InverseOnSurface, M3Theme.InverseSurface), 4.5f);
        }

        [Test]
        public void NoTextIsSmallerThanXrsMinimum()
        {
            foreach (M3Theme.Type type in System.Enum.GetValues(typeof(M3Theme.Type)))
                Assert.GreaterOrEqual(M3Theme.Style(type).size, 14f, type.ToString());
        }

        [Test]
        public void CornersAreDrawnAtTheirRadius()
        {
            // The sprite's corner is k_Radius pixels; at the multiplier it spans the radius in units.
            Assert.AreEqual(M3Shapes.k_Radius, M3Shapes.PixelsPerUnitMultiplier(32f) * 32f, 1e-4f);
            var sprite = M3Shapes.CreateRound();
            try
            {
                Assert.AreEqual(new Vector4(M3Shapes.k_Radius, M3Shapes.k_Radius, M3Shapes.k_Radius, M3Shapes.k_Radius), sprite.border);
                var texture = sprite.texture;
                Assert.AreEqual(0f, texture.GetPixel(0, 0).a, 0.01f, "outside the circle");
                Assert.AreEqual(1f, texture.GetPixel(M3Shapes.k_Size / 2, M3Shapes.k_Size / 2).a, 0.01f, "the middle");
            }
            finally
            {
                Object.DestroyImmediate(sprite.texture);
                Object.DestroyImmediate(sprite);
            }
        }

        [Test]
        public void StateLayerFollowsHoverAndPress()
        {
            Assert.AreEqual(0f, M3StateLayer.Target(false, false, true));
            Assert.AreEqual(M3Theme.k_HoverOpacity, M3StateLayer.Target(true, false, true));
            Assert.AreEqual(M3Theme.k_PressedOpacity, M3StateLayer.Target(true, true, true));
            Assert.AreEqual(0f, M3StateLayer.Target(true, true, false), "nothing shows on a disabled element");
        }

        [Test]
        public void ButtonsTakeTheirVariantsColors()
        {
            var filled = M3Button.Colors(M3Button.Variant.Filled, false, true, Color.white);
            Assert.AreEqual(M3Theme.Primary, filled.container);
            Assert.AreEqual(M3Theme.OnPrimary, filled.content);
            var tonal = M3Button.Colors(M3Button.Variant.Tonal, false, true, Color.white);
            Assert.AreEqual(M3Theme.SecondaryContainer, tonal.container);
            // A selected toggle reads as on.
            Assert.AreEqual(M3Theme.Primary, M3Button.Colors(M3Button.Variant.Tonal, true, true, Color.white).container);
            var toolbarOn = M3Button.Colors(M3Button.Variant.Toolbar, true, true, Color.white);
            var toolbarOff = M3Button.Colors(M3Button.Variant.Toolbar, false, true, Color.white);
            Assert.AreEqual(M3Theme.Primary, toolbarOn.container);
            Assert.AreEqual(M3Theme.OnPrimary, toolbarOn.content);
            Assert.AreEqual(0f, toolbarOff.container.a);
            var disabled = M3Button.Colors(M3Button.Variant.Filled, false, false, Color.white);
            Assert.AreEqual(M3Theme.k_DisabledContent, disabled.content.a, 1e-5f);
            Assert.AreEqual(M3Theme.k_DisabledContainer, disabled.container.a, 1e-5f);
            var swatch = M3Button.Colors(M3Button.Variant.Swatch, true, true, Color.red);
            Assert.AreEqual(Color.red, swatch.container);
            Assert.AreEqual(M3Theme.Primary, swatch.outline);
        }

        [Test]
        public void IconsResolveToPrivateUseCharacters()
        {
            foreach (var name in M3Icons.Codepoints.Keys)
            {
                var glyph = M3Icons.Glyph(name);
                Assert.IsNotEmpty(glyph, name);
                Assert.That(char.ConvertToUtf32(glyph, 0), Is.InRange(0xE000, 0xF8FF), name);
            }
            Assert.IsEmpty(M3Icons.Glyph("no_such_icon"));
        }

        [Test]
        public void TheIconFontHasEveryIcon()
        {
            var assets = M3Assets.Instance;
            if (assets == null || assets.Icons == null)
                Assert.Ignore("The Material UI's assets haven't been made yet (they are with the first build)");
            foreach (var codepoint in M3Icons.Codepoints.Values)
                Assert.IsTrue(assets.Icons.characterLookupTable.ContainsKey((uint)codepoint), $"U+{codepoint:X4}");
        }

        [Test]
        public void ToolbarClosesUpAroundHiddenItems()
        {
            // Lobby | Recenter, Scan | 6DoF, Passthrough, Pinch, items 92 wide, dividers 16.
            var groups = new[] { 0, 1, 1, 2, 2, 2 };
            var itemX = new float[6];
            var dividerX = new float[3];
            var width = M3Toolbar.Arrange(new[] { true, true, true, true, true, true }, groups, 3, 92f, 16f, itemX, dividerX);
            Assert.AreEqual(6 * 92f + 2 * 16f, width);
            Assert.AreEqual(46f, itemX[0]);
            Assert.AreEqual(92f + 8f, dividerX[1]);
            Assert.AreEqual(92f + 16f + 46f, itemX[1]);

            // Lobby hidden: its group and the divider after it go, and the rest start at the left.
            width = M3Toolbar.Arrange(new[] { false, true, true, true, true, true }, groups, 3, 92f, 16f, itemX, dividerX);
            Assert.AreEqual(5 * 92f + 16f, width);
            Assert.IsNaN(itemX[0]);
            Assert.IsNaN(dividerX[1]);
            Assert.AreEqual(46f, itemX[1]);
            Assert.AreEqual(2 * 92f + 8f, dividerX[2]);
        }

        [Test]
        public void VisionSurfaceEffectClampsRadiusAndPacksStyleParams()
        {
            Assert.AreEqual(40f, VisionSurfaceEffect.ClampRadius(1000f, new Vector2(120f, 80f)), 1e-4f);
            Assert.AreEqual(20f, VisionSurfaceEffect.ClampRadius(20f, new Vector2(120f, 80f)), 1e-4f);
            Assert.AreEqual(0f, VisionSurfaceEffect.ClampRadius(-5f, new Vector2(120f, 80f)), 1e-4f);

            var packed = VisionSurfaceEffect.PackParams(26f, VisionSurfaceStyle.Recessed, 0.5f, 0.35f);
            Assert.AreEqual(new Vector4(26f, 0.5f, 0.35f, 1f), packed);
        }

        [Test]
        public void VisionMaterialsAreCreatedAndAssignedInM3Assets()
        {
            var assets = M3Assets.Instance;
            if (assets == null)
                Assert.Ignore("M3Assets hasn't been created yet");
            Assert.IsNotNull(assets.GlassMaterial);
            Assert.IsNotNull(assets.RoundedScreenMaterial);
            Assert.AreEqual(M3Assets.k_VisionGlassShader, assets.GlassMaterial.shader.name);
            Assert.AreEqual(M3Assets.k_RoundedScreenShader, assets.RoundedScreenMaterial.shader.name);
        }

        [Test]
        public void M3CanvasScaleFactorIsPreservedByM3Fade()
        {
            var rect = M3.Canvas("Test Scaled Canvas", null, new Vector2(800f, 450f), 1.5f,
                M3Theme.SurfaceContainer, M3Theme.k_ShapePanel, scaleFactor: 0.62f);
            try
            {
                var expected = M3Theme.DpToMeters(1f, 1.5f) * 0.62f;
                Assert.AreEqual(expected, rect.localScale.x, 1e-5f);
                Assert.AreEqual(expected, M3Fade.GetBaseScale(rect.gameObject).x, 1e-5f);

                var doubled = Vector3.one * (expected * 1.48f);
                M3Fade.SetBaseScale(rect.gameObject, doubled);
                Assert.AreEqual(doubled.x, rect.localScale.x, 1e-5f);
                Assert.AreEqual(doubled.x, M3Fade.GetBaseScale(rect.gameObject).x, 1e-5f);
            }
            finally
            {
                Object.DestroyImmediate(rect.gameObject);
            }
        }

        [Test]
        public void GazeCanvasIgnoresRaycastWhenCanvasDisabled()
        {
            var canvasGo = new GameObject("Test GazeCanvas", typeof(RectTransform));
            try
            {
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var gazeCanvas = canvasGo.AddComponent<GazeCanvas>();
                var rt = (RectTransform)canvasGo.transform;
                rt.sizeDelta = new Vector2(500f, 500f);
                rt.localScale = Vector3.one * 0.001f;
                rt.position = new Vector3(0f, 0f, 2f);

                var imgGo = new GameObject("Target", typeof(RectTransform));
                imgGo.transform.SetParent(rt, false);
                ((RectTransform)imgGo.transform).sizeDelta = new Vector2(300f, 300f);
                imgGo.AddComponent<Image>().raycastTarget = true;

                var ray = new Ray(Vector3.zero, Vector3.forward);
                Assert.IsTrue(gazeCanvas.Raycast(ray, 10f, out _, out _));

                canvas.enabled = false;
                Assert.IsFalse(gazeCanvas.Raycast(ray, 10f, out _, out _));
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
            }
        }

        [Test]
        public void M3ButtonSelectedSetterIsIdempotent()
        {
            var canvas = M3.Canvas("Test Button Canvas", null, new Vector2(400f, 200f), 1.5f);
            try
            {
                var btn = M3.Button("Test", canvas, "Prova", M3Button.Variant.Tonal, Vector2.zero, null);
                btn.Selected = true;
                btn.Selected = true;
                Assert.IsTrue(btn.Selected);
                btn.Selected = false;
                btn.Selected = false;
                Assert.IsFalse(btn.Selected);
            }
            finally
            {
                Object.DestroyImmediate(canvas.gameObject);
            }
        }

        [Test]
        public void VisionGlassCornerArcStylePacksModeFive()
        {
            var packed = VisionSurfaceEffect.PackParams(36f, VisionSurfaceStyle.CornerArc, 3.0f, 0.9f);
            Assert.AreEqual(new Vector4(36f, 3.0f, 0.9f, 5f), packed);
        }

        [Test]
        public void WatchGrabControllerComputeResizedScaleClampsWithinBounds()
        {
            var anchorBase = Vector3.one * 0.002f;
            var start = anchorBase;

            Assert.AreEqual(anchorBase.x * 1.5f,
                WatchGrabController.ComputeResizedScale(start, 1.5f, anchorBase).x, 1e-6f);
            Assert.AreEqual(anchorBase.x * 2.40f,
                WatchGrabController.ComputeResizedScale(start, 5.0f, anchorBase).x, 1e-6f);
            Assert.AreEqual(anchorBase.x * 0.40f,
                WatchGrabController.ComputeResizedScale(start, 0.1f, anchorBase).x, 1e-6f);
        }

        [Test]
        public void WatchGrabControllerToggleResizeActivatesAndReleasesCleanly()
        {
            var head = new GameObject("Head");
            var panel = M3.Canvas("Resize Target", null, new Vector2(800f, 450f), 1.5f);
            panel.position = new Vector3(0f, 0f, 1.5f);
            try
            {
                WatchGrabController.Release();
                WatchGrabController.ToggleResizePanel(panel, head.transform, new Ray(Vector3.zero, Vector3.forward));
                Assert.IsTrue(WatchGrabController.IsResizing);
                Assert.IsTrue(WatchGrabController.IsBusy);
                Assert.IsTrue(WatchGrabController.IsResizingPanel(panel));

                WatchGrabController.ToggleResizePanel(panel, head.transform);
                Assert.IsFalse(WatchGrabController.IsResizing);
                Assert.IsFalse(WatchGrabController.IsBusy);
                Assert.IsFalse(WatchGrabController.IsResizingPanel(panel));
            }
            finally
            {
                WatchGrabController.Release();
                Object.DestroyImmediate(panel.gameObject);
                Object.DestroyImmediate(head);
            }
        }

        [Test]
        public void SpatialWindowAnchorSetUniformScaleUpdatesAndClampsM3FadeBaseScale()
        {
            var panel = M3.Canvas("Anchored Resize Canvas", null, new Vector2(800f, 450f), 1.5f);
            try
            {
                var baseS = M3Theme.DpToMeters(1f, 1.5f);
                Assert.IsTrue(SpatialWindowAnchor.SetUniformScale(panel, Vector3.one * (baseS * 1.5f)));
                Assert.AreEqual(baseS * 1.5f, panel.localScale.x, 1e-5f);
                Assert.AreEqual(baseS * 1.5f, M3Fade.GetBaseScale(panel.gameObject).x, 1e-5f);

                SpatialWindowAnchor.SetUniformScale(panel, Vector3.one * (baseS * 10f));
                Assert.AreEqual(baseS * 2.40f, panel.localScale.x, 1e-5f);
                Assert.AreEqual(baseS * 2.40f, M3Fade.GetBaseScale(panel.gameObject).x, 1e-5f);

                SpatialWindowAnchor.SetUniformScale(panel, Vector3.one * (baseS * 0.05f));
                Assert.AreEqual(baseS * 0.40f, panel.localScale.x, 1e-5f);
                Assert.AreEqual(baseS * 0.40f, M3Fade.GetBaseScale(panel.gameObject).x, 1e-5f);
            }
            finally
            {
                Object.DestroyImmediate(panel.gameObject);
            }
        }

        [Test]
        public void M3FadeEvaluatesSmoothOpenCloseCurvesAndInvokesOnHidden()
        {
            Assert.AreEqual(0f, M3Fade.EvaluateAlpha(0f, hiding: false), 1e-5f);
            Assert.AreEqual(1f, M3Fade.EvaluateAlpha(1f, hiding: false), 1e-5f);
            Assert.AreEqual(0f, M3Fade.EvaluateAlpha(0f, hiding: true), 1e-5f);
            Assert.AreEqual(1f, M3Fade.EvaluateAlpha(1f, hiding: true), 1e-5f);

            var openMidScale = M3Fade.EvaluateScaleFactor(0.5f, hiding: false);
            var closeMidScale = M3Fade.EvaluateScaleFactor(0.5f, hiding: true);
            Assert.Greater(openMidScale, 0.86f);
            Assert.Less(openMidScale, 1f);
            Assert.Greater(closeMidScale, 0.88f);
            Assert.Less(closeMidScale, 1f);

            var panel = M3.Canvas("Fade Callback Canvas", null, new Vector2(400f, 300f), 1.5f);
            try
            {
                panel.gameObject.SetActive(false);
                var invoked = false;
                M3Fade.Hide(panel.gameObject, () => invoked = true);
                Assert.IsTrue(invoked);
            }
            finally
            {
                Object.DestroyImmediate(panel.gameObject);
            }
        }

        [Test]
        public void FloatingWindowChrome_HidesCompletelyWhenNotGazedAndShowsOnGazeOrHold()
        {
            Assert.AreEqual(0f, FloatingWindowHandle.ComputeTargetChromeAlpha(false, false, false, 2f), 1e-5f);
            Assert.AreEqual(1f, FloatingWindowHandle.ComputeTargetChromeAlpha(false, false, true, 2f), 1e-5f);
            Assert.AreEqual(1f, FloatingWindowHandle.ComputeTargetChromeAlpha(false, false, false, 0.2f), 1e-5f);
            Assert.AreEqual(1f, FloatingWindowHandle.ComputeTargetChromeAlpha(true, false, false, 2f), 1e-5f);
            Assert.AreEqual(1f, FloatingWindowHandle.ComputeTargetChromeAlpha(false, true, false, 2f), 1e-5f);
        }
    }
}
