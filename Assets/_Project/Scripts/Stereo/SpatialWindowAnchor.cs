using System.Collections.Generic;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Manages spatial locking, floating follow-me mode, stow-to-sides animation, and in-place resizing
    // for every floating 3D window/panel:
    //   - Anchored (default): pins the window to a real ARCore ARAnchor (+ XR Origin session fallback)
    //     so it stays glued to the exact millimeter in the room like Apple Vision Pro.
    //   - Floating (unpinned via the bottom handle's pin button): parks cleanly to the side clear of
    //     the central gaze axis, keeps multiple floating windows separated without overlapping, and
    //     smoothly slows down and stops whenever the user looks toward or at them.
    //   - Stowed (toggled from the look-down bar): slides all floating windows smoothly out to the
    //     left/right sides and disables their Canvas so fast head turns never flash them back in,
    //     while keeping the GameObject active (audio/WebRTC stays live) until restored.
    [DefaultExecutionOrder(-70)]
    public class SpatialWindowAnchor : MonoBehaviour
    {
        public struct FloatingSlotInput
        {
            public float PreferredYawDeg;
            public float HalfWidthDeg;
            public bool KeepCenterAllowed;
        }

        const float k_CenterClearHalfDeg = 9f;
        const float k_SideClearHalfDeg = 13f;
        const float k_MinSideOffsetDeg = 18f;
        const float k_MaxSideParkDeg = 54f;
        const float k_FovHalfDeg = 39f;
        const float k_WindowGapDeg = 5f;
        const float k_MaxSlotYawDeg = 145f;

        const float k_DeadZoneEnterDeg = 14f;
        const float k_DeadZoneSettledDeg = 1.5f;
        const float k_FollowSmoothRate = 4.2f;
        const float k_MaxFollowSpeedDegPerSec = 55f;
        const float k_SideFollowCalmMultiplier = 0.55f;
        const float k_PosFollowRate = 4.5f;
        const float k_GazedPosDeadZoneMeters = 0.30f;

        const float k_BrakeOuterDeg = 10f;
        const float k_BrakeStopDeg = 1.5f;
        const float k_GazeHoldSeconds = 0.45f;
        const float k_HitPadDeg = 2.5f;
        const float k_DirectGazeToleranceDeg = 0.5f;
        const float k_InitialParkSeconds = 0.65f;
        const float k_ReleaseParkSeconds = 0.55f;
        const float k_SlotSmoothRate = 7.5f;
        const float k_GainRecoverRate = 3.5f;
        public const float k_GazeFocusPromoteSeconds = 0.65f;
        public const float k_EmptyGazePartSeconds = 0.85f;
        const float k_CenterFocusGlideRate = 4.8f;

        const float k_StowDurationSeconds = 0.26f;
        const float k_StowSideYawDeg = 72f;
        const float k_ResizeGraceSeconds = 0.50f;
        const float k_MinScaleFactor = 0.40f;
        const float k_MaxScaleFactor = 2.40f;
        const float k_ScaleStepPerRotary = 0.085f;

        static readonly List<SpatialWindowAnchor> s_All = new();
        static readonly List<SpatialWindowAnchor> s_FloatingBuffer = new();
        static readonly List<FloatingSlotInput> s_SlotInputs = new();
        static readonly List<float> s_ContinuousGazeBuffer = new();
        static bool s_FloatingStowed;
        static SpatialWindowAnchor s_CenterFocusedWindow;
        static float s_EmptyGazeDuration;
        static float s_CenterAnchorHeadYaw;

        static bool s_HasGazeRay;
        static Ray s_GazeRay;
        static GameObject s_GazeTarget;

        static int s_LastClusterFrame = -1;
        static float s_SmoothedClusterGain = 1f;

        ARAnchor m_Anchor;
        Canvas m_Canvas;
        CanvasGroup m_Group;
        Vector3 m_SessionPos;
        Quaternion m_SessionRot = Quaternion.identity;
        bool m_HasSessionPose;
        bool m_Suspended;
        bool m_NeedsAnchor;
        bool m_PinInFlight;
        float m_PendingPinAt;
        int m_PinVersion;

        // Floating follow-me state (relative to head).
        bool m_Floating;
        float m_FloatPitchOffset;
        float m_FloatDistance = 1.85f;
        float m_PreferredYawOffset;
        float m_ResolvedYawOffset;
        float m_CurrentYawOffset;
        float m_WorldYaw;
        bool m_CatchingUp;
        bool m_UserPositioned;
        float m_InitialParkUntil;
        float m_ParkAnchorHeadYaw;
        float m_LastDirectGazeTime = float.NegativeInfinity;
        float m_ContinuousGazeDuration;
        float m_WindowBrakeGain = 1f;
        Vector3 m_SmoothFollowPos;
        bool m_HasFollowState;

        // Stow animation progress: 0 = visible around user, 1 = completely hidden off to the side.
        float m_StowProgress;
        float m_StowSide = 1f;

        // In-place Watch crown scale baseline.
        bool m_HasBaseScale;
        Vector3 m_BaseScale = Vector3.one;
        float m_ScaleMultiplier = 1f;

        float m_LastResizeTime = float.NegativeInfinity;

        public static IReadOnlyList<SpatialWindowAnchor> All => s_All;

        public bool IsWindowFloating => m_Floating;
        public bool IsSuspended => m_Suspended;
        public bool IsBeingResized => Time.unscaledTime - m_LastResizeTime < k_ResizeGraceSeconds;

        public static bool AreFloatingStowed => s_FloatingStowed;

        public static bool HasAnyFloating
        {
            get
            {
                for (var i = 0; i < s_All.Count; i++)
                {
                    var a = s_All[i];
                    if (a != null && a.gameObject.activeInHierarchy && a.m_Floating)
                        return true;
                }
                return false;
            }
        }

        public static void SetActiveGaze(Ray pointerRay, GameObject target)
        {
            s_HasGazeRay = true;
            s_GazeRay = pointerRay;
            s_GazeTarget = target;
        }

        public static void ClearActiveGaze()
        {
            s_HasGazeRay = false;
            s_GazeTarget = null;
        }

        // Preferred lateral yaw magnitude that clears the central gaze cone while remaining inside FOV.
        public static float IdealSideOffsetDeg(float halfWidthDeg)
        {
            var hw = Mathf.Clamp(halfWidthDeg, 6f, 28f);
            var clearCenter = k_CenterClearHalfDeg + hw;
            var maxInFov = Mathf.Max(k_MinSideOffsetDeg, k_FovHalfDeg - hw);
            return Mathf.Clamp(clearCenter, k_MinSideOffsetDeg, maxInFov);
        }

        // Lateral yaw magnitude that always leaves a clear central corridor even for wide windows.
        public static float SideParkOffsetDeg(float halfWidthDeg)
        {
            var hw = Mathf.Clamp(halfWidthDeg, 6f, 30f);
            return Mathf.Clamp(k_SideClearHalfDeg + hw, k_MinSideOffsetDeg, k_MaxSideParkDeg);
        }

        // Resolves non-overlapping horizontal offsets for any set of active floating windows.
        public static float[] ResolveFloatingYawSlots(IReadOnlyList<FloatingSlotInput> windows)
        {
            var count = windows?.Count ?? 0;
            var result = new float[count];
            if (count == 0)
                return result;

            var spans = new float[count];
            var right = new List<int>(count);
            var left = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                spans[i] = Mathf.Clamp(windows[i].HalfWidthDeg, 6f, 28f);
                if (windows[i].PreferredYawDeg >= 0f)
                    right.Add(i);
                else
                    left.Add(i);
            }

            right.Sort((a, b) => windows[a].PreferredYawDeg.CompareTo(windows[b].PreferredYawDeg));
            for (var r = 0; r < right.Count; r++)
            {
                var idx = right[r];
                var w = windows[idx];
                var span = spans[idx];
                if (r == 0)
                {
                    result[idx] = w.KeepCenterAllowed
                        ? w.PreferredYawDeg
                        : Mathf.Max(w.PreferredYawDeg, IdealSideOffsetDeg(span));
                }
                else
                {
                    var prev = right[r - 1];
                    result[idx] = Mathf.Max(w.PreferredYawDeg, result[prev] + spans[prev] + k_WindowGapDeg + span);
                }
            }

            left.Sort((a, b) => windows[b].PreferredYawDeg.CompareTo(windows[a].PreferredYawDeg));
            for (var l = 0; l < left.Count; l++)
            {
                var idx = left[l];
                var w = windows[idx];
                var span = spans[idx];
                if (l == 0)
                {
                    result[idx] = w.KeepCenterAllowed
                        ? w.PreferredYawDeg
                        : Mathf.Min(w.PreferredYawDeg, -IdealSideOffsetDeg(span));
                }
                else
                {
                    var prev = left[l - 1];
                    result[idx] = Mathf.Min(w.PreferredYawDeg, result[prev] - spans[prev] - k_WindowGapDeg - span);
                }
            }

            if (count > 1)
            {
                var order = new int[count];
                for (var i = 0; i < count; i++)
                    order[i] = i;
                System.Array.Sort(order, (a, b) => result[a].CompareTo(result[b]));
                for (var i = 1; i < count; i++)
                {
                    var prev = order[i - 1];
                    var curr = order[i];
                    var required = spans[prev] + k_WindowGapDeg + spans[curr];
                    if (result[curr] - result[prev] < required)
                        result[curr] = result[prev] + required;
                }
            }

            for (var i = 0; i < count; i++)
                result[i] = Mathf.Clamp(result[i], -k_MaxSlotYawDeg, k_MaxSlotYawDeg);
            return result;
        }

        // Smoothstep gaze brake: 0 when gazing at or immediately after leaving a window, 1 when well away.
        public static float ComputeGazeBrakeFactor(bool isDirectTarget, float edgeDistDeg, float timeSinceDirectGaze)
        {
            if (isDirectTarget || timeSinceDirectGaze < k_GazeHoldSeconds || edgeDistDeg <= k_BrakeStopDeg)
                return 0f;
            if (edgeDistDeg >= k_BrakeOuterDeg)
                return 1f;
            var t = (edgeDistDeg - k_BrakeStopDeg) / (k_BrakeOuterDeg - k_BrakeStopDeg);
            return t * t * (3f - 2f * t);
        }

        // Updates world yaw smoothly with hysteresis deadzone, speed clamp, and complete pause while gazed.
        public static float StepFloatingWorldYaw(float currentWorldYaw, float headYaw, float slotYawOffset,
            float followGain, float dt, ref bool catchingUp)
        {
            if (followGain <= 0.001f)
            {
                catchingUp = false;
                return currentWorldYaw;
            }
            var target = headYaw + slotYawOffset;
            var err = Mathf.DeltaAngle(currentWorldYaw, target);
            var absErr = Mathf.Abs(err);
            if (!catchingUp)
            {
                if (absErr <= k_DeadZoneEnterDeg)
                    return currentWorldYaw;
                catchingUp = true;
            }
            else if (absErr <= k_DeadZoneSettledDeg)
            {
                catchingUp = false;
                return currentWorldYaw;
            }

            var rate = k_FollowSmoothRate * followGain;
            var maxStep = k_MaxFollowSpeedDegPerSec * followGain * dt;
            var step = Mathf.Clamp(err * (1f - Mathf.Exp(-dt * rate)), -maxStep, maxStep);
            return currentWorldYaw + step;
        }

        // Tracks continuous gaze on a floating app to glide it into the center gap, and tracks
        // looking into empty space to re-open the center gap between the side apps.
        public static int StepCenterFocusState(int gazedIndex, int currentFocusedIndex,
            float[] continuousGazeTimes, ref float emptyGazeDuration, float dt)
        {
            return StepCenterFocusInternal(gazedIndex, currentFocusedIndex, continuousGazeTimes,
                continuousGazeTimes?.Length ?? 0, ref emptyGazeDuration, dt);
        }

        static int StepCenterFocusInternal(int gazedIndex, int currentFocusedIndex,
            IList<float> continuousGazeTimes, int count, ref float emptyGazeDuration, float dt)
        {
            var safeDt = Mathf.Max(0f, dt);
            if (continuousGazeTimes != null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (i == gazedIndex)
                        continuousGazeTimes[i] += safeDt;
                    else
                        continuousGazeTimes[i] = Mathf.MoveTowards(continuousGazeTimes[i], 0f, safeDt * 3f);
                }
            }

            if (gazedIndex >= 0 && continuousGazeTimes != null && gazedIndex < count)
            {
                emptyGazeDuration = 0f;
                if (gazedIndex != currentFocusedIndex && continuousGazeTimes[gazedIndex] >= k_GazeFocusPromoteSeconds)
                    return gazedIndex;
                return currentFocusedIndex;
            }

            if (currentFocusedIndex >= 0)
            {
                emptyGazeDuration += safeDt;
                if (emptyGazeDuration >= k_EmptyGazePartSeconds)
                {
                    emptyGazeDuration = 0f;
                    return -1;
                }
            }
            else
            {
                emptyGazeDuration = 0f;
            }
            return currentFocusedIndex;
        }

        public static bool IsFloating(Transform target)
        {
            if (target == null)
                return false;
            var a = target.GetComponent<SpatialWindowAnchor>();
            return a != null && a.m_Floating;
        }

        public static bool IsStowed(Transform target)
        {
            if (target == null)
                return false;
            var a = target.GetComponent<SpatialWindowAnchor>();
            return a != null && a.m_Floating && (s_FloatingStowed || a.m_StowProgress > 0.05f);
        }

        // Attaches (if needed) and pins this floating window at its current world pose in ARCore space
        // (unless it is currently in Floating follow-me mode, in which case it updates its manual slot).
        public static void PinAtCurrentPose(Transform target, float delaySeconds = 0f)
        {
            if (target == null)
                return;
            var anchor = GetOrAdd(target);
            if (anchor.m_Floating)
            {
                var placer = AnchorPlacer.Instance;
                var head = placer != null && placer.Fusion != null ? placer.Fusion.transform : null;
                anchor.CaptureFloatingOffset(head, isInitialPin: false);
                anchor.m_Suspended = false;
                return;
            }
            anchor.Pin(delaySeconds);
        }

        // Toggles between room-anchored (ARCore ARAnchor) and floating follow-me mode.
        public static bool ToggleFloating(Transform target, Transform head)
        {
            if (target == null)
                return false;
            var anchor = GetOrAdd(target);
            var next = !anchor.m_Floating;
            SetFloating(target, next, head);
            return next;
        }

        public static void SetFloating(Transform target, bool floating, Transform head)
        {
            if (target == null)
                return;
            var anchor = GetOrAdd(target);
            if (anchor.m_Floating == floating)
                return;

            anchor.m_Floating = floating;
            if (floating)
            {
                anchor.m_PinVersion++;
                anchor.m_NeedsAnchor = false;
                anchor.m_HasSessionPose = false;
                anchor.ReleaseAnchor();
                anchor.m_StowProgress = 0f;
                anchor.RestoreVisualState();
                // If floating windows were hidden to the sides, bring them back when unpinning one.
                if (s_FloatingStowed)
                    SetFloatingStowed(false);
                anchor.CaptureFloatingOffset(head, isInitialPin: true);
                Debug.Log($"[SpatialAnchor] '{target.name}' -> Fluttuante laterale (segue lo sguardo)");
            }
            else
            {
                if (s_CenterFocusedWindow == anchor)
                {
                    s_CenterFocusedWindow = null;
                    s_EmptyGazeDuration = 0f;
                }
                anchor.m_ContinuousGazeDuration = 0f;
                anchor.m_StowProgress = 0f;
                anchor.RestoreVisualState();
                anchor.m_HasFollowState = false;
                anchor.m_InitialParkUntil = 0f;
                anchor.m_CatchingUp = false;
                anchor.m_UserPositioned = false;
                anchor.Pin(0f);
                RebalanceFloatingSlots();
                Debug.Log($"[SpatialAnchor] '{target.name}' -> Ancorata nello spazio ARCore");
            }
        }

        // Updates a floating window's relative offset after being moved via WatchGrabController.
        public static void RefreshFloatingOffset(Transform target, Transform head)
        {
            if (target == null)
                return;
            var anchor = target.GetComponent<SpatialWindowAnchor>();
            if (anchor == null)
                return;
            anchor.m_Suspended = false;
            if (anchor.m_Floating)
                anchor.CaptureFloatingOffset(head, isInitialPin: false);
            else
                anchor.Pin(0f);
        }

        // Slides all floating windows smoothly off to the left/right sides (stowed = true) and hides
        // their Canvas so rapid head movement never flashes them back, or slides them back in (false).
        public static void ToggleFloatingStowed() => SetFloatingStowed(!s_FloatingStowed);

        public static void SetFloatingStowed(bool stowed)
        {
            s_FloatingStowed = stowed;
            s_EmptyGazeDuration = 0f;
            if (stowed)
                s_CenterFocusedWindow = null;
            else
                RebalanceFloatingSlots();
            var placer = AnchorPlacer.Instance;
            var head = placer != null && placer.Fusion != null ? placer.Fusion.transform : null;
            var headYaw = head != null ? HeadYaw(head) : 0f;
            var headPos = head != null ? head.position : Vector3.zero;

            for (var i = 0; i < s_All.Count; i++)
            {
                var a = s_All[i];
                if (a == null || !a.m_Floating)
                    continue;
                if (stowed)
                {
                    a.m_StowSide = a.m_ResolvedYawOffset < -1f ? -1f : 1f;
                }
                else
                {
                    // Re-enable Canvas right at the user's current heading and glide back in from the side.
                    a.RestoreVisualState();
                    a.m_ContinuousGazeDuration = 0f;
                    if (head != null)
                    {
                        a.m_CurrentYawOffset = a.m_ResolvedYawOffset;
                        a.m_WorldYaw = headYaw + a.m_CurrentYawOffset;
                        a.m_CatchingUp = false;
                        a.m_SmoothFollowPos = headPos;
                        a.m_HasFollowState = true;
                    }
                }
            }
            Debug.Log($"[SpatialAnchor] Floating windows {(stowed ? "stowed to sides" : "restored")}");
        }

        // Temporarily suspends spatial locking/following while the user is actively dragging the window.
        public static void Suspend(Transform target)
        {
            if (target == null)
                return;
            var anchor = target.GetComponent<SpatialWindowAnchor>();
            if (anchor != null)
                anchor.SuspendInternal();
        }

        static void EnsureBaseScale(SpatialWindowAnchor anchor, Transform target)
        {
            if (anchor.m_HasBaseScale)
                return;
            var baseScale = M3Fade.GetBaseScale(target.gameObject);
            anchor.m_BaseScale = baseScale.sqrMagnitude > 1e-9f ? baseScale : target.localScale;
            anchor.m_ScaleMultiplier = 1f;
            anchor.m_HasBaseScale = true;
        }

        public static Vector3 GetAnchorBaseScale(Transform target)
        {
            if (target == null)
                return Vector3.one;
            var anchor = GetOrAdd(target);
            EnsureBaseScale(anchor, target);
            return anchor.m_BaseScale;
        }

        // Resizes any window uniformly in place (staying anchored in space) when turning the Watch crown.
        public static bool AdjustScale(Transform target, float rotarySteps)
        {
            if (target == null || Mathf.Abs(rotarySteps) < 1e-4f)
                return false;
            var anchor = GetOrAdd(target);
            EnsureBaseScale(anchor, target);
            var factor = Mathf.Exp(rotarySteps * k_ScaleStepPerRotary);
            anchor.m_ScaleMultiplier = Mathf.Clamp(anchor.m_ScaleMultiplier * factor, k_MinScaleFactor, k_MaxScaleFactor);
            var scaled = anchor.m_BaseScale * anchor.m_ScaleMultiplier;
            target.localScale = scaled;
            M3Fade.SetBaseScale(target.gameObject, scaled);
            anchor.m_LastResizeTime = Time.unscaledTime;
            return true;
        }

        // Sets a window's uniform scale directly during click-and-drag corner resizing, clamped to safe bounds.
        public static bool SetUniformScale(Transform target, Vector3 desiredScale)
        {
            if (target == null)
                return false;
            var anchor = GetOrAdd(target);
            EnsureBaseScale(anchor, target);
            var mult = Mathf.Clamp(desiredScale.x / Mathf.Max(anchor.m_BaseScale.x, 1e-6f),
                k_MinScaleFactor, k_MaxScaleFactor);
            anchor.m_ScaleMultiplier = mult;
            var clamped = anchor.m_BaseScale * mult;
            target.localScale = clamped;
            M3Fade.SetBaseScale(target.gameObject, clamped);
            anchor.m_LastResizeTime = Time.unscaledTime;
            return true;
        }

        // Updates the stored base scale when a window's built-in Cinema button toggles size.
        public static void SyncBaseScale(Transform target)
        {
            if (target == null)
                return;
            var anchor = target.GetComponent<SpatialWindowAnchor>();
            if (anchor != null)
            {
                anchor.m_BaseScale = M3Fade.GetBaseScale(target.gameObject);
                if (anchor.m_BaseScale.sqrMagnitude < 1e-9f)
                    anchor.m_BaseScale = target.localScale;
                anchor.m_ScaleMultiplier = 1f;
                anchor.m_HasBaseScale = true;
            }
            M3Fade.SyncBaseScale(target.gameObject);
        }

        static SpatialWindowAnchor GetOrAdd(Transform target)
        {
            var anchor = target.GetComponent<SpatialWindowAnchor>();
            if (anchor == null)
                anchor = target.gameObject.AddComponent<SpatialWindowAnchor>();
            return anchor;
        }

        void Awake()
        {
            m_Canvas = GetComponent<Canvas>();
            m_Group = GetComponent<CanvasGroup>();
            if (!s_All.Contains(this))
                s_All.Add(this);
        }

        void OnDisable()
        {
            if (s_CenterFocusedWindow == this)
            {
                s_CenterFocusedWindow = null;
                s_EmptyGazeDuration = 0f;
            }
            m_ContinuousGazeDuration = 0f;
            if (m_Floating)
                RebalanceFloatingSlots();
        }

        void SuspendInternal()
        {
            m_Suspended = true;
            m_NeedsAnchor = false;
            m_HasSessionPose = false;
            m_PinVersion++;
            ReleaseAnchor();
        }

        void Pin(float delaySeconds)
        {
            m_Suspended = false;
            m_PinVersion++;
            ReleaseAnchor();

            var placer = AnchorPlacer.Instance;
            if (placer != null && placer.Origin != null &&
                placer.Fusion != null && placer.Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
            {
                var origin = placer.Origin;
                m_SessionPos = origin.InverseTransformPoint(transform.position);
                m_SessionRot = Quaternion.Inverse(origin.rotation) * transform.rotation;
                m_HasSessionPose = true;
            }
            else
            {
                m_HasSessionPose = false;
            }

            m_NeedsAnchor = true;
            m_PendingPinAt = Time.unscaledTime + Mathf.Max(0f, delaySeconds);
        }

        void ComputeAngularHalfExtents(float distance, out float halfWidthDeg, out float halfHeightDeg)
        {
            var safeDist = Mathf.Max(0.45f, distance);
            if (transform is RectTransform rt)
            {
                var r = rt.rect;
                var s = transform.lossyScale;
                var halfW = Mathf.Abs(r.width * 0.5f * s.x);
                var halfH = Mathf.Abs(r.height * 0.5f * s.y);
                if (halfW > 1e-4f && halfH > 1e-4f)
                {
                    halfWidthDeg = Mathf.Atan2(halfW, safeDist) * Mathf.Rad2Deg;
                    halfHeightDeg = Mathf.Atan2(halfH, safeDist) * Mathf.Rad2Deg + 3.5f;
                    return;
                }
            }
            halfWidthDeg = 14f;
            halfHeightDeg = 14.5f;
        }

        static float PickBalancedSideSign(SpatialWindowAnchor exclude)
        {
            var rightCount = 0;
            var leftCount = 0;
            for (var i = 0; i < s_All.Count; i++)
            {
                var a = s_All[i];
                if (a == null || a == exclude || !a.m_Floating || a.m_Suspended || !a.gameObject.activeInHierarchy)
                    continue;
                if (a.m_ResolvedYawOffset >= 0f)
                    rightCount++;
                else
                    leftCount++;
            }
            return leftCount < rightCount ? -1f : 1f;
        }

        static void RebalanceFloatingSlots()
        {
            s_FloatingBuffer.Clear();
            s_SlotInputs.Clear();

            if (s_CenterFocusedWindow != null &&
                (!s_CenterFocusedWindow.m_Floating || s_CenterFocusedWindow.m_Suspended ||
                 !s_CenterFocusedWindow.gameObject.activeInHierarchy))
            {
                s_CenterFocusedWindow = null;
                s_EmptyGazeDuration = 0f;
            }

            var centerIdx = -1;
            for (var i = 0; i < s_All.Count; i++)
            {
                var a = s_All[i];
                if (a == null || !a.m_Floating || a.m_Suspended || !a.gameObject.activeInHierarchy)
                    continue;
                a.ComputeAngularHalfExtents(a.m_FloatDistance, out var halfW, out _);
                var isCenterFocused = a == s_CenterFocusedWindow;
                if (isCenterFocused)
                    centerIdx = s_FloatingBuffer.Count;
                s_FloatingBuffer.Add(a);
                float preferredYaw;
                if (isCenterFocused)
                {
                    preferredYaw = 0f;
                }
                else
                {
                    var minSide = SideParkOffsetDeg(halfW);
                    var side = Mathf.Abs(a.m_PreferredYawOffset) > 0.5f
                        ? Mathf.Sign(a.m_PreferredYawOffset)
                        : (a.m_CurrentYawOffset < -0.5f ? -1f : 1f);
                    preferredYaw = side * Mathf.Max(Mathf.Abs(a.m_PreferredYawOffset), minSide);
                }
                s_SlotInputs.Add(new FloatingSlotInput
                {
                    PreferredYawDeg = preferredYaw,
                    HalfWidthDeg = halfW,
                    KeepCenterAllowed = isCenterFocused,
                });
            }

            if (s_FloatingBuffer.Count == 0)
                return;

            var resolved = ResolveFloatingYawSlots(s_SlotInputs);
            if (centerIdx >= 0)
            {
                resolved[centerIdx] = 0f;
                var centerSpan = Mathf.Clamp(s_SlotInputs[centerIdx].HalfWidthDeg, 6f, 28f);
                var leftEdge = -centerSpan;
                var rightEdge = centerSpan;

                var leftIndices = new List<int>(s_FloatingBuffer.Count);
                var rightIndices = new List<int>(s_FloatingBuffer.Count);
                for (var i = 0; i < s_FloatingBuffer.Count; i++)
                {
                    if (i == centerIdx)
                        continue;
                    var a = s_FloatingBuffer[i];
                    var pref = Mathf.Abs(a.m_PreferredYawOffset) > 0.5f ? a.m_PreferredYawOffset : a.m_CurrentYawOffset;
                    if (pref < 0f)
                        leftIndices.Add(i);
                    else
                        rightIndices.Add(i);
                }

                leftIndices.Sort((a, b) => resolved[b].CompareTo(resolved[a]));
                for (var k = 0; k < leftIndices.Count; k++)
                {
                    var idx = leftIndices[k];
                    var span = Mathf.Clamp(s_SlotInputs[idx].HalfWidthDeg, 6f, 28f);
                    resolved[idx] = Mathf.Clamp(Mathf.Min(resolved[idx], leftEdge - k_WindowGapDeg - span),
                        -k_MaxSlotYawDeg, k_MaxSlotYawDeg);
                    leftEdge = resolved[idx] - span;
                }

                rightIndices.Sort((a, b) => resolved[a].CompareTo(resolved[b]));
                for (var k = 0; k < rightIndices.Count; k++)
                {
                    var idx = rightIndices[k];
                    var span = Mathf.Clamp(s_SlotInputs[idx].HalfWidthDeg, 6f, 28f);
                    resolved[idx] = Mathf.Clamp(Mathf.Max(resolved[idx], rightEdge + k_WindowGapDeg + span),
                        -k_MaxSlotYawDeg, k_MaxSlotYawDeg);
                    rightEdge = resolved[idx] + span;
                }
            }

            for (var i = 0; i < s_FloatingBuffer.Count; i++)
                s_FloatingBuffer[i].m_ResolvedYawOffset = resolved[i];
        }

        void CaptureFloatingOffset(Transform head, bool isInitialPin)
        {
            if (head == null)
            {
                var placer = AnchorPlacer.Instance;
                head = placer != null && placer.Fusion != null ? placer.Fusion.transform : null;
            }
            if (head == null)
                return;

            var headYaw = HeadYaw(head);
            var offset = transform.position - head.position;
            m_FloatDistance = Mathf.Clamp(offset.magnitude, 0.85f, 3.60f);
            var dir = offset.sqrMagnitude > 1e-4f ? offset.normalized : head.forward;
            var worldYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            var pitch = Mathf.Asin(Mathf.Clamp(dir.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;

            m_WorldYaw = worldYaw;
            m_SmoothFollowPos = head.position;
            m_HasFollowState = true;
            m_CatchingUp = false;

            ComputeAngularHalfExtents(m_FloatDistance, out var halfW, out _);
            var sideMin = SideParkOffsetDeg(halfW);
            var rawOffset = Mathf.DeltaAngle(headYaw, worldYaw);

            if (s_CenterFocusedWindow == this)
            {
                s_CenterFocusedWindow = null;
                s_EmptyGazeDuration = 0f;
            }

            if (isInitialPin)
            {
                m_UserPositioned = false;
                m_FloatPitchOffset = Mathf.Clamp(pitch, -24f, 24f);
                var side = Mathf.Abs(rawOffset) > 4f ? Mathf.Sign(rawOffset) : PickBalancedSideSign(this);
                var chosenAbs = Mathf.Max(sideMin, Mathf.Clamp(Mathf.Abs(rawOffset), 0f, k_MaxSideParkDeg));
                m_PreferredYawOffset = side * chosenAbs;
                m_CurrentYawOffset = rawOffset;
                m_InitialParkUntil = Time.unscaledTime + k_InitialParkSeconds;
                m_ParkAnchorHeadYaw = headYaw;
                m_LastDirectGazeTime = float.NegativeInfinity;
                m_ContinuousGazeDuration = 0f;
            }
            else
            {
                m_UserPositioned = false;
                m_FloatPitchOffset = Mathf.Clamp(pitch, -60f, 65f);
                var side = Mathf.Abs(rawOffset) > 2f ? Mathf.Sign(rawOffset) : PickBalancedSideSign(this);
                m_PreferredYawOffset = side * Mathf.Clamp(Mathf.Max(Mathf.Abs(rawOffset), sideMin), sideMin, k_MaxSlotYawDeg);
                m_InitialParkUntil = 0f;
            }

            RebalanceFloatingSlots();
            if (!isInitialPin)
                m_CurrentYawOffset = m_ResolvedYawOffset;
        }

        static void UpdateFloatingCluster(Transform head, float now, float dt)
        {
            if (s_LastClusterFrame == Time.frameCount)
                return;
            s_LastClusterFrame = Time.frameCount;

            RebalanceFloatingSlots();

            var headYaw = HeadYaw(head);
            var ray = s_HasGazeRay ? s_GazeRay : new Ray(head.position, head.forward);
            var rayDir = ray.direction.sqrMagnitude > 1e-6f ? ray.direction.normalized : Vector3.forward;
            var rayYaw = Mathf.Atan2(rayDir.x, rayDir.z) * Mathf.Rad2Deg;
            var rayPitch = Mathf.Asin(Mathf.Clamp(rayDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            var minGain = 1f;
            var anyNonParking = false;
            var gazedIndex = -1;
            var bestGazeEdgeDist = float.MaxValue;
            var currentFocusedIndex = -1;
            var focusedHalfW = 14f;

            s_ContinuousGazeBuffer.Clear();
            for (var i = 0; i < s_FloatingBuffer.Count; i++)
            {
                var a = s_FloatingBuffer[i];
                var isCurrentFocused = a == s_CenterFocusedWindow;
                if (isCurrentFocused)
                    currentFocusedIndex = i;
                s_ContinuousGazeBuffer.Add(a.m_ContinuousGazeDuration);

                var toWin = a.transform.position - ray.origin;
                var dist = Mathf.Max(0.45f, toWin.magnitude);
                var dir = toWin.sqrMagnitude > 1e-4f ? toWin / dist : rayDir;
                var winYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                var winPitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;

                a.ComputeAngularHalfExtents(dist, out var halfW, out var halfH);
                if (isCurrentFocused)
                    focusedHalfW = halfW;
                var dx = Mathf.Max(0f, Mathf.Abs(Mathf.DeltaAngle(rayYaw, winYaw)) - (halfW + k_HitPadDeg));
                var dy = Mathf.Max(0f, Mathf.Abs(rayPitch - winPitch) - (halfH + k_HitPadDeg));
                var edgeDistDeg = Mathf.Sqrt(dx * dx + dy * dy);

                var strictDx = Mathf.Max(0f, Mathf.Abs(Mathf.DeltaAngle(rayYaw, winYaw)) - halfW);
                var strictDy = Mathf.Max(0f, Mathf.Abs(rayPitch - winPitch) - halfH);
                var strictEdgeDistDeg = Mathf.Sqrt(strictDx * strictDx + strictDy * strictDy);

                var isDirectTarget = s_GazeTarget != null && s_GazeTarget.transform.IsChildOf(a.transform);
                var isStrictDirectGaze = isDirectTarget || strictEdgeDistDeg <= k_DirectGazeToleranceDeg;
                if (isDirectTarget || edgeDistDeg <= k_BrakeStopDeg || a.IsBeingResized)
                    a.m_LastDirectGazeTime = now;

                var winGain = ComputeGazeBrakeFactor(isDirectTarget, edgeDistDeg, now - a.m_LastDirectGazeTime);
                a.m_WindowBrakeGain = winGain;

                if (now >= a.m_InitialParkUntil && !s_FloatingStowed)
                {
                    anyNonParking = true;
                    minGain = Mathf.Min(minGain, winGain);
                    if (isStrictDirectGaze)
                    {
                        var score = isDirectTarget ? -1f : strictEdgeDistDeg;
                        if (score < bestGazeEdgeDist)
                        {
                            bestGazeEdgeDist = score;
                            gazedIndex = i;
                        }
                    }
                }
            }

            var targetGain = anyNonParking ? minGain : 1f;
            if (targetGain < s_SmoothedClusterGain)
                s_SmoothedClusterGain = targetGain;
            else
                s_SmoothedClusterGain = Mathf.MoveTowards(s_SmoothedClusterGain, targetGain, dt * k_GainRecoverRate);

            if (anyNonParking && s_FloatingBuffer.Count > 0)
            {
                var focusDt = dt;
                if (currentFocusedIndex >= 0 && gazedIndex < 0 &&
                    Mathf.Abs(Mathf.DeltaAngle(headYaw, s_CenterAnchorHeadYaw)) > focusedHalfW + 10f)
                {
                    focusDt = dt * 2.2f;
                }
                var nextFocusedIndex = StepCenterFocusInternal(
                    gazedIndex, currentFocusedIndex, s_ContinuousGazeBuffer, s_FloatingBuffer.Count,
                    ref s_EmptyGazeDuration, gazedIndex < 0 && currentFocusedIndex >= 0 ? focusDt : dt);
                for (var i = 0; i < s_FloatingBuffer.Count; i++)
                    s_FloatingBuffer[i].m_ContinuousGazeDuration = s_ContinuousGazeBuffer[i];

                var nextFocused = nextFocusedIndex >= 0 && nextFocusedIndex < s_FloatingBuffer.Count
                    ? s_FloatingBuffer[nextFocusedIndex]
                    : null;
                if (nextFocused != s_CenterFocusedWindow)
                {
                    var prevFocused = s_CenterFocusedWindow;
                    s_CenterFocusedWindow = nextFocused;
                    RebalanceFloatingSlots();
                    if (nextFocused != null)
                    {
                        s_CenterAnchorHeadYaw = headYaw;
                        for (var i = 0; i < s_FloatingBuffer.Count; i++)
                        {
                            s_FloatingBuffer[i].m_InitialParkUntil = 0f;
                            s_FloatingBuffer[i].m_CatchingUp = true;
                        }
                    }
                    else if (prevFocused != null)
                    {
                        for (var i = 0; i < s_FloatingBuffer.Count; i++)
                        {
                            var a = s_FloatingBuffer[i];
                            a.m_InitialParkUntil = now + k_ReleaseParkSeconds;
                            a.m_ParkAnchorHeadYaw = headYaw;
                            a.m_LastDirectGazeTime = float.NegativeInfinity;
                            a.m_ContinuousGazeDuration = 0f;
                            a.m_CatchingUp = false;
                        }
                    }
                }
            }
        }

        void LateUpdate()
        {
            if (m_Suspended)
                return;

            var placer = AnchorPlacer.Instance;
            if (placer == null || placer.Fusion == null)
                return;

            if (m_Floating)
            {
                UpdateFloating(placer.Fusion.transform);
                return;
            }

            if (placer.Origin == null)
                return;

            var sixDof = placer.Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (!sixDof)
            {
                if (m_Anchor != null)
                    ReleaseAnchor();
                m_HasSessionPose = false;
                m_NeedsAnchor = true;
                return;
            }

            var origin = placer.Origin;
            if (!m_HasSessionPose)
            {
                m_SessionPos = origin.InverseTransformPoint(transform.position);
                m_SessionRot = Quaternion.Inverse(origin.rotation) * transform.rotation;
                m_HasSessionPose = true;
            }

            if (m_Anchor != null && m_Anchor.trackingState == TrackingState.Tracking)
            {
                var anchorPos = m_Anchor.transform.position;
                var anchorRot = LevelRotation(m_Anchor.transform.rotation);
                transform.SetPositionAndRotation(anchorPos, anchorRot);
                m_SessionPos = origin.InverseTransformPoint(anchorPos);
                m_SessionRot = Quaternion.Inverse(origin.rotation) * anchorRot;
            }
            else if (m_HasSessionPose)
            {
                var worldPos = origin.TransformPoint(m_SessionPos);
                var worldRot = LevelRotation(origin.rotation * m_SessionRot);
                transform.SetPositionAndRotation(worldPos, worldRot);
            }

            if (m_Anchor == null && !m_NeedsAnchor && !m_PinInFlight)
            {
                m_NeedsAnchor = true;
                m_PendingPinAt = Time.unscaledTime + 0.35f;
            }

            if (m_NeedsAnchor && !m_PinInFlight && Time.unscaledTime >= m_PendingPinAt &&
                placer.Fusion.IsArTracking && placer.AnchorManager != null &&
                placer.AnchorManager.enabled && placer.AnchorManager.subsystem != null)
            {
                CreateAnchorAsync(placer, m_PinVersion);
            }
        }

        void UpdateFloating(Transform head)
        {
            if (head == null)
                return;

            var now = Time.unscaledTime;
            var dt = Time.unscaledDeltaTime;
            var targetStow = s_FloatingStowed ? 1f : 0f;
            m_StowProgress = Mathf.MoveTowards(m_StowProgress, targetStow, dt / k_StowDurationSeconds);

            if (m_Canvas == null)
                m_Canvas = GetComponent<Canvas>();
            if (m_Group == null)
                m_Group = GetComponent<CanvasGroup>();

            // Once fully slid off to the side, disable Canvas & raycasts completely so fast head turns
            // can NEVER glitch or flash the stowed window back on screen.
            if (s_FloatingStowed && m_StowProgress >= 0.995f)
            {
                if (m_Canvas != null && m_Canvas.enabled)
                    m_Canvas.enabled = false;
                if (m_Group != null)
                {
                    m_Group.alpha = 0f;
                    m_Group.blocksRaycasts = false;
                    m_Group.interactable = false;
                }
                return;
            }

            if (m_Canvas != null && !m_Canvas.enabled)
                m_Canvas.enabled = true;

            if (!m_HasFollowState)
                CaptureFloatingOffset(head, isInitialPin: false);

            UpdateFloatingCluster(head, now, dt);

            m_CurrentYawOffset = Mathf.Lerp(m_CurrentYawOffset, m_ResolvedYawOffset, 1f - Mathf.Exp(-dt * k_SlotSmoothRate));

            var isGazedFreeze = s_SmoothedClusterGain <= 0.05f && !s_FloatingStowed && now >= m_InitialParkUntil;
            if (!isGazedFreeze || Vector3.Distance(m_SmoothFollowPos, head.position) > k_GazedPosDeadZoneMeters)
                m_SmoothFollowPos = Vector3.Lerp(m_SmoothFollowPos, head.position, 1f - Mathf.Exp(-dt * k_PosFollowRate));

            var headYaw = HeadYaw(head);
            float finalYaw;
            var eased = 0f;
            if (s_FloatingStowed || m_StowProgress > 0.001f)
            {
                eased = m_StowProgress * m_StowProgress * (3f - 2f * m_StowProgress);
                var sideYaw = m_StowSide * k_StowSideYawDeg;
                finalYaw = headYaw + Mathf.Lerp(m_CurrentYawOffset, sideYaw, eased);
                m_WorldYaw = headYaw + m_CurrentYawOffset;
            }
            else if (now < m_InitialParkUntil)
            {
                // Initial park glide bypasses gaze brake so the cursor smoothly slips off the pin button.
                var parkTarget = m_ParkAnchorHeadYaw + m_CurrentYawOffset;
                m_WorldYaw = Mathf.LerpAngle(m_WorldYaw, parkTarget, 1f - Mathf.Exp(-dt * 7.5f));
                finalYaw = m_WorldYaw;
            }
            else if (s_CenterFocusedWindow != null)
            {
                // Stay anchored at the heading where the user focused the app (never chase headYaw),
                // so turning the head away cleanly leaves the app and re-opens the center view.
                var targetYaw = s_CenterAnchorHeadYaw + (this == s_CenterFocusedWindow ? 0f : m_CurrentYawOffset);
                m_WorldYaw = Mathf.LerpAngle(m_WorldYaw, targetYaw, 1f - Mathf.Exp(-dt * k_CenterFocusGlideRate));
                m_CatchingUp = false;
                finalYaw = m_WorldYaw;
            }
            else
            {
                // Calm peripheral side-follow so side windows drift gently without aggressively chasing the user.
                var calmGain = s_SmoothedClusterGain * k_SideFollowCalmMultiplier;
                m_WorldYaw = StepFloatingWorldYaw(m_WorldYaw, headYaw, m_CurrentYawOffset,
                    calmGain, dt, ref m_CatchingUp);
                if (isGazedFreeze && m_WindowBrakeGain > 0.5f)
                    ApplyWorldPairSeparationNudge(dt);
                finalYaw = m_WorldYaw;
            }

            var dir = Quaternion.Euler(-m_FloatPitchOffset, finalYaw, 0f) * Vector3.forward;
            var pos = m_SmoothFollowPos + dir * m_FloatDistance;
            transform.SetPositionAndRotation(pos, M3Theme.SphericalFacingRotation(pos, m_SmoothFollowPos));

            if (m_Group != null && m_StowProgress > 0.001f)
            {
                m_Group.alpha = 1f - eased;
                var interactive = !s_FloatingStowed && m_StowProgress < 0.1f;
                m_Group.blocksRaycasts = interactive;
                m_Group.interactable = interactive;
            }
        }

        // Gently nudges non-gazed floating windows clear if a manual move or head lock left two neighbors overlapping.
        void ApplyWorldPairSeparationNudge(float dt)
        {
            ComputeAngularHalfExtents(m_FloatDistance, out var myHalfW, out _);
            for (var i = 0; i < s_FloatingBuffer.Count; i++)
            {
                var other = s_FloatingBuffer[i];
                if (other == null || other == this)
                    continue;
                other.ComputeAngularHalfExtents(other.m_FloatDistance, out var otherHalfW, out _);
                var minSep = myHalfW + k_WindowGapDeg + otherHalfW;
                var diff = Mathf.DeltaAngle(other.m_WorldYaw, m_WorldYaw);
                var absDiff = Mathf.Abs(diff);
                if (absDiff >= minSep)
                    continue;
                var pushDir = Mathf.Abs(diff) > 0.5f ? Mathf.Sign(diff) : (m_CurrentYawOffset >= other.m_CurrentYawOffset ? 1f : -1f);
                var targetYaw = other.m_WorldYaw + pushDir * minSep;
                m_WorldYaw = Mathf.LerpAngle(m_WorldYaw, targetYaw, 1f - Mathf.Exp(-dt * k_SlotSmoothRate));
            }
        }

        void RestoreVisualState()
        {
            if (m_Canvas == null)
                m_Canvas = GetComponent<Canvas>();
            if (m_Group == null)
                m_Group = GetComponent<CanvasGroup>();
            if (m_Canvas != null)
                m_Canvas.enabled = true;
            if (m_Group != null)
            {
                m_Group.alpha = 1f;
                m_Group.blocksRaycasts = true;
                m_Group.interactable = true;
            }
        }

        async void CreateAnchorAsync(AnchorPlacer placer, int version)
        {
            var anchors = placer.AnchorManager;
            var origin = placer.Origin;
            if (anchors == null || origin == null || !anchors.enabled || anchors.subsystem == null)
                return;

            m_NeedsAnchor = false;
            m_PinInFlight = true;

            var worldPos = m_HasSessionPose ? origin.TransformPoint(m_SessionPos) : transform.position;
            var worldRot = LevelRotation(m_HasSessionPose ? origin.rotation * m_SessionRot : transform.rotation);

            try
            {
                var result = await anchors.TryAddAnchorAsync(new Pose(worldPos, worldRot));
                if (this == null)
                {
                    if (result.status.IsSuccess() && result.value != null && anchors != null && anchors.enabled)
                        anchors.TryRemoveAnchor(result.value);
                    return;
                }

                m_PinInFlight = false;
                if (m_Suspended || m_Floating || version != m_PinVersion || !isActiveAndEnabled)
                {
                    if (result.status.IsSuccess() && result.value != null && anchors != null && anchors.enabled)
                        anchors.TryRemoveAnchor(result.value);
                    return;
                }

                if (result.status.IsSuccess() && result.value != null)
                {
                    ReleaseAnchor();
                    m_Anchor = result.value;
                    Debug.Log($"[SpatialAnchor] Pinned '{name}' in ARCore 3D space");
                }
                else
                {
                    m_NeedsAnchor = true;
                    m_PendingPinAt = Time.unscaledTime + 1f;
                }
            }
            catch (System.Exception e)
            {
                if (this != null)
                {
                    m_PinInFlight = false;
                    m_NeedsAnchor = true;
                    m_PendingPinAt = Time.unscaledTime + 1.5f;
                    Debug.LogWarning($"[SpatialAnchor] Anchor creation deferred for '{name}': {e.Message}");
                }
            }
        }

        void ReleaseAnchor()
        {
            if (m_Anchor == null)
                return;
            var anchor = m_Anchor;
            m_Anchor = null;
            var placer = AnchorPlacer.Instance;
            if (placer != null && placer.AnchorManager != null && placer.AnchorManager.enabled)
                placer.AnchorManager.TryRemoveAnchor(anchor);
        }

        void OnDestroy()
        {
            if (s_CenterFocusedWindow == this)
            {
                s_CenterFocusedWindow = null;
                s_EmptyGazeDuration = 0f;
            }
            s_All.Remove(this);
            if (m_Floating)
                RebalanceFloatingSlots();
            m_PinVersion++;
            ReleaseAnchor();
        }

        static float HeadYaw(Transform head)
        {
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : 0f;
        }

        static Quaternion LevelRotation(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            if (Mathf.Abs(forward.y) > 0.995f)
                return rotation;
            return Quaternion.LookRotation(forward, Vector3.up);
        }
    }
}
