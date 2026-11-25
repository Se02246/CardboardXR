using System;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos
{
    // First-launch and on-demand Shizuku onboarding card. Explains the unlocked features
    // (real phone apps, interactive notifications, media controls, reliable flashlight) and
    // adapts its primary button to Shizuku's live installation/permission state.
    public class ShizukuOnboardingController : MonoBehaviour
    {
        const string k_PrefDone = "CardboardXR.ShizukuOnboardingDone";
        const float k_PanelWidth = 580f;
        const float k_PanelHeight = 456f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] SettingsSummoner m_Settings;

        RectTransform m_Panel;
        TMP_Text m_StatusText;
        M3Button m_PrimaryBtn;
        M3Button m_RefreshBtn;
        M3Button m_SkipBtn;

        Action m_OnDismissed;
        bool m_Shown;
        bool m_Subscribed;
        float m_Yaw;
        bool m_Following;
        float m_YawSpeed;
        Vector3 m_Center;
        Vector3 m_CenterVel;

        public bool IsShown => m_Shown;

        public bool ShouldShowOnStartup =>
            PlayerPrefs.GetInt(k_PrefDone, 0) == 0 &&
            !(SystemBridge.Instance != null && SystemBridge.Instance.IsShizukuReady);

        void OnEnable() => SettingsPanelHub.ShizukuSetupRequested += OnSetupRequested;

        void OnDisable() => SettingsPanelHub.ShizukuSetupRequested -= OnSetupRequested;

        void Start() => EnsureBridgeSubscribed();

        void OnDestroy()
        {
            if (m_Subscribed && SystemBridge.Instance != null)
                SystemBridge.Instance.ShizukuStatusChanged -= RefreshStatusUi;
            m_Subscribed = false;
        }

        void OnSetupRequested() => StartOnboarding(null);

        public void StartOnboarding(Action onDismissed = null)
        {
            m_OnDismissed = onDismissed;
            EnsureBuilt();
            EnsureBridgeSubscribed();

            if (m_Settings != null && m_Settings.IsOpen)
                m_Settings.Close();

            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), true);
            SystemBridge.Instance?.RefreshShizuku();

            if (m_Fusion != null)
            {
                m_Center = m_Fusion.transform.position;
                m_CenterVel = Vector3.zero;
                m_Yaw = HeadYaw();
                m_Following = false;
                m_YawSpeed = 0f;
                PlacePanel();
            }

            m_Shown = true;
            RefreshStatusUi();
            M3Fade.Show(m_Panel.gameObject);
        }

        public void Hide(bool invokeDismiss = false)
        {
            m_Shown = false;
            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), false);
            if (m_Panel != null)
                M3Fade.Hide(m_Panel.gameObject);
            if (invokeDismiss)
            {
                var cb = m_OnDismissed;
                m_OnDismissed = null;
                cb?.Invoke();
            }
        }

        void EnsureBridgeSubscribed()
        {
            if (m_Subscribed || SystemBridge.Instance == null)
                return;
            SystemBridge.Instance.ShizukuStatusChanged += RefreshStatusUi;
            m_Subscribed = true;
        }

        void EnsureBuilt()
        {
            if (m_Panel != null)
                return;

            m_Panel = M3.Canvas("Shizuku Onboarding", null, new Vector2(k_PanelWidth, k_PanelHeight),
                M3Theme.k_OverlayDistance, M3Theme.SurfaceContainerHigh, M3Theme.k_ShapePanel);
            m_Panel.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_Panel);

            M3.Icon("Badge Icon", m_Panel, "verified_user", 40f, new Vector2(0f, 178f), M3Theme.GlassAccent);

            var title = M3.Text("Title", m_Panel, "Configura Shizuku", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 134f), new Vector2(520f, 36f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            M3.Text("Body", m_Panel,
                "Per un'esperienza completa su CardboardXR attiva Shizuku.\n" +
                "Potrai aprire vere app Android, controllare media e notifiche,\n" +
                "e attivare la torcia rapidamente.",
                M3Theme.Type.BodyLarge, new Vector2(0f, 52f), new Vector2(516f, 96f),
                M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);

            var statusPill = M3.Surface("Status Pill", m_Panel, new Vector2(0f, -34f), new Vector2(500f, 52f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Recessed);
            m_StatusText = M3.Text("Status", statusPill.rectTransform, "Verifica stato Shizuku...",
                M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(468f, 36f),
                M3Theme.OnSurface, TextAlignmentOptions.Center);
            m_StatusText.textWrappingMode = TextWrappingModes.NoWrap;

            m_PrimaryBtn = M3.Button("Primary", m_Panel, "Apri Shizuku", M3Button.Variant.Filled,
                new Vector2(-92f, -108f), OnPrimaryClicked, "open_in_new", width: 280f);
            m_RefreshBtn = M3.Button("Refresh", m_Panel, "Aggiorna", M3Button.Variant.Tonal,
                new Vector2(158f, -108f), OnRefreshClicked, "refresh", width: 176f);

            m_SkipBtn = M3.Button("Skip", m_Panel, "Continua senza Shizuku", M3Button.Variant.Tonal,
                new Vector2(0f, -176f), OnSkipClicked, "arrow_forward", width: 296f);

            UiRenderQueue.ApplyRecursive(m_Panel, UiRenderQueue.k_PalmOverlayQueue);
            m_Panel.gameObject.SetActive(false);
        }

        void RefreshStatusUi()
        {
            if (m_Panel == null)
                return;
            var bridge = SystemBridge.Instance;
            var code = bridge != null ? bridge.ShizukuStatusCode : SystemBridge.StatusNotRunning;
            var notifOk = bridge != null && bridge.NotificationAccessEnabled;

            switch (code)
            {
                case SystemBridge.StatusReady:
                    m_StatusText.text = notifOk
                        ? "Shizuku e accesso notifiche attivi!"
                        : "Shizuku attivo! Premi Inizia per completare.";
                    m_PrimaryBtn.Text = "Tutto pronto — Inizia";
                    m_PrimaryBtn.Icon = "check_circle";
                    m_PrimaryBtn.Interactable = true;
                    break;
                case SystemBridge.StatusPermissionRequired:
                    m_StatusText.text = "Shizuku in esecuzione • Autorizzazione richiesta";
                    m_PrimaryBtn.Text = "Autorizza Shizuku";
                    m_PrimaryBtn.Icon = "verified_user";
                    m_PrimaryBtn.Interactable = true;
                    break;
                case SystemBridge.StatusConnecting:
                    m_StatusText.text = "Connessione a Shizuku in corso...";
                    m_PrimaryBtn.Text = "Connessione...";
                    m_PrimaryBtn.Icon = "refresh";
                    m_PrimaryBtn.Interactable = false;
                    break;
                case SystemBridge.StatusNotInstalled:
                    m_StatusText.text = "Shizuku non installato sul dispositivo";
                    m_PrimaryBtn.Text = "Apri Shizuku";
                    m_PrimaryBtn.Icon = "open_in_new";
                    m_PrimaryBtn.Interactable = true;
                    break;
                default:
                    m_StatusText.text = "Avvia il servizio Shizuku dall'app Shizuku";
                    m_PrimaryBtn.Text = "Apri Shizuku";
                    m_PrimaryBtn.Icon = "open_in_new";
                    m_PrimaryBtn.Interactable = true;
                    break;
            }
        }

        void OnPrimaryClicked()
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
            {
                MarkDoneAndClose();
                return;
            }

            switch (bridge.ShizukuStatusCode)
            {
                case SystemBridge.StatusReady:
                    if (!bridge.NotificationAccessEnabled)
                        bridge.GrantNotificationAccessViaShizuku();
                    MarkDoneAndClose();
                    break;
                case SystemBridge.StatusPermissionRequired:
                    bridge.RequestShizukuPermission();
                    break;
                default:
                    bridge.OpenShizukuApp();
                    break;
            }
        }

        void OnRefreshClicked()
        {
            SystemBridge.Instance?.RefreshShizuku();
            RefreshStatusUi();
        }

        void OnSkipClicked() => MarkDoneAndClose();

        void MarkDoneAndClose()
        {
            PlayerPrefs.SetInt(k_PrefDone, 1);
            PlayerPrefs.Save();
            Hide(invokeDismiss: true);
        }

        void LateUpdate()
        {
            if (!m_Shown || m_Panel == null)
                return;
            if (SettingsSummoner.SuppressForFullscreen)
            {
                Hide(invokeDismiss: false);
                return;
            }
            if (m_Fusion == null)
                return;

            var dt = Time.unscaledDeltaTime;
            m_Center = Vector3.SmoothDamp(m_Center, m_Fusion.transform.position, ref m_CenterVel,
                0.25f, Mathf.Infinity, dt);
            var halfWidth = k_PanelWidth * 0.5f * M3Theme.DpToMeters(1f, M3Theme.k_OverlayDistance);
            var deadZone = Mathf.Atan2(halfWidth, M3Theme.k_OverlayDistance) * Mathf.Rad2Deg + SettingsSummoner.k_FollowMargin;
            m_Yaw = SettingsSummoner.FollowYaw(m_Yaw, HeadYaw(), deadZone, ref m_Following, ref m_YawSpeed, dt);
            PlacePanel();
        }

        void PlacePanel()
        {
            var dir = Quaternion.Euler(0f, m_Yaw, 0f) * Vector3.forward;
            var pos = m_Center + dir * M3Theme.k_OverlayDistance;
            m_Panel.SetPositionAndRotation(pos, M3Theme.FacingRotation(pos, m_Center));
        }

        float HeadYaw()
        {
            if (m_Fusion == null)
                return 0f;
            var flat = Vector3.ProjectOnPlane(m_Fusion.transform.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : 0f;
        }
    }
}
