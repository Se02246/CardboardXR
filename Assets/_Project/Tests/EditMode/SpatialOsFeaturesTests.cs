using System.Collections.Generic;
using CardboardXR.Demos;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    public class SpatialOsFeaturesTests
    {
        [Test]
        public void PassthroughReprojection_IsExactWithinLinearBandAndSmoothlySaturatesWithoutSnap()
        {
            var captureRot = Quaternion.Euler(10f, 20f, 0f);

            // Exact 1:1 world-lock inside 0..16 deg band.
            for (var deg = 0; deg <= 16; deg += 4)
            {
                var headRot = captureRot * Quaternion.Euler(0f, deg, 0f);
                var result = PassthroughController.ComputeReprojectedRotation(
                    headRot, captureRot, hasCaptureRot: true, secondsSinceFrame: 0.03f);
                Assert.Less(Quaternion.Angle(result, captureRot), 0.01f,
                    $"Expected 1:1 world-lock at {deg} deg offset.");
            }

            // Above 16 deg: C1-smooth monotonic saturation < 26 deg, never snaps back to headRot.
            var prevLag = 16f;
            foreach (var deg in new[] { 20f, 30f, 45f, 90f })
            {
                var headRot = captureRot * Quaternion.Euler(deg, 0f, 0f);
                var result = PassthroughController.ComputeReprojectedRotation(
                    headRot, captureRot, hasCaptureRot: true, secondsSinceFrame: 0.03f);
                var lagFromHead = Quaternion.Angle(headRot, result);
                Assert.Greater(lagFromHead, prevLag, $"Reprojection should increase smoothly at {deg} deg.");
                Assert.Less(lagFromHead, 26f, $"Reprojection must stay clamped below 26 deg at {deg} deg.");
                prevLag = lagFromHead;
            }

            // Continuous sweep 0 -> 60 -> 0 deg in 1 deg increments has zero snap/hysteresis jump.
            var sweepCapture = Quaternion.identity;
            var prevSweep = PassthroughController.ComputeReprojectedRotation(
                Quaternion.identity, sweepCapture, hasCaptureRot: true, secondsSinceFrame: 0.02f);
            for (var deg = 1; deg <= 60; deg++)
            {
                var headRot = Quaternion.Euler(0f, deg, 0f);
                var curr = PassthroughController.ComputeReprojectedRotation(
                    headRot, sweepCapture, hasCaptureRot: true, secondsSinceFrame: 0.02f);
                Assert.LessOrEqual(Quaternion.Angle(prevSweep, curr), 1.01f,
                    $"Discontinuity detected going forward at {deg} deg.");
                prevSweep = curr;
            }
            for (var deg = 59; deg >= 0; deg--)
            {
                var headRot = Quaternion.Euler(0f, deg, 0f);
                var curr = PassthroughController.ComputeReprojectedRotation(
                    headRot, sweepCapture, hasCaptureRot: true, secondsSinceFrame: 0.02f);
                Assert.LessOrEqual(Quaternion.Angle(prevSweep, curr), 1.01f,
                    $"Discontinuity detected returning at {deg} deg.");
                prevSweep = curr;
            }

            // Stale frames fade smoothly between 0.25s and 0.50s back to headRot.
            var headTen = Quaternion.Euler(0f, 10f, 0f);
            var halfStale = PassthroughController.ComputeReprojectedRotation(
                headTen, Quaternion.identity, hasCaptureRot: true, secondsSinceFrame: 0.375f);
            Assert.AreEqual(5f, Quaternion.Angle(headTen, halfStale), 0.05f);

            var fullStale = PassthroughController.ComputeReprojectedRotation(
                headTen, Quaternion.identity, hasCaptureRot: true, secondsSinceFrame: 0.50f);
            Assert.Less(Quaternion.Angle(headTen, fullStale), 0.01f);

            // Missing capture rotation safely tracks current head rotation.
            var noCapture = PassthroughController.ComputeReprojectedRotation(
                headTen, Quaternion.identity, hasCaptureRot: false, secondsSinceFrame: 0.01f);
            Assert.Less(Quaternion.Angle(headTen, noCapture), 0.01f);
        }

        [Test]
        public void PalmPhonePose3D_SitsBetweenPalmAndFingersAndRotatesWithHand()
        {
            var joints = BuildHand3D(Quaternion.identity);
            var upright = SettingsSummoner.ComputePalmPhonePose3D(joints, rightHand: true, eyes: Vector3.zero);

            var palmMid = joints[0] * 0.30f + joints[5] * 0.25f + joints[9] * 0.25f + joints[17] * 0.20f;
            Assert.Greater(upright.position.y, palmMid.y, "Phone should sit toward proximal fingers rather than low wrist.");
            Assert.Greater(Vector3.Dot(upright.rotation * Vector3.up, Vector3.up), 0.85f);

            var rotatedJoints = BuildHand3D(Quaternion.Euler(0f, 0f, 90f));
            var tilted = SettingsSummoner.ComputePalmPhonePose3D(rotatedJoints, rightHand: true, eyes: Vector3.zero);
            Assert.Greater(Vector3.Dot(tilted.rotation * Vector3.up, Vector3.left), 0.85f,
                "Twisting hand 90 deg must rotate palm smartphone orientation with the fingers.");
        }

        [Test]
        public void PalmPhonePose2D_RotatesWithHandTwist()
        {
            var upright2D = BuildHand2D(0f);
            var poseUpright = SettingsSummoner.ComputePalmPhonePose2D(
                upright2D, new Vector2(640f, 480f), 64f, 50f, 0.42f, Vector3.zero, Quaternion.identity, flipImage: false);
            Assert.Greater(Vector3.Dot(poseUpright.rotation * Vector3.up, Vector3.up), 0.9f);

            var rotated2D = BuildHand2D(90f);
            var poseRotated = SettingsSummoner.ComputePalmPhonePose2D(
                rotated2D, new Vector2(640f, 480f), 64f, 50f, 0.42f, Vector3.zero, Quaternion.identity, flipImage: false);
            Assert.Less(Mathf.Abs(Vector3.Dot(poseRotated.rotation * Vector3.up, Vector3.up)), 0.2f);
            Assert.Greater(Mathf.Abs(Vector3.Dot(poseRotated.rotation * Vector3.up, Vector3.right)), 0.85f);
        }

        [Test]
        public void LookUpBarHysteresis_PreventsFlicker()
        {
            Assert.IsFalse(SettingsSummoner.ShouldShowLookUpBar(false, 25f));
            Assert.IsTrue(SettingsSummoner.ShouldShowLookUpBar(false, 34f));
            Assert.IsTrue(SettingsSummoner.ShouldShowLookUpBar(true, 26f));
            Assert.IsTrue(SettingsSummoner.ShouldShowLookUpBar(true, 22f));
            Assert.IsFalse(SettingsSummoner.ShouldShowLookUpBar(true, 21.5f));
        }

        [Test]
        public void NotificationBannerRules_FloatsHighDocksAboveLookUpBarAndPausesTimeout()
        {
            var level = NotificationBannerRules.ComputeStep(
                headPitchUpDeg: 0f, headYawDeg: 18f,
                lookUpBarShown: false, lookUpBarYawDeg: 0f, currentYawDeg: 18f,
                ageSeconds: 2f, timeoutSeconds: 8f, isHovered: false, wasDocked: false);
            Assert.IsFalse(level.IsDocked);
            Assert.IsFalse(level.ShouldDismiss);
            Assert.AreEqual(NotificationBannerRules.k_TopFovOffsetDeg, level.PitchDeg, 1e-3f);
            Assert.AreEqual(18f, level.YawDeg, 1e-3f);

            // Looking down clamps floating pitch safely above horizon
            var lookingDown = NotificationBannerRules.ComputeStep(
                headPitchUpDeg: -25f, headYawDeg: 0f,
                lookUpBarShown: false, lookUpBarYawDeg: 0f, currentYawDeg: 0f,
                ageSeconds: 1f, timeoutSeconds: 8f, isHovered: false, wasDocked: false);
            Assert.AreEqual(NotificationBannerRules.k_MinFloatPitchDeg, lookingDown.PitchDeg, 1e-3f);

            // Looking up docks above the look-up quick toggles bar, locks yaw to it, and pauses dismissal
            var docked = NotificationBannerRules.ComputeStep(
                headPitchUpDeg: 36f, headYawDeg: 55f,
                lookUpBarShown: true, lookUpBarYawDeg: 42f, currentYawDeg: 50f,
                ageSeconds: 30f, timeoutSeconds: 8f, isHovered: false, wasDocked: false);
            Assert.IsTrue(docked.IsDocked);
            Assert.IsFalse(docked.ShouldDismiss);
            Assert.AreEqual(NotificationBannerRules.k_DockPitchUpDeg, docked.PitchDeg, 1e-3f);
            Assert.AreEqual(42f, docked.YawDeg, 1e-3f);

            // Hovering prevents auto-dismiss; leaving hover past timeout dismisses
            var hovered = NotificationBannerRules.ComputeStep(
                0f, 0f, false, 0f, 0f, ageSeconds: 12f, timeoutSeconds: 8f, isHovered: true, wasDocked: false);
            Assert.IsFalse(hovered.ShouldDismiss);

            var expired = NotificationBannerRules.ComputeStep(
                0f, 0f, false, 0f, 0f, ageSeconds: 9f, timeoutSeconds: 8f, isHovered: false, wasDocked: false);
            Assert.IsTrue(expired.ShouldDismiss);
        }

        [Test]
        public void FormatDuration_FormatsSecondsMinutesAndHours()
        {
            Assert.AreEqual("0:00", SystemBridge.FormatDuration(-500));
            Assert.AreEqual("0:05", SystemBridge.FormatDuration(5000));
            Assert.AreEqual("3:05", SystemBridge.FormatDuration(185000));
            Assert.AreEqual("1:02:05", SystemBridge.FormatDuration(3725000));
        }

        [Test]
        public void FloatingSlots_ClearCenterConeWhileStayingVisibleInFov()
        {
            var smallOffset = SpatialWindowAnchor.IdealSideOffsetDeg(10f);
            Assert.GreaterOrEqual(smallOffset, 18f);
            Assert.LessOrEqual(smallOffset + 10f, 39f + 1e-3f);

            var mediumOffset = SpatialWindowAnchor.IdealSideOffsetDeg(15f);
            Assert.GreaterOrEqual(mediumOffset - 15f, 9f - 1e-3f);
            Assert.LessOrEqual(mediumOffset + 15f, 39f + 1e-3f);
        }

        [Test]
        public void FloatingSlots_NeverOverlapAcrossMultipleWindowsOrManualCenter()
        {
            var sameSide = new[]
            {
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 18f, HalfWidthDeg = 12f },
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 20f, HalfWidthDeg = 12f },
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 21f, HalfWidthDeg = 10f },
            };
            var sameSideOut = SpatialWindowAnchor.ResolveFloatingYawSlots(sameSide);
            AssertPairwiseClearance(sameSide, sameSideOut);

            var mixedWithManualCenter = new[]
            {
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 0f, HalfWidthDeg = 12f, KeepCenterAllowed = true },
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 12f, HalfWidthDeg = 11f, KeepCenterAllowed = false },
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = -14f, HalfWidthDeg = 11f, KeepCenterAllowed = false },
                new SpatialWindowAnchor.FloatingSlotInput { PreferredYawDeg = 28f, HalfWidthDeg = 10f, KeepCenterAllowed = false },
            };
            var mixedOut = SpatialWindowAnchor.ResolveFloatingYawSlots(mixedWithManualCenter);
            AssertPairwiseClearance(mixedWithManualCenter, mixedOut);
        }

        [Test]
        public void FloatingGazeBrake_StopsWhenGazedOrHeldAndRampsSmoothlyOnApproach()
        {
            Assert.AreEqual(0f, SpatialWindowAnchor.ComputeGazeBrakeFactor(true, 15f, 5f), 1e-4f);
            Assert.AreEqual(0f, SpatialWindowAnchor.ComputeGazeBrakeFactor(false, 20f, 0.2f), 1e-4f);
            Assert.AreEqual(0f, SpatialWindowAnchor.ComputeGazeBrakeFactor(false, 1.5f, 5f), 1e-4f);

            var mid = SpatialWindowAnchor.ComputeGazeBrakeFactor(false, 5.75f, 5f);
            Assert.Greater(mid, 0.1f);
            Assert.Less(mid, 0.9f);

            Assert.AreEqual(1f, SpatialWindowAnchor.ComputeGazeBrakeFactor(false, 10f, 5f), 1e-4f);
        }

        [Test]
        public void FloatingWorldYawStep_PausesInDeadzoneOrUnderGazeAndRespectsMaxSpeed()
        {
            var catching = false;

            // Within 14 deg deadzone -> stationary
            var stayed = SpatialWindowAnchor.StepFloatingWorldYaw(20f, 12f, 20f, 1f, 0.016f, ref catching);
            Assert.AreEqual(20f, stayed, 1e-4f);
            Assert.IsFalse(catching);

            // Past deadzone, but followGain == 0 (user looking at window) -> frozen completely
            var frozen = SpatialWindowAnchor.StepFloatingWorldYaw(20f, 35f, 20f, 0f, 0.016f, ref catching);
            Assert.AreEqual(20f, frozen, 1e-4f);
            Assert.IsFalse(catching);

            // Free to catch up -> smooth clamped speed step
            var stepped = SpatialWindowAnchor.StepFloatingWorldYaw(20f, 60f, 20f, 1f, 0.1f, ref catching);
            Assert.IsTrue(catching);
            Assert.Greater(stepped, 20f);
            Assert.LessOrEqual(stepped - 20f, 55f * 0.1f + 1e-3f);

            // Settles cleanly within 1.5 deg and clears catchingUp
            var yaw = stepped;
            for (var i = 0; i < 120 && catching; i++)
                yaw = SpatialWindowAnchor.StepFloatingWorldYaw(yaw, 60f, 20f, 1f, 0.05f, ref catching);
            Assert.IsFalse(catching);
            Assert.LessOrEqual(Mathf.Abs(Mathf.DeltaAngle(yaw, 80f)), 1.51f);
        }

        [Test]
        public void FloatingCenterFocus_GlidesGazedAppIntoCenterAndReopensGapOnEmptyGaze()
        {
            var gazeTimes = new float[2];
            var emptyTime = 0f;
            var focused = -1;

            // Brief glance does not pull window 0 into center yet
            focused = SpatialWindowAnchor.StepCenterFocusState(0, focused, gazeTimes, ref emptyTime, 0.30f);
            Assert.AreEqual(-1, focused);

            // Sustained gaze promotes window 0 into center slot
            focused = SpatialWindowAnchor.StepCenterFocusState(0, focused, gazeTimes, ref emptyTime, 0.40f);
            Assert.AreEqual(0, focused);
            Assert.AreEqual(0f, emptyTime, 1e-5f);

            // Gazing window 1 long enough switches center focus to window 1
            focused = SpatialWindowAnchor.StepCenterFocusState(1, focused, gazeTimes, ref emptyTime, 0.35f);
            Assert.AreEqual(0, focused);
            focused = SpatialWindowAnchor.StepCenterFocusState(1, focused, gazeTimes, ref emptyTime, 0.35f);
            Assert.AreEqual(1, focused);

            // Brief look into empty space keeps window 1 centered
            focused = SpatialWindowAnchor.StepCenterFocusState(-1, focused, gazeTimes, ref emptyTime, 0.40f);
            Assert.AreEqual(1, focused);

            // Prolonged look into empty space clears focus and re-opens the center gap
            focused = SpatialWindowAnchor.StepCenterFocusState(-1, focused, gazeTimes, ref emptyTime, 0.50f);
            Assert.AreEqual(-1, focused);
            Assert.AreEqual(0f, emptyTime, 1e-5f);
        }

        [Test]
        public void FloatingSidePark_ClearsCenterCorridorEvenForWideWindows()
        {
            var smallPark = SpatialWindowAnchor.SideParkOffsetDeg(10f);
            Assert.GreaterOrEqual(smallPark - 10f, 13f - 1e-3f);

            var widePark = SpatialWindowAnchor.SideParkOffsetDeg(22f);
            Assert.GreaterOrEqual(widePark - 22f, 13f - 1e-3f);

            var clampedPark = SpatialWindowAnchor.SideParkOffsetDeg(28f);
            Assert.LessOrEqual(clampedPark, 54f + 1e-3f);
        }

        [Test]
        public void SupportPad_TransfersOntoNewAnchorBeforeOldDestroyedAndAlignsToCubeBottom()
        {
            var oldAnchor = new GameObject("OldAnchor");
            var newAnchor = new GameObject("NewAnchor");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                cube.transform.SetParent(newAnchor.transform, false);
                cube.transform.position = new Vector3(1.25f, -0.75f, 0.80f);
                cube.transform.rotation = Quaternion.Euler(25f, 40f, 18f);
                cube.transform.localScale = Vector3.one * 0.1f;

                var oldPad = new GameObject("Surface Support");
                oldPad.layer = 2;
                oldPad.AddComponent<BoxCollider>().size = AnchorPlacer.k_SupportPadSize;
                oldPad.transform.SetParent(oldAnchor.transform, false);

                Physics.SyncTransforms();
                var cubeCol = cube.GetComponent<Collider>();
                var expectedBottomY = cubeCol.bounds.min.y;

                var transferred = AnchorPlacer.TransferOrCreateSupportPad(newAnchor.transform, cube, oldPad, -1f);
                Assert.IsNotNull(transferred);
                Assert.AreSame(newAnchor.transform, transferred.transform.parent);
                Assert.AreEqual(2, transferred.layer);

                // Removing the old anchor must not destroy the transferred support collider.
                Object.DestroyImmediate(oldAnchor);
                Assert.IsTrue(transferred != null);

                Physics.SyncTransforms();
                var padBox = transferred.GetComponent<BoxCollider>();
                Assert.IsTrue(padBox != null && padBox.enabled);
                Assert.AreEqual(AnchorPlacer.k_SupportPadSize, padBox.size);
                Assert.Less(Quaternion.Angle(Quaternion.identity, transferred.transform.rotation), 0.1f);
                Assert.AreEqual(expectedBottomY, padBox.bounds.max.y, 1e-3f);
            }
            finally
            {
                if (oldAnchor != null) Object.DestroyImmediate(oldAnchor);
                if (newAnchor != null) Object.DestroyImmediate(newAnchor);
                if (cube != null) Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void WatchWindowGyroSetting_SwitchesBetweenWatchGyroAndGaze()
        {
            Assert.IsTrue(WatchGrabController.UseWatchGyroForWindows(watchConnected: true, gyroWindowControlEnabled: true));
            Assert.IsFalse(WatchGrabController.UseWatchGyroForWindows(watchConnected: true, gyroWindowControlEnabled: false));
            Assert.IsFalse(WatchGrabController.UseWatchGyroForWindows(watchConnected: false, gyroWindowControlEnabled: true));
            Assert.IsFalse(WatchGrabController.UseWatchGyroForWindows(watchConnected: false, gyroWindowControlEnabled: false));
        }

        [Test]
        public void ClickLockoutAndQuickTap_PreventsDroppedKeyboardAndPhoneAppClicks()
        {
            var headGo = new GameObject("Head");
            var canvasGo = new GameObject("KeyboardCanvas", typeof(RectTransform), typeof(Canvas), typeof(GazeCanvas));
            var btnGo = new GameObject("KeyButton", typeof(RectTransform), typeof(Image), typeof(Button));
            var emptyGo = new GameObject("EmptySurface");
            try
            {
                Assert.LessOrEqual(PinchRouter.LockoutForSource(PinchSource.Watch, btnGo), 0.08f);
                Assert.LessOrEqual(PinchRouter.LockoutForSource(PinchSource.Watch, emptyGo), 0.08f);
                Assert.LessOrEqual(PinchRouter.LockoutForSource(PinchSource.Camera, btnGo), 0.20f);
                Assert.LessOrEqual(PinchRouter.LockoutForSource(PinchSource.Hand, btnGo), 0.20f);

                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var canvasRt = (RectTransform)canvasGo.transform;
                canvasRt.position = new Vector3(0f, 0f, 1.5f);
                canvasRt.rotation = Quaternion.identity;
                canvasRt.sizeDelta = new Vector2(200f, 200f);
                canvasRt.localScale = Vector3.one * 0.002f;

                var btnRt = (RectTransform)btnGo.transform;
                btnRt.SetParent(canvasRt, false);
                btnRt.anchoredPosition = Vector2.zero;
                btnRt.sizeDelta = new Vector2(30f, 30f);

                var clicked = 0;
                var btn = btnGo.GetComponent<Button>();
                btn.targetGraphic = btnGo.GetComponent<Image>();
                btn.onClick.AddListener(() => clicked++);

                GraphicRegistry.RegisterRaycastGraphicForCanvas(canvas, btnGo.GetComponent<Image>());
                Canvas.ForceUpdateCanvases();

                var gaze = headGo.AddComponent<GazePointer>();
                gaze.UpdateGaze();
                Assert.AreSame(btnGo, gaze.Target);

                gaze.TriggerDown();
                // Micro head drift onto gap between keys during a quick tap (within 3.5 deg grace).
                headGo.transform.eulerAngles = new Vector3(0f, 2.0f, 0f);
                gaze.UpdateGaze();
                Assert.IsNull(gaze.Target);
                gaze.TriggerUp();

                Assert.AreEqual(1, clicked, "Quick tap should still fire if gaze drifts briefly onto empty gap.");

                // Deliberately looking far away before release must still cancel the click.
                headGo.transform.eulerAngles = Vector3.zero;
                gaze.UpdateGaze();
                Assert.AreSame(btnGo, gaze.Target);
                gaze.TriggerDown();
                headGo.transform.eulerAngles = new Vector3(0f, 25f, 0f);
                gaze.UpdateGaze();
                Assert.IsNull(gaze.Target);
                gaze.TriggerUp();

                Assert.AreEqual(1, clicked, "Deliberately looking far away before release must still cancel the click.");
            }
            finally
            {
                Object.DestroyImmediate(emptyGo);
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(headGo);
            }
        }

        [Test]
        public void VoiceDictation_FormatsAppendedTextAndAnimatesGlowWhileListening()
        {
            Assert.AreEqual("ciao", VoiceDictation.FormatDictatedAppend("", "  ciao  "));
            Assert.AreEqual(" mondo", VoiceDictation.FormatDictatedAppend("ciao", "mondo"));
            Assert.AreEqual("mondo", VoiceDictation.FormatDictatedAppend("ciao ", "mondo"));
            Assert.AreEqual("", VoiceDictation.FormatDictatedAppend("ciao", "   "));

            var off = VoiceDictation.ComputeButtonVisuals(listening: false, voiceLevel01: 0.9f, timeSec: 1f);
            Assert.AreEqual(0f, off.haloColor.a, 1e-4f);
            Assert.AreEqual(1f, off.haloScale, 1e-4f);

            var on = VoiceDictation.ComputeButtonVisuals(listening: true, voiceLevel01: 0.8f, timeSec: 1f);
            Assert.Greater(on.haloColor.a, 0.3f);
            Assert.Greater(on.haloScale, 1.04f);
        }

        [Test]
        public void PassthroughMovingCameraSetting_DisablesReprojectionWhenFixedToScreen()
        {
            Assert.IsTrue(PassthroughController.ShouldReprojectCamera(sixDof: true, isMono: false, movingCamera: true));
            Assert.IsFalse(PassthroughController.ShouldReprojectCamera(sixDof: true, isMono: false, movingCamera: false));
            Assert.IsFalse(PassthroughController.ShouldReprojectCamera(sixDof: false, isMono: false, movingCamera: true));
            Assert.IsFalse(PassthroughController.ShouldReprojectCamera(sixDof: true, isMono: true, movingCamera: true));
        }

        [Test]
        public void WatchRotary_ScrollsLookedAtScreenAndNeverResizes()
        {
            WatchGrabController.Release();
            var root = new GameObject("ScrollParent", typeof(RectTransform));
            var child = new GameObject("ScrollChild", typeof(RectTransform));
            var srGo = new GameObject("ScrollView", typeof(RectTransform), typeof(ScrollRect));
            var contentGo = new GameObject("Content", typeof(RectTransform));
            var headGo = new GameObject("Head");
            var canvasGo = new GameObject("FloatingWindow", typeof(RectTransform), typeof(Canvas), typeof(GazeCanvas));
            try
            {
                child.transform.SetParent(root.transform, false);
                var stub = root.AddComponent<StubWatchScrollable>();
                Assert.IsTrue(PinchRouter.TryScrollTarget(child, 0.5f));
                Assert.AreEqual(0.5f, stub.LastSteps, 1e-4f);

                var srRect = (RectTransform)srGo.transform;
                srRect.sizeDelta = new Vector2(100f, 200f);
                var contentRect = (RectTransform)contentGo.transform;
                contentRect.SetParent(srRect, false);
                contentRect.sizeDelta = new Vector2(100f, 500f);
                contentRect.anchoredPosition = Vector2.zero;
                var sr = srGo.GetComponent<ScrollRect>();
                sr.viewport = srRect;
                sr.content = contentRect;
                sr.vertical = true;
                sr.horizontal = false;

                Assert.IsTrue(PinchRouter.TryScrollTarget(contentGo, 0.5f));
                Assert.AreEqual(70f, contentRect.anchoredPosition.y, 1e-2f);
                Assert.IsTrue(PinchRouter.TryScrollTarget(contentGo, -2f));
                Assert.AreEqual(0f, contentRect.anchoredPosition.y, 1e-2f);

                var initialScale = Vector3.one * 0.62f;
                canvasGo.transform.localScale = initialScale;
                Assert.IsFalse(WatchGrabController.TryHandleRotary(1f, headGo.transform));
                Assert.IsFalse(PinchRouter.TryScrollTarget(canvasGo, 1f));
                Assert.AreEqual(initialScale, canvasGo.transform.localScale, "Crown must never resize windows.");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(headGo);
                Object.DestroyImmediate(srGo);
                Object.DestroyImmediate(root);
            }
        }

        sealed class StubWatchScrollable : MonoBehaviour, IWatchScrollable
        {
            public float LastSteps { get; private set; }

            public bool TryHandleWatchScroll(float rotarySteps)
            {
                LastSteps = rotarySteps;
                return true;
            }
        }

        [Test]
        public void RoomBoundary_SuppressesPlaneDetectionAndCrossesWhenScanDisabled()
        {
            Assert.IsFalse(RoomBoundary.ShouldRunPlaneDetection(
                sixDof: true, roomScanEnabled: false, scanning: true, continuousPlacement: true, showAll: false));
            Assert.IsFalse(RoomBoundary.ShouldShowScanGrid(
                roomScanEnabled: false, showAll: false, scanning: true, continuousPlacement: false, manualGrid: true));

            Assert.IsTrue(RoomBoundary.ShouldRunPlaneDetection(
                sixDof: true, roomScanEnabled: false, scanning: false, continuousPlacement: false, showAll: true));
            Assert.IsTrue(RoomBoundary.ShouldShowScanGrid(
                roomScanEnabled: false, showAll: true, scanning: false, continuousPlacement: false, manualGrid: false));
            Assert.IsTrue(RoomBoundary.ShouldRunPlaneDetection(
                sixDof: true, roomScanEnabled: true, scanning: false, continuousPlacement: true, showAll: false));
            Assert.IsTrue(RoomBoundary.ShouldShowScanGrid(
                roomScanEnabled: true, showAll: false, scanning: true, continuousPlacement: false, manualGrid: false));
        }

        [Test]
        public void EnvironmentBubblePicker_UsesVisionPro454HoneycombAndSwitchesWithoutActivatingFirst()
        {
            var rows = AppLibrary.EnvironmentRowCounts(13);
            CollectionAssert.AreEqual(new[] { 4, 5, 4 }, rows);

            var gridSize = AppLibrary.EnvironmentGridSize(13);
            var positions = new Vector2[13];
            for (var i = 0; i < 13; i++)
            {
                positions[i] = AppLibrary.EnvironmentBubblePosition(i, 13);
                Assert.LessOrEqual(Mathf.Abs(positions[i].x) + AppLibrary.k_EnvCellWidth * 0.5f,
                    gridSize.x * 0.5f - 15.9f, $"Bubble {i} overflowed horizontally.");
                Assert.LessOrEqual(Mathf.Abs(positions[i].y) + AppLibrary.k_EnvCellHeight * 0.5f,
                    gridSize.y * 0.5f - 15.9f, $"Bubble {i} overflowed vertically.");
            }

            // Center-row middle bubble stays centered, and no two circular bubbles overlap.
            Assert.AreEqual(Vector2.zero, positions[6]);
            for (var i = 0; i < 13; i++)
            {
                for (var j = i + 1; j < 13; j++)
                {
                    Assert.GreaterOrEqual(Vector2.Distance(positions[i], positions[j]), AppLibrary.k_EnvBubbleSize,
                        $"Bubbles {i} and {j} overlapped.");
                }
            }

            // Clicking Ambienti opens the picker instead of entering 360 isolation immediately.
            var envHost = new GameObject("EnvHost");
            var pickerToggleCount = 0;
            void OnToggle() => pickerToggleCount++;
            try
            {
                SettingsPanelHub.EnvironmentsToggleRequested += OnToggle;
                var env = envHost.AddComponent<EnvironmentManager>();
                env.EnsureInitialized();
                Assert.IsFalse(EnvironmentManager.IsIsolated);

                env.OpenPicker();
                Assert.AreEqual(1, pickerToggleCount);
                Assert.IsFalse(EnvironmentManager.IsIsolated,
                    "Ambienti button should show the bubble picker first before activating an environment.");
            }
            finally
            {
                SettingsPanelHub.EnvironmentsToggleRequested -= OnToggle;
                Object.DestroyImmediate(envHost);
            }
        }

        static void AssertPairwiseClearance(SpatialWindowAnchor.FloatingSlotInput[] inputs, float[] resolved)
        {
            for (var i = 0; i < inputs.Length; i++)
            {
                for (var j = i + 1; j < inputs.Length; j++)
                {
                    var minSep = Mathf.Clamp(inputs[i].HalfWidthDeg, 6f, 28f) + 5f +
                                 Mathf.Clamp(inputs[j].HalfWidthDeg, 6f, 28f);
                    Assert.GreaterOrEqual(Mathf.Abs(resolved[i] - resolved[j]), minSep - 1e-3f,
                        $"Slots {i} ({resolved[i]:F1}) and {j} ({resolved[j]:F1}) overlapped.");
                }
            }
        }

        static List<Vector3> BuildHand3D(Quaternion rot)
        {
            var pivot = new Vector3(0f, 0f, 0.40f);
            var local = new Vector3[HandGeometry.JointCount];
            local[0] = new Vector3(0f, -0.07f, 0f);
            local[5] = new Vector3(-0.035f, 0.01f, 0f);
            local[6] = new Vector3(-0.035f, 0.055f, 0f);
            local[9] = new Vector3(0f, 0.015f, 0f);
            local[10] = new Vector3(0f, 0.065f, 0f);
            local[14] = new Vector3(0.025f, 0.060f, 0f);
            local[17] = new Vector3(0.040f, 0.005f, 0f);

            var list = new List<Vector3>(HandGeometry.JointCount);
            for (var i = 0; i < HandGeometry.JointCount; i++)
                list.Add(pivot + rot * local[i]);
            return list;
        }

        static List<Vector2> BuildHand2D(float clockwiseDeg)
        {
            var center = new Vector2(320f, 240f);
            var rad = clockwiseDeg * Mathf.Deg2Rad;
            var cos = Mathf.Cos(rad);
            var sin = Mathf.Sin(rad);
            Vector2 Rot(Vector2 v) => center + new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);

            var list = new List<Vector2>(HandGeometry.JointCount);
            for (var i = 0; i < HandGeometry.JointCount; i++)
                list.Add(center);
            // Image coordinates have +Y downward, so wrist is positive Y and fingers are negative Y.
            list[0] = Rot(new Vector2(0f, 60f));
            list[5] = Rot(new Vector2(-24f, -5f));
            list[6] = Rot(new Vector2(-24f, -45f));
            list[9] = Rot(new Vector2(0f, -8f));
            list[10] = Rot(new Vector2(0f, -52f));
            list[14] = Rot(new Vector2(20f, -48f));
            list[17] = Rot(new Vector2(32f, -4f));
            return list;
        }
    }
}
