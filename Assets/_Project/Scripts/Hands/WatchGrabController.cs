using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace CardboardXR.Hands
{
    // Lets the user grab any floating window (BrowserWindow, Dock, Keyboard, Settings, Lobby panels)
    // or 3D object (PlacedObject / XRGrabInteractable) by double-clicking with the Pixel Watch
    // (two single pinches in 1x mode, or two consecutive double-pinches in 2x mode), move it smoothly
    // in 3D space using the watch's gyroscope (and crown for distance), and release it with one pinch.
    // Logs [WatchGrab].
    public static class WatchGrabController
    {
        public const float k_DoubleClickMinSeconds = 0.30f;
        public const float k_DoubleClickMaxSeconds = 1.65f;
        const float k_ReleaseGuardSeconds = 0.85f;
        const float k_ResizeReleaseGuardSeconds = 0.35f;
        const float k_PostReleaseCooldownSeconds = 0.55f;
        const float k_YawGain = 1.85f;
        const float k_PitchGain = 1.85f;
        const float k_ResizeDegSensitivity = 0.026f;
        const float k_MinDistance = 0.55f;
        const float k_MaxDistance = 3.60f;

        enum TargetKind
        {
            None,
            SettingsPanel,
            Canvas,
            WorldObject,
        }

        struct Grabbable
        {
            public TargetKind Kind;
            public Transform Transform;

            public bool IsValid => Kind == TargetKind.SettingsPanel
                ? SettingsSummoner.IsPanelOpen
                : Kind != TargetKind.None && Transform != null && Transform.gameObject.activeInHierarchy;

            public bool SameAs(Grabbable other) =>
                Kind == other.Kind && (Kind == TargetKind.SettingsPanel || ReferenceEquals(Transform, other.Transform));
        }

        static Grabbable s_LastClickTarget;
        static float s_LastClickTime = float.NegativeInfinity;
        static float s_LastReleaseTime = float.NegativeInfinity;

        static Grabbable s_Held;
        static float s_GrabStartTime;
        static Vector3 s_LastHeadPosition;
        static float s_YawDeg;
        static float s_PitchDeg;
        static float s_PendingYawDeg;
        static float s_PendingPitchDeg;
        static float s_LastHeadYaw;
        static float s_LastHeadPitch;
        static float s_Distance;
        static Rigidbody s_HeldBody;
        static bool s_WasKinematic;

        static Transform s_ResizePanel;
        static float s_ResizeStartTime;
        static Vector3 s_ResizeStartScale;
        static float s_ResizeStartDiagDist;
        static float s_ResizeAccumDeg;
        static float s_ResizePendingDiagDeg;
        static float s_ResizeLastHeadYaw;
        static float s_ResizeLastHeadPitch;

        public static bool IsGrabbing => s_Held.Kind != TargetKind.None;
        public static bool IsResizing => s_ResizePanel != null;
        public static bool IsBusy => IsGrabbing || IsResizing;

        public static bool IsHolding(Transform panel) =>
            s_Held.Kind == TargetKind.Canvas && ReferenceEquals(s_Held.Transform, panel);

        public static bool IsResizingPanel(Transform panel) =>
            s_ResizePanel != null && ReferenceEquals(s_ResizePanel, panel);

        public static bool IsHoldingSettingsPanel => s_Held.Kind == TargetKind.SettingsPanel;

        public static bool UseWatchGyroForWindows(bool watchConnected, bool gyroWindowControlEnabled) =>
            watchConnected && gyroWindowControlEnabled;

        // One-click grab/release from a floating window's Move pill: 1st click grabs, next click anywhere releases.
        public static void ToggleGrabPanel(Transform panel, Transform head, bool isSettingsPanel = false)
        {
            if (IsResizing)
                Release();
            if (IsGrabbing)
            {
                Release();
                return;
            }
            if (head == null)
                return;
            var target = isSettingsPanel
                ? new Grabbable { Kind = TargetKind.SettingsPanel }
                : new Grabbable { Kind = TargetKind.Canvas, Transform = panel };
            if (!target.IsValid)
                return;
            s_LastClickTarget = default;
            s_LastClickTime = float.NegativeInfinity;
            StartGrab(target, head);
        }

        // One-click resize from a floating window's external corner handle:
        // 1st click starts drag-to-resize, 2nd click anywhere / on the corner finishes resizing.
        public static void ToggleResizePanel(Transform panel, Transform head, Ray? pointerRay = null)
        {
            if (IsResizing)
            {
                Release();
                return;
            }
            if (IsGrabbing)
                Release();
            if (panel == null || head == null || !panel.gameObject.activeInHierarchy)
                return;

            s_LastClickTarget = default;
            s_LastClickTime = float.NegativeInfinity;
            s_ResizePanel = panel;
            s_ResizeStartTime = Time.unscaledTime;
            var baseScale = M3Fade.GetBaseScale(panel.gameObject);
            s_ResizeStartScale = baseScale.sqrMagnitude > 1e-9f ? baseScale : panel.localScale;
            s_ResizeAccumDeg = 0f;
            s_ResizePendingDiagDeg = 0f;

            var fwd = head.forward;
            s_ResizeLastHeadYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            s_ResizeLastHeadPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;

            var ray = pointerRay ?? new Ray(head.position, head.forward);
            var plane = new Plane(panel.forward, panel.position);
            if (plane.Raycast(ray, out var hitDist) && hitDist > 0.1f && hitDist < 12f)
            {
                var diag = (panel.right - panel.up).normalized;
                s_ResizeStartDiagDist = Mathf.Max(0.08f, Vector3.Dot(ray.GetPoint(hitDist) - panel.position, diag));
            }
            else if (panel is RectTransform rt)
            {
                var r = rt.rect;
                s_ResizeStartDiagDist = Mathf.Max(0.08f,
                    0.5f * Mathf.Sqrt(r.width * r.width + r.height * r.height) * panel.lossyScale.x * 0.75f);
            }
            else
            {
                s_ResizeStartDiagDist = 0.25f;
            }

            WatchLink.SetGrabbing(WatchLink.GyroWindowControlEnabled);
            Debug.Log($"[WatchGrab] Resizing {panel.name} — drag to resize, click again to finish");
        }

        // Pure uniform clamp helper for corner drag resizing tests.
        public static Vector3 ComputeResizedScale(Vector3 startScale, float factor, Vector3 anchorBaseScale,
            float minMult = 0.40f, float maxMult = 2.40f)
        {
            var mult = Mathf.Clamp((startScale.x * factor) / Mathf.Max(anchorBaseScale.x, 1e-6f), minMult, maxMult);
            return anchorBaseScale * mult;
        }

        // Consumes a single pinch or screen tap to finish dragging/resizing the held window cleanly.
        public static bool TryConsumeRelease()
        {
            if (IsResizing)
            {
                if (Time.unscaledTime - s_ResizeStartTime >= k_ResizeReleaseGuardSeconds)
                {
                    Release();
                    s_LastClickTime = float.NegativeInfinity;
                    s_LastClickTarget = default;
                }
                return true;
            }
            if (!IsGrabbing)
                return false;
            if (Time.unscaledTime - s_GrabStartTime >= k_ReleaseGuardSeconds)
            {
                Release();
                s_LastClickTime = float.NegativeInfinity;
                s_LastClickTarget = default;
            }
            return true;
        }

        // Called on every watch pinch before normal click routing.
        // Returns true if the pinch started a grab (2nd click of a double-click) or released the held object.
        public static bool TryHandleWatchPinch(GazePointer gaze)
        {
            if (TryConsumeRelease())
                return true;

            if (gaze == null)
                return false;

            var now = Time.unscaledTime;
            if (now - s_LastReleaseTime < k_PostReleaseCooldownSeconds)
            {
                s_LastClickTarget = default;
                s_LastClickTime = float.NegativeInfinity;
                return false;
            }
            var candidate = Resolve(gaze.Target);
            if (!candidate.IsValid)
            {
                s_LastClickTarget = default;
                s_LastClickTime = float.NegativeInfinity;
                return false;
            }

            var elapsed = now - s_LastClickTime;
            if (candidate.SameAs(s_LastClickTarget) &&
                elapsed >= k_DoubleClickMinSeconds &&
                elapsed <= k_DoubleClickMaxSeconds)
            {
                s_LastClickTarget = default;
                s_LastClickTime = float.NegativeInfinity;
                StartGrab(candidate, gaze.transform);
                return true;
            }

            s_LastClickTarget = candidate;
            s_LastClickTime = now;
            return false;
        }

        // Called once a frame from PinchInput to move the held window/object or resize the dragged corner.
        public static void Update(GazePointer gaze)
        {
            if (IsResizing)
            {
                UpdateResize(gaze);
                return;
            }
            if (!IsGrabbing)
                return;

            if (!s_Held.IsValid || gaze == null)
            {
                Release();
                return;
            }

            var head = gaze.transform;
            var fwd = head.forward;
            var headYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            var headPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;

            float dYaw;
            float dPitch;
            if (UseWatchGyroForWindows(WatchLink.IsConnected, WatchLink.GyroWindowControlEnabled))
            {
                var gyro = WatchLink.GyroDeltaRad;
                s_PendingYawDeg += gyro.x * Mathf.Rad2Deg * k_YawGain;
                s_PendingPitchDeg += gyro.y * Mathf.Rad2Deg * k_PitchGain;
                var alpha = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 24f);
                dYaw = s_PendingYawDeg * alpha;
                dPitch = s_PendingPitchDeg * alpha;
                s_PendingYawDeg -= dYaw;
                s_PendingPitchDeg -= dPitch;
            }
            else
            {
                s_PendingYawDeg = 0f;
                s_PendingPitchDeg = 0f;
                dYaw = Mathf.DeltaAngle(s_LastHeadYaw, headYaw);
                dPitch = headPitch - s_LastHeadPitch;
            }

            s_LastHeadYaw = headYaw;
            s_LastHeadPitch = headPitch;

            var headPos = head.position;
            var headMoved = (headPos - s_LastHeadPosition).sqrMagnitude >= 1e-8f;
            s_LastHeadPosition = headPos;

            if (!headMoved && Mathf.Abs(dYaw) < 1e-4f && Mathf.Abs(dPitch) < 1e-4f)
                return;

            ApplyDelta(headPos, dYaw, dPitch, 0f);
        }

        static void UpdateResize(GazePointer gaze)
        {
            if (s_ResizePanel == null || !s_ResizePanel.gameObject.activeInHierarchy || gaze == null)
            {
                Release();
                return;
            }

            var head = gaze.transform;
            var fwd = head.forward;
            var headYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            var headPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;
            var dHeadYaw = Mathf.DeltaAngle(s_ResizeLastHeadYaw, headYaw);
            var dHeadPitch = headPitch - s_ResizeLastHeadPitch;
            s_ResizeLastHeadYaw = headYaw;
            s_ResizeLastHeadPitch = headPitch;

            if (UseWatchGyroForWindows(WatchLink.IsConnected, WatchLink.GyroWindowControlEnabled))
            {
                var gyro = WatchLink.GyroDeltaRad;
                var dYaw = gyro.x * Mathf.Rad2Deg * k_YawGain;
                var dPitch = gyro.y * Mathf.Rad2Deg * k_PitchGain;
                s_ResizePendingDiagDeg += (dYaw - dPitch) * 0.70710678f;
                var alpha = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 24f);
                var step = s_ResizePendingDiagDeg * alpha;
                s_ResizePendingDiagDeg -= step;
                s_ResizeAccumDeg += step;
                var factor = Mathf.Exp(s_ResizeAccumDeg * k_ResizeDegSensitivity);
                SpatialWindowAnchor.SetUniformScale(s_ResizePanel, s_ResizeStartScale * factor);
                return;
            }

            var ray = gaze.PointerRay;
            var plane = new Plane(s_ResizePanel.forward, s_ResizePanel.position);
            if (plane.Raycast(ray, out var dist) && dist > 0.1f && dist < 12f)
            {
                var diag = (s_ResizePanel.right - s_ResizePanel.up).normalized;
                var currentDiag = Vector3.Dot(ray.GetPoint(dist) - s_ResizePanel.position, diag);
                var ratio = Mathf.Clamp(currentDiag / Mathf.Max(s_ResizeStartDiagDist, 0.08f), 0.25f, 4.0f);
                var target = s_ResizeStartScale * ratio;
                var current = M3Fade.GetBaseScale(s_ResizePanel.gameObject);
                var smoothed = Vector3.Lerp(current, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 22f));
                SpatialWindowAnchor.SetUniformScale(s_ResizePanel, smoothed);
            }
            else
            {
                s_ResizeAccumDeg += (dHeadYaw - dHeadPitch) * 0.70710678f;
                var factor = Mathf.Exp(s_ResizeAccumDeg * k_ResizeDegSensitivity);
                SpatialWindowAnchor.SetUniformScale(s_ResizePanel, s_ResizeStartScale * factor);
            }
        }

        // Lets the watch crown push/pull ONLY the currently grabbed object closer or farther (never resizes).
        public static bool TryHandleRotary(float rotarySteps, Transform head)
        {
            if (!IsGrabbing || head == null)
                return false;
            if (!s_Held.IsValid)
            {
                Release();
                return false;
            }
            s_LastHeadPosition = head.position;
            ApplyDelta(head.position, 0f, 0f, rotarySteps * 0.18f);
            return true;
        }

        public static void Release()
        {
            if (IsResizing)
            {
                var panel = s_ResizePanel;
                s_ResizePanel = null;
                s_ResizePendingDiagDeg = 0f;
                s_ResizeAccumDeg = 0f;
                s_LastReleaseTime = Time.unscaledTime;
                WatchLink.SetGrabbing(false);
                if (panel != null)
                    Debug.Log($"[WatchGrab] Finished resizing {panel.name}");
            }
            if (!IsGrabbing)
            {
                WatchLink.SetGrabbing(false);
                return;
            }

            Debug.Log($"[WatchGrab] Released {Describe(s_Held)}");
            if (s_Held.Kind == TargetKind.SettingsPanel)
            {
                SettingsSummoner.SetWatchGrabbed(false);
            }
            else if (s_Held.Kind == TargetKind.Canvas && s_Held.Transform != null)
            {
                SpatialWindowAnchor.PinAtCurrentPose(s_Held.Transform);
            }
            else if (s_HeldBody != null)
            {
                s_HeldBody.isKinematic = s_WasKinematic;
                if (!s_WasKinematic)
                {
                    s_HeldBody.linearVelocity = Vector3.zero;
                    s_HeldBody.angularVelocity = Vector3.zero;
                }
            }

            s_Held = default;
            s_HeldBody = null;
            s_WasKinematic = false;
            s_LastReleaseTime = Time.unscaledTime;
            WatchLink.SetGrabbing(false);
        }

        static void StartGrab(Grabbable target, Transform head)
        {
            s_Held = target;
            s_GrabStartTime = Time.unscaledTime;
            s_LastHeadPosition = head.position;
            s_PendingYawDeg = 0f;
            s_PendingPitchDeg = 0f;
            s_HeldBody = null;
            s_WasKinematic = false;

            var fwd = head.forward;
            s_LastHeadYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            s_LastHeadPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;

            if (target.Kind == TargetKind.SettingsPanel)
            {
                s_YawDeg = s_LastHeadYaw;
                s_PitchDeg = s_LastHeadPitch;
                SettingsSummoner.SetWatchGrabbed(true);
            }
            else
            {
                if (target.Kind == TargetKind.Canvas)
                    SpatialWindowAnchor.Suspend(target.Transform);

                var offset = target.Transform.position - head.position;
                s_Distance = Mathf.Clamp(offset.magnitude, k_MinDistance, k_MaxDistance);
                var dir = offset.sqrMagnitude > 1e-4f ? offset.normalized : head.forward;
                s_YawDeg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                s_PitchDeg = Mathf.Asin(Mathf.Clamp(dir.y, -0.99f, 0.99f)) * Mathf.Rad2Deg;

                if (target.Kind == TargetKind.WorldObject)
                {
                    s_HeldBody = target.Transform.GetComponent<Rigidbody>();
                    if (s_HeldBody != null)
                    {
                        s_WasKinematic = s_HeldBody.isKinematic;
                        s_HeldBody.isKinematic = true;
                    }
                }
            }

            WatchLink.SetGrabbing(WatchLink.GyroWindowControlEnabled);
            Debug.Log($"[WatchGrab] Grabbed {Describe(target)} — move wrist to reposition, pinch once to release");
        }

        /// <summary>
        /// Computes the world pose of a dragged panel relative to the user's current head position
        /// so the screen stays at a constant distance while walking. Pure, for testing.
        /// </summary>
        public static Pose ComputeDraggedPose(Vector3 headPosition, float yawDeg, float pitchDeg,
            float distance, bool sphericalFacing)
        {
            var dir = Quaternion.Euler(-pitchDeg, yawDeg, 0f) * Vector3.forward;
            var pos = headPosition + dir * distance;
            var rot = sphericalFacing
                ? M3Theme.SphericalFacingRotation(pos, headPosition)
                : M3Theme.FacingRotation(pos, headPosition);
            return new Pose(pos, rot);
        }

        static void ApplyDelta(Vector3 headPosition, float dYawDeg, float dPitchDeg, float dDistMeters)
        {
            if (s_Held.Kind == TargetKind.SettingsPanel)
            {
                SettingsSummoner.ApplyWatchGrab(headPosition, dYawDeg, dPitchDeg, dDistMeters);
                return;
            }

            s_YawDeg += dYawDeg;
            s_PitchDeg = Mathf.Clamp(s_PitchDeg + dPitchDeg, -60f, 65f);
            s_Distance = Mathf.Clamp(s_Distance + dDistMeters, k_MinDistance, k_MaxDistance);

            if (s_Held.Kind == TargetKind.Canvas)
            {
                // Floating windows sit on a sphere facing your eyes straight-on; anchored windows stay upright (0 deg roll).
                var pose = ComputeDraggedPose(headPosition, s_YawDeg, s_PitchDeg, s_Distance,
                    SpatialWindowAnchor.IsFloating(s_Held.Transform));
                s_Held.Transform.SetPositionAndRotation(pose.position, pose.rotation);
            }
            else if (s_Held.Kind == TargetKind.WorldObject)
            {
                var dir = Quaternion.Euler(-s_PitchDeg, s_YawDeg, 0f) * Vector3.forward;
                s_Held.Transform.position = headPosition + dir * s_Distance;
                if (Mathf.Abs(dYawDeg) > 1e-4f)
                    s_Held.Transform.Rotate(Vector3.up, dYawDeg, Space.World);
            }
        }

        static Grabbable Resolve(GameObject target)
        {
            if (target == null)
                return default;

            // Never grab the look-down/look-up bars themselves, nor UI buttons/interactive surfaces
            // (so rapid clicks on keyboard keys, scroll buttons, zoom buttons, or live screens never grab the panel).
            if (SettingsSummoner.IsLookDownTarget(target) || SettingsSummoner.IsLookUpTarget(target))
                return default;
            if (target.GetComponentInParent<Selectable>() != null ||
                target.GetComponentInParent<UnityEngine.EventSystems.IPointerClickHandler>() != null)
                return default;

            if (SettingsSummoner.IsOnPanel(target))
                return new Grabbable { Kind = TargetKind.SettingsPanel };

            var canvas = target.GetComponentInParent<GazeCanvas>();
            if (canvas != null)
            {
                if (FloatingWindowHandle.IsExcluded(canvas.transform))
                    return default;
                return new Grabbable { Kind = TargetKind.Canvas, Transform = canvas.transform };
            }

            var interactable = target.GetComponentInParent<XRGrabInteractable>();
            if (interactable != null)
                return new Grabbable { Kind = TargetKind.WorldObject, Transform = interactable.transform };

            var placed = target.GetComponentInParent<PlacedObject>();
            if (placed != null)
                return new Grabbable { Kind = TargetKind.WorldObject, Transform = placed.transform };

            return default;
        }

        static string Describe(Grabbable target) => target.Kind switch
        {
            TargetKind.SettingsPanel => "Settings Panel",
            TargetKind.Canvas => target.Transform != null ? target.Transform.name : "Canvas",
            TargetKind.WorldObject => target.Transform != null ? target.Transform.name : "Object",
            _ => "None",
        };
    }
}
