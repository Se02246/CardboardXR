using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Gaze and pinch are the app's main input everywhere: the head aims, a pinch clicks, and a screen
    // tap still works too. In 6DoF, hand tracking (HandTracker, through HandInteractorRig) turns a
    // pinch into a click and also picks things up. In 3DoF there's no AR session to track the hand
    // with, so PinchSensor watches a plain camera feed for pinches instead. This runs it whenever the
    // app is in 3DoF and nothing else has the camera: it lets go of the camera just before 6DoF starts
    // the AR session, and waits k_HandoffSeconds after 6DoF ends before taking it back, since the
    // camera can come up silent if reopened at once. Pinching is always on: it's the app's input, and
    // a switch for it in the settings panel got pressed by accident, leaving only a screen tap to
    // switch it back. (The detector already slows to a few checks a second with no hand in view.) The
    // "pinch 0" launch option turns it off, for measuring its heat. It's for 3DoF only: in 6DoF, hand
    // tracking is part of what each demo asks for (DemoRequirements.Hands). The reticle turns
    // light blue while a hand is in view in either mode, so it's clear a pinch will register; and while
    // the gaze rests on a button with no hand in view, a hint under the reticle says to raise one (the
    // camera only sees about 21 degrees above and below the gaze, so a hand resting low can't pinch:
    // in the lobby a pinch on the settings panel's X once took 16 s to be seen). It also reads the
    // Pixel Watch app (WatchLink): while that's connected its double pinch clicks instead, and the
    // camera's pinch only works in scenes that need the hand (PinchRouter.HandPinchMuted). Put it on
    // the XR Origin. Logs [Pinch].
    [DefaultExecutionOrder(-10)]
    public class PinchInput : MonoBehaviour
    {
        const float k_HandoffSeconds = 1f;
        // How long the gaze rests on a button, and no hand is seen, before the hint shows, in seconds.
        public const float k_HintSeconds = 1f;
        // Watch gyro air-mouse cursor gain, smoothing, and FOV clamp (degrees from head center).
        const float k_WatchCursorGain = 1.65f;
        const float k_WatchCursorMaxYaw = 36f;
        const float k_WatchCursorMaxPitch = 28f;
        const float k_WatchCursorSmoothRate = 28f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] PinchSensor m_Sensor;
        [SerializeField] HandTracker m_Tracker;
        [SerializeField] GazePointer m_Gaze;
        [Tooltip("Under the reticle: shown while the gaze rests on a button and no hand is in view.")]
        [SerializeField] GameObject m_HandHint;

        bool m_On = true;
        float m_ThreeDofSince;
        float m_NoHandFor;
        float m_OnButtonFor;
        Vector2 m_WatchCursorTarget;
        bool m_WasWatchConnected;

        // Whether pinching clicks: always, unless the "pinch 0" launch option says otherwise.
        public bool IsOn => m_On;

        void Awake()
        {
            m_On = LaunchOptions.GetInt("pinch", 1) != 0;
        }

        void OnEnable()
        {
            m_Fusion.ModeChanging += OnModeChanging;
            GazePointer.TriggerInterceptor = WatchGrabController.TryConsumeRelease;
            m_ThreeDofSince = Time.unscaledTime;
            Debug.Log($"[Pinch] Pinch to click {(m_On ? "on" : "off")}");
        }

        void OnDisable()
        {
            m_Fusion.ModeChanging -= OnModeChanging;
            if (GazePointer.TriggerInterceptor == WatchGrabController.TryConsumeRelease)
                GazePointer.TriggerInterceptor = null;
            m_Sensor.enabled = false;
        }

        void OnModeChanging(HeadPoseFusion.Mode next)
        {
            if (next == HeadPoseFusion.Mode.SixDof)
                m_Sensor.enabled = false;
            else
                m_ThreeDofSince = Time.unscaledTime;
        }

        void Update()
        {
            // The watch: its gyro steers the VR cursor (or moves a grabbed window), its pinches click/grab,
            // its crown scrolls/pushes, and its Settings button opens the panel.
            WatchLink.Poll(PinchRouter.SceneWantsHandPinch, out var watchPinches, out var settingsRequests);
            UpdateWatchCursor();
            for (var i = 0; i < watchPinches; i++)
                PinchRouter.WatchPinch(m_Gaze);
            WatchGrabController.Update(m_Gaze);
            FloatingWindowHandle.UpdateAll(m_Gaze);
            if (Mathf.Abs(WatchLink.RotarySteps) > 1e-4f)
                PinchRouter.WatchRotary(m_Gaze, WatchLink.RotarySteps);
            if (settingsRequests > 0)
            {
                Debug.Log("[Pinch] The watch asked for the settings panel");
                m_WatchCursorTarget = Vector2.zero;
                if (m_Gaze != null)
                    m_Gaze.CursorAnglesDeg = Vector2.zero;
                if (SettingsSummoner.SuppressForFullscreen)
                    SettingsSummoner.FullscreenExitAction?.Invoke();
                else
                    SettingsPanelHub.Open();
            }
            // Keep open-palm & camera pinch detection active in ALL modes:
            // - 6DoF (Main Camera): HandTracker runs.
            // - 3DoF / Ultrawide 0.5x: PinchSensor runs (always sharing the 110 deg Ultrawide feed on Android).
            var visionActive = !PinchRouter.HandPinchMuted;
            var threeDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.ThreeDof;

            if (!threeDof && m_Tracker != null && !m_Tracker.enabled)
                m_Tracker.enabled = true;

            var wanted = threeDof && (PassthroughController.IsUltrawideActive || Application.platform == RuntimePlatform.Android ||
                (m_On && Time.unscaledTime - m_ThreeDofSince >= k_HandoffSeconds));
            if (m_Sensor.enabled != wanted)
                m_Sensor.enabled = wanted;

            var deltaTime = Time.unscaledDeltaTime;
            var cameraHandSeen = threeDof
                ? m_Sensor.enabled && m_Sensor.SeesHand
                : m_Tracker.isActiveAndEnabled && m_Tracker.IsTracking;

            // Feed continuous pinch ray & hold state to VisualPinchScroller for bounds-restricted 1:1 touch scrolling.
            var pinchRay = default(Ray);
            var hasPinchRay = false;
            var visualPinchHeld = false;
            if (visionActive)
            {
                if (threeDof)
                {
                    visualPinchHeld = m_Sensor.enabled && m_Sensor.IsPinched;
                    hasPinchRay = m_Sensor.enabled && m_Sensor.TryGetPinchRay(out pinchRay);
                }
                else
                {
                    var feed = m_Tracker != null ? m_Tracker.GetComponent<HandSubsystemFeed>() : null;
                    visualPinchHeld = m_Tracker != null && m_Tracker.isActiveAndEnabled && feed != null && feed.IsPinching;
                    if (cameraHandSeen && m_Gaze != null)
                    {
                        var head = m_Gaze.transform;
                        var dir = m_Tracker.PinchPoint - head.position;
                        hasPinchRay = dir.sqrMagnitude > 1e-6f;
                        if (hasPinchRay)
                            pinchRay = new Ray(head.position, dir.normalized);
                    }
                }
            }
            VisualPinchScroller.UpdateHand(pinchRay, hasPinchRay, visualPinchHeld, deltaTime);

            var handSeen = !visionActive && WatchLink.IsConnected ? true : cameraHandSeen || WatchLink.IsConnected;
            if (m_Gaze.Reticle != null)
            {
                m_Gaze.Reticle.HandSeen = handSeen;
                m_Gaze.Reticle.Grabbing = WatchGrabController.IsBusy;
            }

            m_NoHandFor = handSeen ? 0f : m_NoHandFor + deltaTime;
            m_OnButtonFor = PinchRouter.IsButton(m_Gaze.Target) ? m_OnButtonFor + deltaTime : 0f;
            if (m_HandHint != null && m_HandHint.activeSelf)
                m_HandHint.SetActive(false);
        }

        void UpdateWatchCursor()
        {
            if (m_Gaze == null)
                return;
            if (GazePointer.LockCursorToCenter)
            {
                m_WatchCursorTarget = Vector2.zero;
                m_Gaze.CursorAnglesDeg = Vector2.zero;
                return;
            }
            var dt = Time.unscaledDeltaTime;
            if (!WatchLink.IsConnected || !WatchLink.GyroCursorEnabled)
            {
                m_WasWatchConnected = false;
                m_WatchCursorTarget = Vector2.zero;
                if (m_Gaze.CursorAnglesDeg.sqrMagnitude > 1e-4f)
                    m_Gaze.CursorAnglesDeg = Vector2.Lerp(m_Gaze.CursorAnglesDeg, Vector2.zero,
                        1f - Mathf.Exp(-dt * 14f));
                else
                    m_Gaze.CursorAnglesDeg = Vector2.zero;
                return;
            }

            if (!m_WasWatchConnected)
            {
                m_WasWatchConnected = true;
                m_WatchCursorTarget = m_Gaze.CursorAnglesDeg;
            }
            // If an external recenter reset CursorAnglesDeg to zero while target wasn't near zero, sync target.
            if (m_Gaze.CursorAnglesDeg == Vector2.zero && m_WatchCursorTarget.sqrMagnitude > 1f && WatchLink.GyroDeltaRad.sqrMagnitude < 1e-10f)
                m_WatchCursorTarget = Vector2.zero;

            // While dragging/resizing a window/object, WatchGrabController consumes the watch gyro.
            if (WatchGrabController.IsBusy)
                return;

            var gyro = WatchLink.GyroDeltaRad;
            if (gyro.sqrMagnitude >= 1e-10f)
            {
                m_WatchCursorTarget.x = Mathf.Clamp(
                    m_WatchCursorTarget.x + gyro.x * Mathf.Rad2Deg * k_WatchCursorGain,
                    -k_WatchCursorMaxYaw, k_WatchCursorMaxYaw);
                m_WatchCursorTarget.y = Mathf.Clamp(
                    m_WatchCursorTarget.y + gyro.y * Mathf.Rad2Deg * k_WatchCursorGain,
                    -k_WatchCursorMaxPitch, k_WatchCursorMaxPitch);
            }

            var cur = m_Gaze.CursorAnglesDeg;
            if ((m_WatchCursorTarget - cur).sqrMagnitude > 1e-5f)
            {
                var alpha = 1f - Mathf.Exp(-dt * k_WatchCursorSmoothRate);
                m_Gaze.CursorAnglesDeg = Vector2.Lerp(cur, m_WatchCursorTarget, alpha);
            }
        }

        // Whether to show the hint: pinching is running, and for k_HintSeconds the gaze has rested on a
        // button and no hand has been seen. Pure, for testing.
        public static bool ShowHint(bool pinchRunning, float noHandFor, float onButtonFor) =>
            pinchRunning && noHandFor >= k_HintSeconds && onButtonFor >= k_HintSeconds;
    }
}
