using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // Pure placement and dismissal rules for incoming head-up notifications.
    // While looking forward, notifications stay comfortably high in the top FOV;
    // when looking up, they dock smoothly above SettingsSummoner's look-up quick-toggles pill.
    public static class NotificationBannerRules
    {
        public const float k_TopFovOffsetDeg = 17f;
        public const float k_MinFloatPitchDeg = 12f;
        public const float k_DockPitchUpDeg = 46.5f;
        public const float k_DockEngagePitchDeg = 28f;
        public const float k_DockReleasePitchDeg = 20f;

        public struct BannerStep
        {
            public float PitchDeg;
            public float YawDeg;
            public bool IsDocked;
            public bool ShouldDismiss;
        }

        public static BannerStep ComputeStep(float headPitchUpDeg, float headYawDeg, bool lookUpBarShown,
            float lookUpBarYawDeg, float currentYawDeg, float ageSeconds, float timeoutSeconds,
            bool isHovered, bool wasDocked)
        {
            var dock = lookUpBarShown || (wasDocked
                ? headPitchUpDeg >= k_DockReleasePitchDeg
                : headPitchUpDeg >= k_DockEngagePitchDeg);
            if (dock)
            {
                return new BannerStep
                {
                    PitchDeg = k_DockPitchUpDeg,
                    YawDeg = lookUpBarShown ? lookUpBarYawDeg : currentYawDeg,
                    IsDocked = true,
                    ShouldDismiss = false,
                };
            }

            return new BannerStep
            {
                PitchDeg = Mathf.Clamp(headPitchUpDeg + k_TopFovOffsetDeg, k_MinFloatPitchDeg, k_DockPitchUpDeg),
                YawDeg = headYawDeg,
                IsDocked = false,
                ShouldDismiss = !isHovered && ageSeconds >= timeoutSeconds,
            };
        }
    }

    // Renders live incoming phone notifications as a sleek VisionOS top-FOV banner.
    // Looking up docks it directly above the floating quick-toggles bar so the user can click to open the app.
    public class NotificationOverlayController : MonoBehaviour
    {
        const float k_Width = 490f;
        const float k_Height = 104f;
        const float k_AutoDismissSeconds = 8.0f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] DemoManager m_Demos;
        [SerializeField] GazePointer m_Gaze;

        RectTransform m_Panel;
        RawImage m_AppIcon;
        Image m_BadgeBg;
        TMP_Text m_BadgeInitial;
        TMP_Text m_HeaderText;
        TMP_Text m_TitleText;
        TMP_Text m_BodyText;

        SystemBridge.NotificationItem m_Current;
        bool m_IsShown;
        bool m_IsDocked;
        bool m_WasDocked;
        bool m_Subscribed;
        float m_ShownAt;
        float m_CurrentPitch;
        float m_CurrentYaw;
        float m_LockedYaw;

        void OnEnable() => SettingsSummoner.PreferredLookUpYawProvider = GetPreferredLookUpYaw;

        void OnDisable()
        {
            if (SettingsSummoner.PreferredLookUpYawProvider == GetPreferredLookUpYaw)
                SettingsSummoner.PreferredLookUpYawProvider = null;
        }

        void Start() => EnsureBridgeSubscribed();

        void OnDestroy()
        {
            if (m_Subscribed && SystemBridge.Instance != null)
                SystemBridge.Instance.IncomingNotification -= OnIncomingNotification;
            m_Subscribed = false;
        }

        float? GetPreferredLookUpYaw() => m_IsShown && m_IsDocked ? m_LockedYaw : (float?)null;

        void EnsureBridgeSubscribed()
        {
            if (m_Subscribed || SystemBridge.Instance == null)
                return;
            SystemBridge.Instance.IncomingNotification += OnIncomingNotification;
            m_Subscribed = true;
        }

        void OnIncomingNotification(SystemBridge.NotificationItem item)
        {
            if (SettingsSummoner.SuppressForFullscreen)
                return;

            EnsureBuilt();
            m_Current = item;

            var appLabel = string.IsNullOrEmpty(item.AppName) ? item.PackageName : item.AppName;
            var timeStr = SystemBridge.FormatTimestamp(item.PostTimeMs);
            m_HeaderText.text = string.IsNullOrEmpty(timeStr) ? appLabel : $"{appLabel} • {timeStr}";
            m_TitleText.text = string.IsNullOrEmpty(item.Title) ? appLabel : item.Title;
            m_BodyText.text = string.IsNullOrEmpty(item.Text) ? "Nuova notifica" : item.Text;

            var iconTex = SystemBridge.Instance != null ? SystemBridge.Instance.GetAppIcon(item.PackageName) : null;
            if (iconTex != null)
            {
                m_AppIcon.texture = iconTex;
                m_AppIcon.gameObject.SetActive(true);
                m_BadgeBg.gameObject.SetActive(false);
            }
            else
            {
                m_AppIcon.gameObject.SetActive(false);
                m_BadgeBg.gameObject.SetActive(true);
                m_BadgeInitial.text = string.IsNullOrEmpty(appLabel) ? "!" : appLabel.Substring(0, 1).ToUpperInvariant();
            }

            m_ShownAt = Time.unscaledTime;
            m_IsDocked = false;
            m_WasDocked = false;

            if (m_Fusion != null)
            {
                var head = m_Fusion.transform;
                var headPitchUp = HeadPitchUp(head);
                var headYaw = HeadYaw(head);
                var step = NotificationBannerRules.ComputeStep(headPitchUp, headYaw,
                    SettingsSummoner.IsLookUpBarShown, SettingsSummoner.LookUpBarYaw, headYaw,
                    0f, k_AutoDismissSeconds, false, false);
                m_CurrentPitch = step.PitchDeg;
                m_CurrentYaw = step.YawDeg;
                m_LockedYaw = step.YawDeg;
                m_IsDocked = step.IsDocked;
                m_WasDocked = step.IsDocked;
                PlaceBanner(head.position);
            }

            m_IsShown = true;
            M3Fade.Show(m_Panel.gameObject);
        }

        void EnsureBuilt()
        {
            if (m_Panel != null)
                return;

            m_Panel = M3.Canvas("Notification Overlay", null, new Vector2(k_Width, k_Height),
                SettingsSummoner.k_LookUpBarDistance, M3Theme.SurfaceContainerHigh, M3Theme.k_ShapePanel);
            m_Panel.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_Panel);

            var hitSurface = M3.Surface("Open Hitbox", m_Panel, new Vector2(-24f, 0f), new Vector2(k_Width - 64f, k_Height),
                Color.clear, M3Theme.k_ShapePanel, raycastTarget: true, style: VisionSurfaceStyle.Pure);
            var openBtn = m_Panel.gameObject.AddComponent<Button>();
            openBtn.transition = Selectable.Transition.None;
            openBtn.targetGraphic = hitSurface;
            openBtn.onClick.AddListener(OnBannerClicked);

            m_BadgeBg = M3.Surface("Badge", m_Panel, new Vector2(-196f, 0f), new Vector2(54f, 54f),
                M3Theme.PrimaryContainer, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy);
            m_BadgeInitial = M3.Text("Initial", m_BadgeBg.rectTransform, "!", M3Theme.Type.HeadlineSmall,
                Vector2.zero, new Vector2(46f, 46f), Color.white, TextAlignmentOptions.Center);

            m_AppIcon = M3.RoundedScreen("App Icon", m_Panel, new Vector2(-196f, 0f), new Vector2(54f, 54f),
                M3Theme.k_ShapeFull, raycastTarget: false, rimStrength: 0.65f, sheenStrength: 0.35f);
            m_AppIcon.gameObject.SetActive(false);

            m_HeaderText = M3.Text("Header", m_Panel, "", M3Theme.Type.LabelLarge,
                new Vector2(14f, 28f), new Vector2(326f, 22f), M3Theme.GlassAccent, TextAlignmentOptions.Left);
            m_HeaderText.textWrappingMode = TextWrappingModes.NoWrap;

            m_TitleText = M3.Text("Title", m_Panel, "", M3Theme.Type.TitleMedium,
                new Vector2(14f, 4f), new Vector2(326f, 26f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            m_TitleText.textWrappingMode = TextWrappingModes.NoWrap;

            m_BodyText = M3.Text("Body", m_Panel, "", M3Theme.Type.BodyMedium,
                new Vector2(14f, -24f), new Vector2(326f, 28f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_BodyText.textWrappingMode = TextWrappingModes.NoWrap;

            M3.IconButton("Dismiss", m_Panel, "close", M3Button.Variant.Tonal,
                new Vector2(208f, 0f), OnDismissClicked, 36f);

            UiRenderQueue.ApplyRecursive(m_Panel, UiRenderQueue.k_PalmOverlayQueue);
            m_Panel.gameObject.SetActive(false);
        }

        void Update() => EnsureBridgeSubscribed();

        void LateUpdate()
        {
            if (!m_IsShown || m_Panel == null)
                return;
            if (SettingsSummoner.SuppressForFullscreen)
            {
                DismissBanner();
                return;
            }
            if (m_Fusion == null)
                return;

            var head = m_Fusion.transform;
            var age = Time.unscaledTime - m_ShownAt;
            var hovered = IsPointerOnBanner();
            var step = NotificationBannerRules.ComputeStep(
                HeadPitchUp(head), HeadYaw(head),
                SettingsSummoner.IsLookUpBarShown, SettingsSummoner.LookUpBarYaw,
                m_LockedYaw, age, k_AutoDismissSeconds, hovered, m_WasDocked);

            if (step.ShouldDismiss)
            {
                DismissBanner();
                return;
            }

            if (hovered)
                m_ShownAt = Time.unscaledTime - Mathf.Min(age, k_AutoDismissSeconds * 0.5f);

            m_IsDocked = step.IsDocked;
            m_WasDocked = step.IsDocked;
            if (step.IsDocked)
                m_LockedYaw = step.YawDeg;

            var t = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 14f);
            m_CurrentPitch = Mathf.Lerp(m_CurrentPitch, step.PitchDeg, t);
            m_CurrentYaw = Mathf.LerpAngle(m_CurrentYaw, step.YawDeg, t);
            PlaceBanner(head.position);
        }

        void PlaceBanner(Vector3 eyes)
        {
            var dir = Quaternion.Euler(-m_CurrentPitch, m_CurrentYaw, 0f) * Vector3.forward;
            var pos = eyes + dir * SettingsSummoner.k_LookUpBarDistance;
            m_Panel.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - eyes, Vector3.up));
        }

        bool IsPointerOnBanner()
        {
            if (m_Gaze == null || m_Gaze.Target == null || m_Panel == null)
                return false;
            return m_Gaze.Target.transform.IsChildOf(m_Panel);
        }

        void OnBannerClicked()
        {
            var pkg = m_Current.PackageName;
            var lbl = string.IsNullOrEmpty(m_Current.AppName) ? pkg : m_Current.AppName;
            DismissBanner();
            PhoneAppShortcutStore.LaunchPackage(m_Demos, pkg, lbl);
        }

        void OnDismissClicked()
        {
            if (!string.IsNullOrEmpty(m_Current.Key))
                SystemBridge.Instance?.DismissNotification(m_Current.Key);
            DismissBanner();
        }

        void DismissBanner()
        {
            m_IsShown = false;
            m_IsDocked = false;
            m_WasDocked = false;
            if (m_Panel != null)
                M3Fade.Hide(m_Panel.gameObject);
        }

        static float HeadPitchUp(Transform head) =>
            Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

        static float HeadYaw(Transform head)
        {
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : 0f;
        }
    }
}
