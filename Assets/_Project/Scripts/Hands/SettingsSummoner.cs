using System;
using System.Collections.Generic;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Hands
{
    // The app's settings panel (Core's toolbar), summoned as visionOS offers its controls: look at your
    // palm and a round gear button appears between your thumb and index finger, following the hand;
    // pinch while it shows and the panel opens in front of you. Its X button closes it, and so does
    // moving to another scene (Close). The panel is the same in every scene, so demos need no controls
    // of their own for what it does. While open, the panel follows the user lazily (Follow): always
    // M3Theme.k_OverlayDistance from the head, in front of any scene's panels, and turning to stay in
    // front once the head has turned past the panel's edge (DeadZone), so looking at any of its buttons,
    // even the outermost, doesn't make it slide away.
    //   Which hand pipeline is used follows the mode: in 6DoF HandTracker, whose 3D joints place the
    //   button; in 3DoF PinchSensor, which only sees the hand in the image, so the button goes in that
    //   direction at k_ButtonDistance (the hand isn't drawn in 3DoF anyway). Either way the palm test
    //   is PalmGesture's, on the image. The gear's pinch opens the panel and nothing else
    //   (TryConsumePinch, which PinchRouter asks first). The phone-screen menu can open it too
    //   (SettingsPanelHub), for when there's no hand. Put it on the XR Origin. Logs [Settings].
    [DefaultExecutionOrder(-115)]
    public class SettingsSummoner : MonoBehaviour
    {
        // Where the 3DoF button goes, in meters along the hand's direction.
        const float k_ButtonDistance = 0.4f;
        // The 3DoF camera's view, as PinchSensor's 4:3 frames see it, in degrees.
        const float k_CameraHorizontal = 64f;
        const float k_CameraVertical = 50f;
        // The button sits this far in front of the hand, toward the eyes, in meters.
        const float k_InFront = 0.03f;
        // The panel sits at least this far below the line of sight (level, at the head's height), and
        // farther if the panel above it (the app library, at eye level) needs the room: k_PanelGap
        // between them. Both face the head; the library stands upright, the panel tilts up to it.
        public const float k_MinPanelDrop = 15f;
        const float k_PanelGap = 1.5f;
        // The panel turns to follow the head once the head is past its edge by this much, in degrees,
        // and keeps turning until it's within k_FollowSettled. A fixed 15 degrees was less than the
        // toolbar's own half-width (about 23), so aiming at its outer buttons set it moving away.
        public const float k_FollowMargin = 10f;
        const float k_FollowSettled = 1f;
        // How it catches up: a damped spring that eases out of rest and into place, taking about this
        // long, no faster than k_FollowMaxSpeed degrees a second. A plain exponential catch-up (about
        // 0.12 s) felt locked to the head the moment the dead zone was left.
        public const float k_FollowSmoothTime = 0.5f;
        const float k_FollowMaxSpeed = 90f;
        // How long the panel takes to follow the head's position, for a step or a lean.
        const float k_PositionSmoothTime = 0.25f;
        // Thumb and index joints between which the button sits.
        const int k_ThumbJoint = 3;
        const int k_IndexJoint = 6;
        const int k_MiddleKnuckle = 9;
        // How much each palm-facing frame moves the upright vote (see Upright).
        const float k_VoteRate = 0.1f;
        // Looking down past this angle (degrees) shows gaze-clickable buttons low below the horizon;
        // looking back up above k_LookDownHideDeg hides them.
        const float k_LookDownShowDeg = 50f;
        const float k_LookDownHideDeg = 38f;
        const float k_LookDownButtonPitchDeg = 64f;
        const float k_LookDownButtonDistance = 1.15f;
        // Looking up past this angle shows the floating quick-toggles pill + live clock.
        const float k_LookUpShowDeg = 34f;
        const float k_LookUpHideDeg = 22f;
        public const float k_LookUpBarPitchDeg = 36f;
        public const float k_LookUpBarDistance = 1.28f;
        // Compact vertical smartphone hologram resting realistically between palm & fingers,
        // dynamically sized and tilted to follow the user's real hand rotation.
        const float k_PalmWidthScale = 1.15f;
        const float k_MinPalmPhoneWidth = 0.075f;
        const float k_MaxPalmPhoneWidth = 0.130f;
        const float k_DefaultPalmWidth = 0.088f;
        const float k_PalmHoverForward = 0.018f;
        const float k_PalmDetachGap = 0.009f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] HandTracker m_Tracker;
        [SerializeField] PinchSensor m_Sensor;
        [Tooltip("The settings panel, hidden until summoned.")]
        [SerializeField] RectTransform m_Panel;
        [Tooltip("The gear button's canvas, shown while a palm faces the user.")]
        [SerializeField] RectTransform m_Button;
        [Tooltip("Optional: shown at eye level above the panel, as part of it (the app library).")]
        [SerializeField] RectTransform m_Above;
        [SerializeField] TMP_Text m_ToolbarClockText;
        [SerializeField] TMP_Text m_ToolbarBatteryText;
        [SerializeField] M3Button m_ToolbarMediaBtn;
        [SerializeField] M3Button m_ToolbarNotifBtn;
        [SerializeField] GameObject m_ToolbarNotifBadge;
        [SerializeField] TMP_Text m_ToolbarNotifBadgeText;

        static SettingsSummoner s_Instance;

        readonly PalmFacingState m_Palm = new();
        readonly OneEuroFilter m_ButtonFilter = new(2f, 2f);
        bool m_ButtonShown;
        bool m_PalmMode;
        bool m_CapturedNormalScale;
        Vector3 m_NormalPanelScale = Vector3.one;
        float m_SmoothPalmWidth = k_DefaultPalmWidth;
        M3Button m_DetachButton;
        M3Button m_PalmTorchButton;
        M3Button m_PalmReorderButton;
        GameObject m_CloseButton;
        UnityEngine.UI.Image m_PanelBackground;
        M3Toolbar m_Toolbar;
        readonly System.Collections.Generic.List<GameObject> m_ToolbarChildren = new();
        readonly System.Collections.Generic.List<GameObject> m_HiddenForPalm = new();

        // The panel's heading around the user, in degrees, whether it's turning to catch up and how fast,
        // and where the head is taken to be, following it smoothly.
        float m_PanelYaw;
        float m_PanelPitchOffset;
        float m_Distance = M3Theme.k_OverlayDistance;
        bool m_Following;
        bool m_WatchGrabbed;
        float m_YawSpeed;
        Vector3 m_Center;
        Vector3 m_CenterVelocity;
        // Whether PinchSensor's image is upright, as the hand shows it: +1 upright, -1 upside down. A
        // safeguard for a camera that turns its image without saying so: turning doesn't change the
        // palm test, only where the button goes. (The iPhone's image was mirrored instead, which
        // PinchSensor now corrects.)
        float m_UprightVote;
        bool m_LoggedUpright;
        RectTransform m_LookDownButton;
        M3Button m_LookDownAppsBtn;
        M3Button m_LookDownRecenterBtn;
        M3Button m_LookDownStowBtn;
        M3Button m_LookDownExtraBtn;
        bool m_LookDownShown;
        float m_LookDownYaw;

        RectTransform m_LookUpBar;
        RectTransform m_LookUpClockGroup;
        TMP_Text m_LookUpClockText;
        TMP_Text m_LookUpBatteryText;
        RectTransform m_LookUpDivider;
        M3Button m_LookUpAppsBtn;
        M3Button m_LookUpEnvBtn;
        M3Button m_LookUpEnvNextBtn;
        M3Button m_LookUpTorchBtn;
        M3Button m_LookUpMediaBtn;
        M3Button m_LookUpNotifBtn;
        GameObject m_LookUpNotifBadge;
        TMP_Text m_LookUpNotifBadgeText;
        bool m_LookUpShown;
        float m_LookUpYaw;

        Quaternion m_PalmRot = Quaternion.identity;
        bool m_HasPalmRot;
        int m_LastClockMinute = -1;
        int m_LastBatteryPercent = -2;
        bool m_LastBatteryCharging;
        float m_NextBatteryPollAt;

        static string s_ExtraLookDownIcon;
        static System.Action s_ExtraLookDownAction;

        // Optional scene-specific callback invoked by the look-down "Centra" button.
        public static System.Action RecenterAction { get; set; }

        // Optional provider supplying locked head yaw from an already-docked look-up notification banner.
        public static System.Func<float?> PreferredLookUpYawProvider { get; set; }
        public static bool IsLookUpBarShown => s_Instance != null && s_Instance.m_LookUpShown;
        public static float LookUpBarYaw => s_Instance != null ? s_Instance.m_LookUpYaw : 0f;

        // Suppresses the palm Home gear/menu and normal look-down/look-up bars during VR SBS fullscreen mode.
        public static bool SuppressForFullscreen { get; set; }
        public static System.Action FullscreenExitAction { get; set; }

        public bool IsOpen => m_Panel != null && M3Fade.IsShown(m_Panel.gameObject);
        public bool IsPalmMode => m_PalmMode;

        // Whether the panel is open, for PinchRouter: demos don't take pinches meanwhile.
        public static bool IsPanelOpen => s_Instance != null && s_Instance.IsOpen;

        // The panel opened (true) or closed (false). A demo can pause while it's open, as Orb Rush does.
        public event System.Action<bool> OpenChanged;
        // Fired when opening the palm pops the miniature vertical smartphone Home/Apps onto the user's hand.
        public event System.Action PalmOpened;
        // Fired whenever switching between vertical smartphone palm mode (true) and horizontal floating mode (false).
        public event System.Action<bool> PalmModeChanged;

        void Awake()
        {
            s_Instance = this;
            CaptureNormalScales();
            m_Panel.gameObject.SetActive(false);
            m_Button.gameObject.SetActive(false);
            if (m_Above != null)
                FloatingWindowHandle.Exclude(m_Above);
            if (m_Panel != null)
                FloatingWindowHandle.Exclude(m_Panel);
            BuildDetachButton();
            BuildLookDownButton();
            BuildLookUpBar();
            UiRenderQueue.ApplyRecursive(m_Panel, UiRenderQueue.k_PalmOverlayQueue);
            UiRenderQueue.ApplyRecursive(m_Button, UiRenderQueue.k_PalmOverlayQueue);
        }

        void CaptureNormalScales()
        {
            if (m_CapturedNormalScale)
                return;
            m_NormalPanelScale = NormalScaleOf(m_Panel);
            m_CapturedNormalScale = true;
        }

        static Vector3 NormalScaleOf(RectTransform panel)
        {
            if (panel == null)
                return Vector3.one;
            var dist = panel.TryGetComponent<M3Canvas>(out var canvas) ? canvas.Distance : M3Theme.k_OverlayDistance;
            return Vector3.one * M3Theme.DpToMeters(1f, dist);
        }

        void BuildDetachButton()
        {
            if (m_Panel == null)
                return;
            m_PanelBackground = m_Panel.GetComponent<UnityEngine.UI.Image>();
            m_Toolbar = m_Panel.GetComponent<M3Toolbar>();
            m_ToolbarChildren.Clear();
            for (var i = 0; i < m_Panel.childCount; i++)
            {
                var child = m_Panel.GetChild(i);
                if (child == m_Above)
                    continue;
                if (child.name == "Close")
                {
                    m_CloseButton = child.gameObject;
                    continue;
                }
                m_ToolbarChildren.Add(child.gameObject);
            }
            // Centered cleanly at (0, 0) on m_Panel while in vertical smartphone palm mode
            // (directly underneath the smartphone bottom edge), flanked by quick Torcia & Riordina toggles.
            m_PalmTorchButton = M3.IconButton("Palm Torch", m_Panel, "flashlight_on", M3Button.Variant.Tonal,
                new Vector2(-114f, 0f), () => TorchManager.ToggleGlobal(), 46f);
            m_PalmTorchButton.gameObject.SetActive(false);

            m_DetachButton = M3.Button("Detach To Floating", m_Panel, "Fluttuante", M3Button.Variant.Filled,
                Vector2.zero, DetachToFloating, "open_in_new", 164f);
            m_DetachButton.gameObject.SetActive(false);

            m_PalmReorderButton = M3.IconButton("Palm Reorder", m_Panel, "swap_horiz", M3Button.Variant.Tonal,
                new Vector2(114f, 0f), () => SettingsPanelHub.ToggleReorder(), 46f);
            m_PalmReorderButton.gameObject.SetActive(false);
        }

        void BuildLookDownButton()
        {
            m_LookDownButton = M3.Canvas("Look Down Apps Button", null, new Vector2(226f, 72f), k_LookDownButtonDistance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            m_LookDownButton.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_LookDownButton);
            m_LookDownAppsBtn = M3.IconButton("Apps", m_LookDownButton, "apps", M3Button.Variant.Filled,
                new Vector2(-68f, 0f), OnLookDownAppsClicked, 54f);
            m_LookDownRecenterBtn = M3.IconButton("Recenter All", m_LookDownButton, "my_location", M3Button.Variant.Tonal,
                Vector2.zero, OnLookDownRecenterClicked, 54f);
            m_LookDownStowBtn = M3.IconButton("Stow Floating", m_LookDownButton, "visibility_off", M3Button.Variant.Tonal,
                new Vector2(68f, 0f), OnLookDownStowClicked, 54f);
            m_LookDownExtraBtn = M3.IconButton("Extra", m_LookDownButton, "keyboard", M3Button.Variant.Tonal,
                new Vector2(136f, 0f), OnLookDownExtraClicked, 54f);
            m_LookDownExtraBtn.gameObject.SetActive(false);
            RefreshLookDownLayout();
            UiRenderQueue.ApplyRecursive(m_LookDownButton, UiRenderQueue.k_PalmOverlayQueue);
            m_LookDownButton.gameObject.SetActive(false);
        }

        void BuildLookUpBar()
        {
            m_LookUpBar = M3.Canvas("Look Up Quick Bar", null, new Vector2(465f, 72f), k_LookUpBarDistance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            m_LookUpBar.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_LookUpBar);

            m_LookUpClockGroup = M3.Group("Clock", m_LookUpBar, Vector2.zero, new Vector2(80f, 56f));
            m_LookUpClockText = M3.Text("Clock Time", m_LookUpClockGroup, DateTime.Now.ToString("HH:mm"),
                M3Theme.Type.TitleMedium, new Vector2(0f, 9f), new Vector2(78f, 28f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            m_LookUpClockText.fontSize = 19f;
            m_LookUpClockText.textWrappingMode = TextWrappingModes.NoWrap;

            m_LookUpBatteryText = M3.Text("Battery Level", m_LookUpClockGroup, "", M3Theme.Type.LabelLarge,
                new Vector2(0f, -13f), new Vector2(78f, 20f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_LookUpBatteryText.fontSize = 12.5f;
            m_LookUpBatteryText.textWrappingMode = TextWrappingModes.NoWrap;

            var divImg = M3.Surface("Divider", m_LookUpBar, Vector2.zero, new Vector2(1f, 34f),
                M3Theme.OutlineVariant, 0f, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            m_LookUpDivider = divImg.rectTransform;

            m_LookUpAppsBtn = M3.IconButton("Apps", m_LookUpBar, "apps", M3Button.Variant.Filled,
                Vector2.zero, OnLookUpAppsClicked, 48f);
            m_LookUpEnvBtn = M3.IconButton("Ambienti", m_LookUpBar, "landscape", M3Button.Variant.Tonal,
                Vector2.zero, OnLookUpEnvClicked, 48f);
            m_LookUpEnvNextBtn = M3.IconButton("Esci dall'ambiente", m_LookUpBar, "view_in_ar", M3Button.Variant.Tonal,
                Vector2.zero, OnLookUpEnvExitClicked, 48f);
            m_LookUpEnvNextBtn.gameObject.SetActive(false);
            m_LookUpTorchBtn = M3.IconButton("Torcia", m_LookUpBar, "flashlight_on", M3Button.Variant.Tonal,
                Vector2.zero, OnLookUpTorchClicked, 48f);
            m_LookUpMediaBtn = M3.IconButton("Media", m_LookUpBar, "music_note", M3Button.Variant.Tonal,
                Vector2.zero, OnLookUpMediaClicked, 48f);
            m_LookUpNotifBtn = M3.IconButton("Notifiche", m_LookUpBar, "notifications", M3Button.Variant.Tonal,
                Vector2.zero, OnLookUpNotifClicked, 48f);

            var badgeImg = M3.Surface("Unread Badge", (RectTransform)m_LookUpNotifBtn.transform,
                new Vector2(16f, 16f), new Vector2(20f, 20f), new Color(1f, 0.27f, 0.23f, 0.98f),
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy);
            m_LookUpNotifBadgeText = M3.Text("Count", badgeImg.rectTransform, "", M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(20f, 20f), Color.white, TextAlignmentOptions.Center);
            m_LookUpNotifBadgeText.fontSize = 11f;
            m_LookUpNotifBadge = badgeImg.gameObject;
            m_LookUpNotifBadge.SetActive(false);

            RefreshLookUpLayout();
            UiRenderQueue.ApplyRecursive(m_LookUpBar, UiRenderQueue.k_PalmOverlayQueue);
            m_LookUpBar.gameObject.SetActive(false);
        }

        // Registers an optional extra circular icon button beside Apps, Recenter & Stow when looking down
        // (e.g. a Keyboard toggle in Web Cinema).
        public static void SetLookDownExtra(string icon, System.Action onClick)
        {
            s_ExtraLookDownIcon = icon;
            s_ExtraLookDownAction = onClick;
            s_Instance?.RefreshLookDownLayout();
        }

        public static void ClearLookDownExtra()
        {
            s_ExtraLookDownIcon = null;
            s_ExtraLookDownAction = null;
            s_Instance?.RefreshLookDownLayout();
        }

        void RefreshLookDownLayout()
        {
            if (m_LookDownButton == null || m_LookDownAppsBtn == null || m_LookDownRecenterBtn == null ||
                m_LookDownStowBtn == null || m_LookDownExtraBtn == null)
                return;
            var hasExtra = s_ExtraLookDownAction != null && !string.IsNullOrEmpty(s_ExtraLookDownIcon);

            var active = new List<M3Button>
            {
                m_LookDownAppsBtn,
                m_LookDownRecenterBtn,
                m_LookDownStowBtn,
            };
            m_LookDownExtraBtn.gameObject.SetActive(hasExtra);
            if (hasExtra)
            {
                var iconTmp = m_LookDownExtraBtn.GetComponentInChildren<TMP_Text>(true);
                if (iconTmp != null)
                    iconTmp.text = M3Icons.Glyph(s_ExtraLookDownIcon);
                active.Add(m_LookDownExtraBtn);
            }

            const float step = 68f;
            var width = 22f + active.Count * step;
            m_LookDownButton.sizeDelta = new Vector2(width, 72f);
            var startX = -(active.Count - 1) * step * 0.5f;
            for (var i = 0; i < active.Count; i++)
                ((RectTransform)active[i].transform).anchoredPosition = new Vector2(startX + i * step, 0f);
            RefreshLookDownStowButton();
        }

        void RefreshLookUpLayout()
        {
            if (m_LookUpBar == null || m_LookUpAppsBtn == null || m_LookUpEnvBtn == null ||
                m_LookUpEnvNextBtn == null || m_LookUpTorchBtn == null ||
                m_LookUpMediaBtn == null || m_LookUpNotifBtn == null)
                return;

            var hasEnv = EnvironmentManager.IsIsolated;
            var active = new List<M3Button>
            {
                m_LookUpAppsBtn,
                m_LookUpEnvBtn,
            };
            m_LookUpEnvNextBtn.gameObject.SetActive(hasEnv);
            if (hasEnv)
                active.Add(m_LookUpEnvNextBtn);
            active.Add(m_LookUpTorchBtn);
            active.Add(m_LookUpMediaBtn);
            active.Add(m_LookUpNotifBtn);

            const float step = 56f;
            var totalW = 129f + active.Count * step;
            m_LookUpBar.sizeDelta = new Vector2(totalW, 72f);
            var left = -totalW * 0.5f + 16f;
            if (m_LookUpClockGroup != null)
                m_LookUpClockGroup.anchoredPosition = new Vector2(left + 40f, 0f);
            if (m_LookUpDivider != null)
                m_LookUpDivider.anchoredPosition = new Vector2(left + 92.5f, 0f);

            var startX = left + 129f;
            for (var i = 0; i < active.Count; i++)
                ((RectTransform)active[i].transform).anchoredPosition = new Vector2(startX + i * step, 0f);

            SyncQuickToggleStates();
        }

        void OnLookUpAppsClicked()
        {
            ShowLookUpBar(false);
            SettingsPanelHub.OpenApps();
        }

        void OnLookUpEnvClicked()
        {
            ShowLookUpBar(false);
            SettingsPanelHub.OpenEnvironments();
        }

        void OnLookUpEnvExitClicked()
        {
            EnvironmentManager.ExitEnvironmentGlobal();
            RefreshLookUpLayout();
        }

        void OnLookUpTorchClicked()
        {
            TorchManager.ToggleGlobal();
            SyncQuickToggleStates();
        }

        void OnLookUpMediaClicked()
        {
            ShowLookUpBar(false);
            SettingsPanelHub.OpenMediaPanel();
        }

        void OnLookUpNotifClicked()
        {
            ShowLookUpBar(false);
            SettingsPanelHub.OpenNotificationCenter();
        }

        void RefreshLookDownStowButton()
        {
            if (m_LookDownStowBtn == null)
                return;
            var stowed = SpatialWindowAnchor.AreFloatingStowed;
            m_LookDownStowBtn.Selected = stowed;
            var iconTmp = m_LookDownStowBtn.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (iconTmp != null)
                iconTmp.text = M3Icons.Glyph(stowed ? "visibility" : "visibility_off");
        }

        void OnLookDownStowClicked()
        {
            SpatialWindowAnchor.ToggleFloatingStowed();
            RefreshLookDownStowButton();
            ShowLookDown(false);
        }

        void OnLookDownAppsClicked()
        {
            ShowLookDown(false);
            Debug.Log("[Settings] Clicked look-down Apps button");
            SettingsPanelHub.OpenApps();
        }

        void OnLookDownRecenterClicked()
        {
            ShowLookDown(false);
            Debug.Log("[Settings] Clicked look-down Recenter All button");
            WatchGrabController.Release();
            var gaze = m_Fusion != null ? m_Fusion.GetComponentInChildren<GazePointer>() : null;
            if (gaze != null)
                gaze.CursorAnglesDeg = Vector2.zero;
            FloatingWindowHandle.RestoreClosedPanels();

            if (IsOpen)
            {
                m_PanelYaw = HeadYaw();
                m_PanelPitchOffset = 0f;
                m_Following = false;
                m_YawSpeed = 0f;
                m_Center = m_Fusion.transform.position;
                PlacePanel();
            }

            if (RecenterAction != null)
            {
                RecenterAction.Invoke();
                return;
            }

            RecenterActiveCanvases();
        }

        void RecenterActiveCanvases()
        {
            if (m_Fusion == null)
                return;
            var head = m_Fusion.transform;
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var baseRot = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;

            var panels = new System.Collections.Generic.List<RectTransform>();
            var canvases = GazeCanvas.Active;
            for (var i = 0; i < canvases.Count; i++)
            {
                var c = canvases[i];
                if (c == null || !c.gameObject.activeInHierarchy)
                    continue;
                if (IsLookDownTarget(c.gameObject) || IsLookUpTarget(c.gameObject) ||
                    IsPalmButtonTarget(c.gameObject) || IsOnPanel(c.gameObject) ||
                    FloatingWindowHandle.IsExcluded(c.transform))
                    continue;
                if (c.transform is RectTransform rt && rt.rect.width >= 180f)
                    panels.Add(rt);
            }

            const float stepYaw = 38f;
            var startYaw = -(panels.Count - 1) * stepYaw * 0.5f;
            for (var i = 0; i < panels.Count; i++)
            {
                var rt = panels[i];
                var dist = Mathf.Clamp((rt.position - head.position).magnitude, 1.35f, 2.25f);
                var yaw = startYaw + i * stepYaw;
                var pos = head.position + baseRot * Quaternion.Euler(-2f, yaw, 0f) * Vector3.forward * dist;
                rt.SetPositionAndRotation(pos, M3Theme.FacingRotation(pos, head.position));
                SpatialWindowAnchor.PinAtCurrentPose(rt);
            }
        }

        void OnLookDownExtraClicked()
        {
            ShowLookDown(false);
            Debug.Log($"[Settings] Clicked look-down extra button ({s_ExtraLookDownIcon})");
            s_ExtraLookDownAction?.Invoke();
        }

        public static bool IsLookDownTarget(GameObject target) =>
            s_Instance != null && s_Instance.m_LookDownButton != null && target != null &&
            target.transform.IsChildOf(s_Instance.m_LookDownButton);

        public static bool IsLookUpTarget(GameObject target) =>
            s_Instance != null && s_Instance.m_LookUpBar != null && target != null &&
            target.transform.IsChildOf(s_Instance.m_LookUpBar);

        public static bool IsPalmButtonTarget(GameObject target) =>
            s_Instance != null && s_Instance.m_Button != null && target != null &&
            target.transform.IsChildOf(s_Instance.m_Button);

        public static bool IsOnPanel(GameObject target)
        {
            if (s_Instance == null || !s_Instance.IsOpen || target == null)
                return false;
            var t = target.transform;
            return (s_Instance.m_Panel != null && t.IsChildOf(s_Instance.m_Panel)) ||
                   (s_Instance.m_Above != null && t.IsChildOf(s_Instance.m_Above));
        }

        public static void SetWatchGrabbed(bool grabbed)
        {
            if (s_Instance == null)
                return;
            s_Instance.m_WatchGrabbed = grabbed;
            s_Instance.m_CenterVelocity = Vector3.zero;
            if (grabbed)
            {
                s_Instance.m_Following = false;
                s_Instance.m_YawSpeed = 0f;
                if (s_Instance.m_Fusion != null)
                    s_Instance.m_Center = s_Instance.m_Fusion.transform.position;
            }
        }

        public static void ApplyWatchGrab(float deltaYawDeg, float deltaPitchDeg, float deltaDistMeters)
        {
            if (s_Instance == null || !s_Instance.IsOpen)
                return;
            var headPosition = s_Instance.m_Fusion != null
                ? s_Instance.m_Fusion.transform.position
                : s_Instance.m_Center;
            ApplyWatchGrab(headPosition, deltaYawDeg, deltaPitchDeg, deltaDistMeters);
        }

        public static void ApplyWatchGrab(Vector3 headPosition, float deltaYawDeg, float deltaPitchDeg, float deltaDistMeters)
        {
            if (s_Instance == null || !s_Instance.IsOpen)
                return;
            s_Instance.m_Center = headPosition;
            s_Instance.m_PanelYaw += deltaYawDeg;
            s_Instance.m_PanelPitchOffset = Mathf.Clamp(s_Instance.m_PanelPitchOffset + deltaPitchDeg, -35f, 45f);
            s_Instance.m_Distance = Mathf.Clamp(s_Instance.m_Distance + deltaDistMeters, 0.55f, 2.40f);
            s_Instance.PlacePanel();
        }

        void OnEnable()
        {
            SettingsPanelHub.OpenRequested += Open;
            SettingsPanelHub.EnvironmentsOpenChanged += OnQuickToggleChanged;
            EnvironmentManager.IsolationChanged += OnQuickToggleChanged;
            TorchManager.TorchChanged += OnQuickToggleChanged;
            SettingsPanelHub.ReorderModeChanged += OnQuickToggleChanged;
            SettingsPanelHub.UnreadNotificationsChanged += OnUnreadCountChanged;
            SettingsPanelHub.MediaStateChanged += OnQuickToggleChanged;
            SyncQuickToggleStates();
        }

        void OnDisable()
        {
            SettingsPanelHub.OpenRequested -= Open;
            SettingsPanelHub.EnvironmentsOpenChanged -= OnQuickToggleChanged;
            EnvironmentManager.IsolationChanged -= OnQuickToggleChanged;
            TorchManager.TorchChanged -= OnQuickToggleChanged;
            SettingsPanelHub.ReorderModeChanged -= OnQuickToggleChanged;
            SettingsPanelHub.UnreadNotificationsChanged -= OnUnreadCountChanged;
            SettingsPanelHub.MediaStateChanged -= OnQuickToggleChanged;
            ShowButton(false);
            ShowLookDown(false);
            ShowLookUpBar(false);
        }

        void OnQuickToggleChanged(bool _)
        {
            SyncQuickToggleStates();
            RefreshLookDownLayout();
            if (m_LookUpShown)
                RefreshLookUpLayout();
        }

        void OnUnreadCountChanged(int _) => SyncUnreadBadges();

        void SyncQuickToggleStates()
        {
            SyncPalmQuickToggles();
            if (m_LookUpEnvBtn != null)
                m_LookUpEnvBtn.Selected = EnvironmentManager.IsIsolated || SettingsPanelHub.IsEnvironmentsOpen;
            if (m_LookUpTorchBtn != null)
                m_LookUpTorchBtn.Selected = TorchManager.IsTorchOn;
            if (m_LookUpMediaBtn != null)
                m_LookUpMediaBtn.Selected = SettingsPanelHub.IsMediaPlaying;
            if (m_ToolbarMediaBtn != null)
                m_ToolbarMediaBtn.Selected = SettingsPanelHub.IsMediaPlaying;
            SyncUnreadBadges();
            UpdateClock(force: true);
        }

        void SyncUnreadBadges()
        {
            var count = SettingsPanelHub.UnreadNotificationCount;
            var hasUnread = count > 0;
            var badgeLabel = count > 9 ? "9+" : count.ToString();
            if (m_LookUpNotifBtn != null)
                m_LookUpNotifBtn.Selected = hasUnread;
            if (m_LookUpNotifBadge != null)
                m_LookUpNotifBadge.SetActive(hasUnread);
            if (m_LookUpNotifBadgeText != null)
                m_LookUpNotifBadgeText.text = badgeLabel;
            if (m_ToolbarNotifBadge != null)
                m_ToolbarNotifBadge.SetActive(hasUnread);
            if (m_ToolbarNotifBadgeText != null)
                m_ToolbarNotifBadgeText.text = badgeLabel;
        }

        public static string FormatBatteryText(float batteryLevel01, BatteryStatus status)
        {
            if (batteryLevel01 < 0f)
                return "";
            var pct = Mathf.RoundToInt(Mathf.Clamp01(batteryLevel01) * 100f);
            return $"{pct}%";
        }

        public static Color GetBatteryColor(float batteryLevel01, BatteryStatus status)
        {
            if (status == BatteryStatus.Charging || status == BatteryStatus.Full)
                return new Color(0.42f, 0.90f, 0.60f, 1f);
            if (batteryLevel01 >= 0f && batteryLevel01 <= 0.20f)
                return M3Theme.Error;
            return M3Theme.OnSurfaceVariant;
        }

        void UpdateClock(bool force = false)
        {
            var now = DateTime.Now;
            var minuteKey = now.Hour * 60 + now.Minute;
            if (force || minuteKey != m_LastClockMinute)
            {
                m_LastClockMinute = minuteKey;
                var formatted = now.ToString("HH:mm");
                if (m_LookUpClockText != null)
                    m_LookUpClockText.text = formatted;
                if (m_ToolbarClockText != null)
                    m_ToolbarClockText.text = formatted;
            }

            if ((m_LookUpBatteryText != null || m_ToolbarBatteryText != null) && (force || Time.unscaledTime >= m_NextBatteryPollAt))
            {
                m_NextBatteryPollAt = Time.unscaledTime + 10f;
                var level = SystemInfo.batteryLevel;
                var status = SystemInfo.batteryStatus;
                var pct = level < 0f ? -1 : Mathf.RoundToInt(Mathf.Clamp01(level) * 100f);
                var charging = status == BatteryStatus.Charging || status == BatteryStatus.Full;
                if (force || pct != m_LastBatteryPercent || charging != m_LastBatteryCharging)
                {
                    m_LastBatteryPercent = pct;
                    m_LastBatteryCharging = charging;
                    var batText = FormatBatteryText(level, status);
                    var hasBat = !string.IsNullOrEmpty(batText);
                    var batColor = GetBatteryColor(level, status);
                    if (m_LookUpBatteryText != null)
                    {
                        m_LookUpBatteryText.gameObject.SetActive(hasBat);
                        m_LookUpBatteryText.text = batText;
                        m_LookUpBatteryText.color = batColor;
                    }
                    if (m_ToolbarBatteryText != null)
                    {
                        m_ToolbarBatteryText.gameObject.SetActive(hasBat);
                        m_ToolbarBatteryText.text = batText;
                        m_ToolbarBatteryText.color = batColor;
                    }
                }
            }
        }

        void SyncPalmQuickToggles()
        {
            if (m_PalmTorchButton != null)
                m_PalmTorchButton.Selected = TorchManager.IsTorchOn;
            if (m_PalmReorderButton != null)
                m_PalmReorderButton.Selected = SettingsPanelHub.IsReorderMode;
        }

        void OnDestroy()
        {
            if (m_LookDownButton != null)
                Destroy(m_LookDownButton.gameObject);
            if (m_LookUpBar != null)
                Destroy(m_LookUpBar.gameObject);
            if (s_Instance == this)
                s_Instance = null;
        }

        // Opening the palm now displays the miniature Home / App Library directly on the palm
        // instead of a separate gear icon, so pinches click its buttons directly.
        public static bool TryConsumePinch() => false;

        // Opens the Home / Settings panel in floating mode (always following the user, never anchored).
        public void Open()
        {
            CaptureNormalScales();
            m_PalmMode = false;
            PalmModeChanged?.Invoke(false);
            ApplyScaleMode(palmMode: false);
            m_PanelYaw = HeadYaw();
            m_PanelPitchOffset = 0f;
            m_Following = false;
            m_WatchGrabbed = false;
            m_YawSpeed = 0f;
            m_Center = m_Fusion.transform.position;
            m_CenterVelocity = Vector3.zero;
            PlacePanel();
            var wasOpen = IsOpen;
            M3Fade.Show(m_Panel.gameObject);
            UiRenderQueue.ApplyRecursive(m_Panel, UiRenderQueue.k_PalmOverlayQueue);
            SyncQuickToggleStates();
            if (!wasOpen)
            {
                Debug.Log("[Settings] Opened (floating follow mode)");
                OpenChanged?.Invoke(true);
            }
            ShowButton(false);
            ShowLookDown(false);
            ShowLookUpBar(false);
            m_Palm.Reset();
        }

        // Opens the compact vertical smartphone Home / App Library directly between palm and fingers,
        // dynamically matched to the real physical width and orientation of the hand.
        void OpenOnPalm(Pose targetPose, float palmWidthMeters)
        {
            CaptureNormalScales();
            m_PalmMode = true;
            m_SmoothPalmWidth = Mathf.Clamp(palmWidthMeters, 0.055f, 0.12f);
            m_WatchGrabbed = false;
            m_Following = false;
            m_HasPalmRot = false;
            m_ButtonFilter.Reset();
            var wasOpen = IsOpen;
            M3Fade.Show(m_Panel.gameObject);
            if (!wasOpen)
            {
                Debug.Log($"[Settings] Opened vertical smartphone App Library on open palm (palmWidth={m_SmoothPalmWidth * 100f:F1} cm)");
                OpenChanged?.Invoke(true);
            }
            PalmOpened?.Invoke();
            UiRenderQueue.ApplyRecursive(m_Panel, UiRenderQueue.k_PalmOverlayQueue);
            // Apply palm smartphone scale & hide horizontal toolbar AFTER AppLibrary switches to portrait 2x3.
            ApplyScaleMode(palmMode: true);
            PlaceOnPalm(targetPose, m_SmoothPalmWidth);
            ShowButton(false);
            ShowLookDown(false);
            ShowLookUpBar(false);
        }

        // Transfers the vertical smartphone App Library off the palm into a full horizontal floating
        // window + toolbar that continuously follows the user until closed back to the hand.
        public void DetachToFloating()
        {
            if (!IsOpen)
                return;
            m_PalmMode = false;
            PalmModeChanged?.Invoke(false);
            ApplyScaleMode(palmMode: false);
            m_PanelYaw = HeadYaw();
            m_PanelPitchOffset = 0f;
            m_Following = false;
            m_WatchGrabbed = false;
            m_YawSpeed = 0f;
            m_Center = m_Fusion.transform.position;
            m_CenterVelocity = Vector3.zero;
            PlacePanel();
            m_Palm.Reset();
            Debug.Log("[Settings] Detached Home / App Library from palm to floating follow mode");
        }

        void ApplyScaleMode(bool palmMode)
        {
            CaptureNormalScales();
            if (m_Panel != null)
            {
                if (palmMode)
                    ApplyPalmHandScale(m_SmoothPalmWidth);
                else
                    m_Panel.localScale = m_NormalPanelScale;
                M3Fade.SyncBaseScale(m_Panel.gameObject);
            }
            // m_Above (App Library) is a child of m_Panel, so its localScale must stay 1.0
            // (it inherits m_Panel's scale automatically).
            if (m_Above != null)
            {
                m_Above.localScale = Vector3.one;
                M3Fade.SyncBaseScale(m_Above.gameObject);
            }
            // Hide the wide horizontal settings bar while on the palm so only the sleek vertical
            // smartphone App Library + centered "Fluttuante" pill hover above the hand.
            if (m_PanelBackground != null)
                m_PanelBackground.enabled = !palmMode;
            SetToolbarHiddenForPalm(palmMode);
            if (m_Toolbar != null)
            {
                m_Toolbar.enabled = !palmMode;
                if (!palmMode)
                    m_Toolbar.Layout();
            }
            if (m_DetachButton != null)
                m_DetachButton.gameObject.SetActive(palmMode);
            if (m_PalmTorchButton != null)
                m_PalmTorchButton.gameObject.SetActive(palmMode);
            if (m_PalmReorderButton != null)
                m_PalmReorderButton.gameObject.SetActive(palmMode);
            if (palmMode)
                SyncPalmQuickToggles();
            if (m_CloseButton != null)
                m_CloseButton.SetActive(!palmMode);
        }

        void SetToolbarHiddenForPalm(bool hide)
        {
            if (hide)
            {
                if (m_HiddenForPalm.Count > 0)
                    return;
                for (var i = 0; i < m_ToolbarChildren.Count; i++)
                {
                    var child = m_ToolbarChildren[i];
                    if (child != null && child.activeSelf)
                    {
                        m_HiddenForPalm.Add(child);
                        child.SetActive(false);
                    }
                }
                return;
            }
            for (var i = 0; i < m_HiddenForPalm.Count; i++)
            {
                if (m_HiddenForPalm[i] != null)
                    m_HiddenForPalm[i].SetActive(true);
            }
            m_HiddenForPalm.Clear();
        }

        void ApplyPalmHandScale(float palmWidthMeters)
        {
            if (m_Panel == null)
                return;
            var targetWidth = Mathf.Clamp(palmWidthMeters * k_PalmWidthScale, k_MinPalmPhoneWidth, k_MaxPalmPhoneWidth);
            var dpWidth = m_Above != null && m_Above.rect.width > 100f ? m_Above.rect.width : 288f;
            var unit = targetWidth / dpWidth;
            m_Panel.localScale = Vector3.one * unit;
            M3Fade.SyncBaseScale(m_Panel.gameObject);
        }

        // The panel's X button (returns it to the hand), turning the palm away, or switching scenes.
        public void Close()
        {
            if (!IsOpen)
                return;
            var wasPalm = m_PalmMode;
            m_PalmMode = false;
            m_HasPalmRot = false;
            if (m_PanelBackground != null)
                m_PanelBackground.enabled = true;
            SetToolbarHiddenForPalm(false);
            if (m_Toolbar != null)
            {
                m_Toolbar.enabled = true;
                m_Toolbar.Layout();
            }
            if (m_DetachButton != null)
                m_DetachButton.gameObject.SetActive(false);
            if (m_PalmTorchButton != null)
                m_PalmTorchButton.gameObject.SetActive(false);
            if (m_PalmReorderButton != null)
                m_PalmReorderButton.gameObject.SetActive(false);
            if (m_CloseButton != null)
                m_CloseButton.SetActive(true);
            M3Fade.Hide(m_Panel.gameObject);
            if (!wasPalm)
                m_Palm.Reset();
            Debug.Log($"[Settings] Closed (returned to hand, wasPalm={wasPalm})");
            OpenChanged?.Invoke(false);
        }

        // Which way the head faces around the vertical, in degrees.
        float HeadYaw()
        {
            var flat = Vector3.ProjectOnPlane(m_Fusion.transform.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : m_PanelYaw;
        }

        // How far the head can turn from the panel before it follows: past its edge, as seen from the
        // head, by k_FollowMargin. It narrows in the lobby, where the toolbar has fewer items.
        float DeadZone()
        {
            var halfWidth = m_Panel.rect.width / 2f * m_Panel.lossyScale.x;
            return Mathf.Atan2(halfWidth, M3Theme.k_OverlayDistance) * Mathf.Rad2Deg + k_FollowMargin;
        }

        // Where the panel's heading should be next: unchanged while the head is within the dead zone,
        // then springing toward the head until it's nearly there and nearly still. Pure, for testing.
        public static float FollowYaw(float panelYaw, float headYaw, float deadZone, ref bool following, ref float speed,
            float deltaTime)
        {
            var off = Mathf.Abs(Mathf.DeltaAngle(panelYaw, headYaw));
            if (!following && off > deadZone)
                following = true;
            if (following && off < k_FollowSettled && Mathf.Abs(speed) < 5f)
            {
                following = false;
                speed = 0f;
            }
            if (!following)
                return panelYaw;
            return Mathf.SmoothDampAngle(panelYaw, headYaw, ref speed, k_FollowSmoothTime, k_FollowMaxSpeed, deltaTime);
        }

        // Optional scroll handler for the panel above (AppLibrary): returns true if it scrolled pages.
        public static System.Func<GameObject, float, bool> AboveScrollHandler { get; set; }

        // Handles Watch crown on AppLibrary (page turn only when looking at multi-page AppLibrary).
        public static bool TryHandleRotary(GameObject gazeTarget, float rotarySteps)
        {
            if (s_Instance == null || !s_Instance.IsOpen || gazeTarget == null)
                return false;
            return AboveScrollHandler != null && AboveScrollHandler(gazeTarget, rotarySteps * 0.16f);
        }

        // M3Theme.k_OverlayDistance from the head along the panel's heading, a little below eye level, facing it.
        void PlacePanel() => Place(m_Panel, m_Above, m_Center, m_PanelYaw, m_Distance, m_PanelPitchOffset);

        public void RefreshPlacement()
        {
            if (IsOpen && !m_PalmMode && m_Panel != null)
                PlacePanel();
        }

        // How far below the line of sight the panel goes, in degrees, for a panel and the one above it
        // of these heights in dp: k_MinPanelDrop, or enough that they clear each other by k_PanelGap.
        public static float PanelDrop(float panelHeight, float aboveHeight)
        {
            var dpDegrees = Mathf.Atan(M3Theme.DpToMeters(1f, M3Theme.k_OverlayDistance) / M3Theme.k_OverlayDistance) * Mathf.Rad2Deg;
            return Mathf.Max(k_MinPanelDrop, (aboveHeight / 2f + panelHeight / 2f) * dpDegrees + k_PanelGap);
        }

        // The panel below the line of sight from a head at center, facing it, and what's above it at
        // eye level, strictly upright (0 deg roll).
        public static void Place(RectTransform panel, RectTransform above, Vector3 center, float yaw,
            float distance = M3Theme.k_OverlayDistance, float pitchOffset = 0f)
        {
            var drop = PanelDrop(panel.rect.height, above != null ? above.rect.height : 0f);
            var direction = Quaternion.Euler(drop - pitchOffset, yaw, 0f) * Vector3.forward;
            var panelPos = center + direction * distance;
            panel.SetPositionAndRotation(panelPos, M3Theme.FacingRotation(panelPos, center, 16f));
            if (above == null)
                return;
            var ahead = Quaternion.Euler(-pitchOffset, yaw, 0f) * Vector3.forward;
            var abovePos = center + ahead * distance;
            above.SetPositionAndRotation(abovePos, M3Theme.FacingRotation(abovePos, center, 10f));
        }

        // Computes a natural handheld smartphone pose nestled between palm and proximal fingers in 3D,
        // tilting and rotating faithfully with the user's hand. Pure, for testing.
        public static Pose ComputePalmPhonePose3D(IReadOnlyList<Vector3> joints, bool rightHand, Vector3 eyes)
        {
            if (joints == null || joints.Count < HandGeometry.JointCount)
                return new Pose(eyes + Vector3.forward * 0.4f, Quaternion.identity);

            var wrist = joints[PalmGesture.k_Wrist];
            var indexMcp = joints[PalmGesture.k_IndexKnuckle];
            var middleMcp = joints[k_MiddleKnuckle];
            var pinkyMcp = joints[PalmGesture.k_PinkyKnuckle];
            var indexPip = joints[6];
            var middlePip = joints[10];
            var ringPip = joints[14];

            var palmMid = wrist * 0.30f + indexMcp * 0.25f + middleMcp * 0.25f + pinkyMcp * 0.20f;
            var fingerBase = (indexPip + middlePip + ringPip) / 3f;
            var surfacePoint = Vector3.Lerp(palmMid, fingerBase, 0.45f);

            var handUp = middlePip - wrist;
            if (handUp.sqrMagnitude < 1e-6f)
                handUp = middleMcp - wrist;
            if (handUp.sqrMagnitude < 1e-6f)
                handUp = Vector3.up;
            handUp.Normalize();

            var toEyes = (eyes - surfacePoint).sqrMagnitude > 1e-6f ? (eyes - surfacePoint).normalized : -Vector3.forward;
            var across = indexMcp - pinkyMcp;
            var palmNormal = across.sqrMagnitude > 1e-6f ? Vector3.Cross(handUp, across) : toEyes;
            if (palmNormal.sqrMagnitude < 1e-6f)
                palmNormal = toEyes;
            palmNormal.Normalize();
            if (Vector3.Dot(palmNormal, toEyes) < 0f)
                palmNormal = -palmNormal;

            var pos = surfacePoint + palmNormal * k_PalmHoverForward;
            var viewForward = (pos - eyes).sqrMagnitude > 1e-6f ? (pos - eyes).normalized : Vector3.forward;
            var lookDir = Vector3.Slerp(viewForward, -palmNormal, 0.55f).normalized;
            var upProj = Vector3.ProjectOnPlane(handUp, lookDir);
            if (upProj.sqrMagnitude < 1e-6f)
                upProj = Vector3.up;
            else
                upProj.Normalize();

            return new Pose(pos, Quaternion.LookRotation(lookDir, upProj));
        }

        // Computes the handheld smartphone pose between palm and fingers from 2D image landmarks (3DoF / Ultrawide),
        // rotating cleanly as the user twists or rotates their hand. Pure, for testing.
        public static Pose ComputePalmPhonePose2D(IReadOnlyList<Vector2> landmarks, Vector2 imageSize,
            float horizontalFovDeg, float verticalFovDeg, float estimatedDistanceMeters,
            Vector3 headPos, Quaternion headRot, bool flipImage)
        {
            if (landmarks == null || landmarks.Count < HandGeometry.JointCount || imageSize.x <= 1f || imageSize.y <= 1f)
                return new Pose(headPos + headRot * (Vector3.forward * estimatedDistanceMeters), headRot);

            var wrist = landmarks[PalmGesture.k_Wrist];
            var indexMcp = landmarks[PalmGesture.k_IndexKnuckle];
            var middleMcp = landmarks[k_MiddleKnuckle];
            var pinkyMcp = landmarks[PalmGesture.k_PinkyKnuckle];
            var indexPip = landmarks[6];
            var middlePip = landmarks[10];
            var ringPip = jointsOrFallback2D(landmarks, 14);

            var palmMidPx = wrist * 0.30f + indexMcp * 0.25f + middleMcp * 0.25f + pinkyMcp * 0.20f;
            var fingerBasePx = (indexPip + middlePip + ringPip) / 3f;
            var centerPx = Vector2.Lerp(palmMidPx, fingerBasePx, 0.45f);

            var frac = new Vector2(centerPx.x / imageSize.x, centerPx.y / imageSize.y);
            if (flipImage)
                frac = Vector2.one - frac;

            var ray = PalmGesture.Ray(frac, horizontalFovDeg, verticalFovDeg);
            var surfacePoint = headPos + headRot * ray * estimatedDistanceMeters;
            var toEyes = (headPos - surfacePoint).sqrMagnitude > 1e-6f
                ? (headPos - surfacePoint).normalized
                : -(headRot * Vector3.forward);
            var pos = surfacePoint + toEyes * k_PalmHoverForward;
            var viewForward = -toEyes;

            var dirPx = middlePip - wrist;
            if (dirPx.sqrMagnitude < 1f)
                dirPx = middleMcp - wrist;
            var localUp2D = flipImage
                ? new Vector3(-dirPx.x, dirPx.y, 0f)
                : new Vector3(dirPx.x, -dirPx.y, 0f);
            var worldUp = localUp2D.sqrMagnitude > 1e-5f
                ? headRot * localUp2D.normalized
                : headRot * Vector3.up;
            var upProj = Vector3.ProjectOnPlane(worldUp, viewForward);
            if (upProj.sqrMagnitude < 1e-6f)
                upProj = Vector3.up;
            else
                upProj.Normalize();

            return new Pose(pos, Quaternion.LookRotation(viewForward, upProj));
        }

        static Vector2 jointsOrFallback2D(IReadOnlyList<Vector2> lm, int idx) =>
            idx < lm.Count ? lm[idx] : lm[9];

        void PlaceOnPalm(Pose targetPose, float palmWidthMeters)
        {
            if (m_Fusion == null || m_Panel == null)
                return;
            var dt = Time.unscaledDeltaTime;
            var clampedWidth = Mathf.Clamp(palmWidthMeters, 0.055f, 0.12f);
            m_SmoothPalmWidth = Mathf.Lerp(m_SmoothPalmWidth, clampedWidth, 1f - Mathf.Exp(-dt * 8f));
            ApplyPalmHandScale(m_SmoothPalmWidth);

            var phoneCenter = m_ButtonFilter.Filter(targetPose.position, Time.unscaledTimeAsDouble);
            m_PalmRot = m_HasPalmRot
                ? Quaternion.Slerp(m_PalmRot, targetPose.rotation, 1f - Mathf.Exp(-dt * 14f))
                : targetPose.rotation;
            m_HasPalmRot = true;
            var rot = m_PalmRot;

            var worldUnit = m_Panel.lossyScale.y;
            var detachHalfH = M3.k_ButtonHeight * 0.5f * worldUnit;
            var aboveHalfH = m_Above != null ? m_Above.rect.height * 0.5f * worldUnit : 0f;
            var up = rot * Vector3.up;
            var panelPos = phoneCenter - up * (aboveHalfH + detachHalfH + k_PalmDetachGap);

            m_Panel.SetPositionAndRotation(panelPos, rot);
            if (m_Above != null)
                m_Above.SetPositionAndRotation(phoneCenter, rot);
        }

        void LateUpdate()
        {
            // Floating Home / App Library ALWAYS follows the user smoothly (never anchored in room space).
            if (!IsOpen || m_PalmMode || m_WatchGrabbed || m_Fusion == null)
                return;
            var dt = Time.unscaledDeltaTime;
            m_Center = Vector3.SmoothDamp(m_Center, m_Fusion.transform.position, ref m_CenterVelocity, k_PositionSmoothTime,
                Mathf.Infinity, dt);
            m_PanelYaw = FollowYaw(m_PanelYaw, HeadYaw(), DeadZone(), ref m_Following, ref m_YawSpeed, dt);
            PlacePanel();
        }

        public static bool ShouldShowLookUpBar(bool currentlyShown, float pitchUpDeg) =>
            currentlyShown ? pitchUpDeg >= k_LookUpHideDeg : pitchUpDeg >= k_LookUpShowDeg;

        void Update()
        {
            if (SuppressForFullscreen)
            {
                if (IsOpen)
                    Close();
                if (m_ButtonShown)
                    ShowButton(false);
                if (m_LookDownShown)
                    ShowLookDown(false);
                if (m_LookUpShown)
                    ShowLookUpBar(false);
                return;
            }
            UpdateClock();
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            var seen = false;
            var facing = 0f;
            var palmPose = new Pose(Vector3.zero, Quaternion.identity);
            var palmWidth = k_DefaultPalmWidth;
            if (sixDof && m_Tracker.isActiveAndEnabled && m_Tracker.IsTracking)
            {
                seen = TryFacing(m_Tracker.ImageLandmarks, m_Tracker.HandednessScore, out facing);
                var joints = m_Tracker.Joints;
                palmPose = ComputePalmPhonePose3D(joints, m_Tracker.HandednessScore >= 0.5f, m_Fusion.transform.position);
                var span = Vector3.Distance(joints[PalmGesture.k_IndexKnuckle], joints[PalmGesture.k_PinkyKnuckle]);
                if (span > 0.03f && span < 0.16f)
                    palmWidth = span;
            }
            else if (!sixDof && m_Sensor.isActiveAndEnabled && m_Sensor.SeesHand)
            {
                seen = TryFacing(m_Sensor.SmoothedImageLandmarks, m_Sensor.RightHandScore, out facing);
                var size = m_Sensor.ImageSize;
                var landmarks = m_Sensor.SmoothedImageLandmarks;
                var isUltrawide = PassthroughController.IsUltrawideActive;
                var flipImage = false;
                if (!isUltrawide)
                {
                    if (seen && facing > PalmFacingState.k_Hide)
                        VoteUpright(landmarks);
                    flipImage = m_UprightVote < 0f;
                }
                var hFov = m_Sensor.HorizontalFovDeg;
                var vFov = m_Sensor.VerticalFovDeg;
                var head = m_Fusion.transform;
                var estDist = Mathf.Clamp(m_Sensor.EstimatedHandDistance, 0.30f, 0.55f);
                palmPose = ComputePalmPhonePose2D(landmarks, size, hFov, vFov, estDist, head.position, head.rotation, flipImage);
                if (size.x > 1f)
                {
                    var pxSpan = Vector2.Distance(landmarks[PalmGesture.k_IndexKnuckle], landmarks[PalmGesture.k_PinkyKnuckle]);
                    var worldFullWidth = 2f * estDist * Mathf.Tan(hFov * 0.5f * Mathf.Deg2Rad);
                    var estWidth = (pxSpan / size.x) * worldFullWidth;
                    if (estWidth > 0.03f && estWidth < 0.16f)
                        palmWidth = estWidth;
                }
            }

            // Detect palm opening/closing whenever closed OR currently docked on the palm.
            // While detached as a full floating window (!m_PalmMode && IsOpen), ignore palm until closed.
            var watchPalm = !IsOpen || m_PalmMode;
            if (m_Palm.Update(seen && watchPalm, facing, Time.unscaledDeltaTime))
            {
                Debug.Log($"[Settings] Palm {(m_Palm.IsFacing ? "toward" : "away from")} the user " +
                          $"(facing {facing:F2}, {(sixDof ? "hand tracking" : "pinch sensor")})");
                if (m_Palm.IsFacing && !IsOpen)
                    OpenOnPalm(palmPose, palmWidth);
                else if (!m_Palm.IsFacing && m_PalmMode && IsOpen)
                    Close();
            }

            if (m_PalmMode && IsOpen && seen)
                PlaceOnPalm(palmPose, palmWidth);

            UpdateLookDownButton();
            UpdateLookUpBar();
        }

        void UpdateLookUpBar()
        {
            if (m_LookUpBar == null || m_Fusion == null)
                return;
            if (IsOpen)
            {
                if (m_LookUpShown)
                    ShowLookUpBar(false);
                return;
            }
            var head = m_Fusion.transform;
            var pitchUp = Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            var wantShow = ShouldShowLookUpBar(m_LookUpShown, pitchUp);
            if (!m_LookUpShown && wantShow)
            {
                m_LookUpYaw = PreferredLookUpYawProvider?.Invoke() ?? HeadYaw();
                RefreshLookUpLayout();
                PlaceLookUpBar(head.position);
                ShowLookUpBar(true);
            }
            else if (m_LookUpShown)
            {
                if (!wantShow)
                    ShowLookUpBar(false);
                else
                    PlaceLookUpBar(head.position);
            }
        }

        void ShowLookUpBar(bool show)
        {
            m_LookUpShown = show;
            if (m_LookUpBar != null)
                M3Fade.SetShown(m_LookUpBar.gameObject, show);
        }

        void PlaceLookUpBar(Vector3 eyes)
        {
            var direction = Quaternion.Euler(-k_LookUpBarPitchDeg, m_LookUpYaw, 0f) * Vector3.forward;
            var pos = eyes + direction * k_LookUpBarDistance;
            m_LookUpBar.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - eyes, Vector3.up));
        }

        void UpdateLookDownButton()
        {
            if (m_LookDownButton == null || m_Fusion == null)
                return;
            if (IsOpen)
            {
                if (m_LookDownShown)
                    ShowLookDown(false);
                return;
            }
            var head = m_Fusion.transform;
            var pitchDown = Mathf.Asin(Mathf.Clamp(-head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            if (!m_LookDownShown && pitchDown >= k_LookDownShowDeg)
            {
                m_LookDownYaw = HeadYaw();
                RefreshLookDownLayout();
                PlaceLookDownButton(head.position);
                ShowLookDown(true);
            }
            else if (m_LookDownShown)
            {
                if (pitchDown < k_LookDownHideDeg)
                {
                    ShowLookDown(false);
                }
                else
                {
                    // Follow head translation while keeping the horizontal heading fixed so it's easy to aim at.
                    PlaceLookDownButton(head.position);
                }
            }
        }

        void ShowLookDown(bool show)
        {
            m_LookDownShown = show;
            if (m_LookDownButton != null)
                M3Fade.SetShown(m_LookDownButton.gameObject, show);
        }

        void PlaceLookDownButton(Vector3 eyes)
        {
            var direction = Quaternion.Euler(k_LookDownButtonPitchDeg, m_LookDownYaw, 0f) * Vector3.forward;
            var pos = eyes + direction * k_LookDownButtonDistance;
            m_LookDownButton.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - eyes, Vector3.up));
        }

        // A palm held up to look at has its fingers above its wrist, so in an upright image (y down) the
        // wrist is lower than the middle knuckle. The vote follows that, and settles which way up the
        // image is.
        void VoteUpright(System.Collections.Generic.IReadOnlyList<Vector2> landmarks)
        {
            var fingersUp = landmarks[PalmGesture.k_Wrist].y > landmarks[k_MiddleKnuckle].y ? 1f : -1f;
            m_UprightVote = Mathf.Clamp(m_UprightVote + fingersUp * k_VoteRate, -1f, 1f);
            if (!m_LoggedUpright && Mathf.Abs(m_UprightVote) >= 1f)
            {
                m_LoggedUpright = true;
                Debug.Log($"[Settings] The pinch sensor's image is {(m_UprightVote > 0f ? "upright" : "upside down")}, " +
                          "by where the wrist is when a palm is held up");
            }
        }

        static bool TryFacing(System.Collections.Generic.IReadOnlyList<Vector2> landmarks, float rightScore, out float facing)
        {
            facing = 0f;
            if (landmarks == null || landmarks.Count < 21)
                return false;
            facing = PalmGesture.Facing(landmarks[PalmGesture.k_Wrist], landmarks[PalmGesture.k_IndexKnuckle],
                landmarks[PalmGesture.k_PinkyKnuckle], rightScore >= 0.5f);
            return true;
        }

        void ShowButton(bool show)
        {
            m_ButtonShown = show;
            if (m_Button != null)
                M3Fade.SetShown(m_Button.gameObject, show);
            if (show)
                m_ButtonFilter.Reset();
        }

        // Between the thumb and index finger, a little toward the eyes, facing them.
        void PlaceButton(Vector3 hand)
        {
            var eyes = m_Fusion.transform.position;
            var toEyes = (eyes - hand).normalized;
            var position = m_ButtonFilter.Filter(hand + toEyes * k_InFront, Time.unscaledTimeAsDouble);
            m_Button.SetPositionAndRotation(position, Quaternion.LookRotation(position - eyes, Vector3.up));
        }
    }
}
