using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // Spatial floating Notification Center window: shows the last 25 notifications with
    // circular app icons, timestamps, clear/dismiss buttons, and one-tap app launch.
    public class NotificationCenterWindow : MonoBehaviour
    {
        const float k_Width = 590f;
        const float k_Height = 580f;
        const float k_Distance = 1.45f;
        const int k_RowsPerPage = 5;

        struct RowUi
        {
            public GameObject Root;
            public RawImage Icon;
            public Image Badge;
            public TMP_Text Initial;
            public TMP_Text AppTime;
            public TMP_Text Title;
            public TMP_Text Body;
        }

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] DemoManager m_Demos;

        RectTransform m_Panel;
        GameObject m_AccessBanner;
        TMP_Text m_EmptyText;
        M3Button m_PrevBtn;
        TMP_Text m_PageText;
        M3Button m_NextBtn;
        readonly RowUi[] m_Rows = new RowUi[k_RowsPerPage];

        bool m_Shown;
        bool m_Subscribed;
        int m_Page;

        void OnEnable() => SettingsPanelHub.NotificationCenterRequested += OpenOrBringToFront;

        void OnDisable() => SettingsPanelHub.NotificationCenterRequested -= OpenOrBringToFront;

        void Start() => EnsureBridgeSubscribed();

        void OnDestroy()
        {
            if (m_Subscribed && SystemBridge.Instance != null)
            {
                SystemBridge.Instance.NotificationsChanged -= RefreshUi;
                SystemBridge.Instance.ShizukuStatusChanged -= RefreshUi;
            }
            m_Subscribed = false;
        }

        public void Toggle()
        {
            if (m_Shown && m_Panel != null && M3Fade.IsShown(m_Panel.gameObject))
                Hide();
            else
                OpenOrBringToFront();
        }

        public void OpenOrBringToFront()
        {
            AppLibrary.DismissGlobal();
            EnsureBuilt();
            EnsureBridgeSubscribed();

            m_Shown = true;
            m_Page = 0;
            SystemBridge.Instance?.ClearUnreadCount();
            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), true);

            var head = m_Fusion != null ? m_Fusion.transform : null;
            WorldUi.BringToFront(m_Panel, head, k_Distance, 1f, 0f);
            FloatingWindowHandle.Configure(m_Panel, Hide, restoreOnRecenter: true);

            RefreshUi();
        }

        public void Hide()
        {
            m_Shown = false;
            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), false);
            if (m_Panel != null)
                M3Fade.Hide(m_Panel.gameObject);
        }

        void EnsureBridgeSubscribed()
        {
            if (m_Subscribed || SystemBridge.Instance == null)
                return;
            SystemBridge.Instance.NotificationsChanged += RefreshUi;
            SystemBridge.Instance.ShizukuStatusChanged += RefreshUi;
            m_Subscribed = true;
        }

        void EnsureBuilt()
        {
            if (m_Panel != null)
                return;

            m_Panel = WorldUi.Panel("Notification Center", null, new Vector2(k_Width, k_Height), k_Distance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapePanel);

            // Header
            M3.Icon("BellIcon", m_Panel, "notifications", 28f, new Vector2(-246f, 246f), M3Theme.GlassAccent);
            var title = M3.Text("Title", m_Panel, "Centro Notifiche", M3Theme.Type.TitleLarge,
                new Vector2(-46f, 246f), new Vector2(340f, 36f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            M3.Button("Clear", m_Panel, "Pulisci", M3Button.Variant.Tonal,
                new Vector2(206f, 246f), OnClearClicked, "delete", width: 124f);

            // Optional access banner when Android NotificationListener permission is disabled
            var accessBg = M3.Surface("Access Banner", m_Panel, new Vector2(0f, 184f), new Vector2(536f, 50f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Recessed);
            m_AccessBanner = accessBg.gameObject;
            var accessMsg = M3.Text("AccessMsg", accessBg.rectTransform, "Accesso alle notifiche disattivato",
                M3Theme.Type.BodyMedium, new Vector2(-78f, 0f), new Vector2(340f, 32f),
                M3Theme.OnSurface, TextAlignmentOptions.Left);
            accessMsg.textWrappingMode = TextWrappingModes.NoWrap;
            M3.Button("EnableAccess", accessBg.rectTransform, "Attiva", M3Button.Variant.Filled,
                new Vector2(186f, 0f), OnEnableAccessClicked, width: 128f);

            // Notification rows
            var rowY = new[] { 108f, 32f, -44f, -120f, -196f };
            for (var i = 0; i < k_RowsPerPage; i++)
                m_Rows[i] = CreateRow(i, new Vector2(0f, rowY[i]));

            m_EmptyText = M3.Text("Empty", m_Panel, "Nessuna notifica recente", M3Theme.Type.BodyLarge,
                new Vector2(0f, -40f), new Vector2(420f, 48f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);

            // Pagination footer
            const float footerY = -256f;
            m_PrevBtn = M3.IconButton("PrevPage", m_Panel, "chevron_left", M3Button.Variant.Tonal,
                new Vector2(-116f, footerY), () => ChangePage(-1), 40f);
            m_PageText = M3.Text("PageText", m_Panel, "Pagina 1 / 1", M3Theme.Type.LabelLarge,
                new Vector2(0f, footerY), new Vector2(150f, 30f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_NextBtn = M3.IconButton("NextPage", m_Panel, "chevron_right", M3Button.Variant.Tonal,
                new Vector2(116f, footerY), () => ChangePage(1), 40f);

            m_Panel.gameObject.AddComponent<NotificationCenterScrollTarget>().Initialize(this);
            m_Panel.gameObject.SetActive(false);
        }

        sealed class NotificationCenterScrollTarget : MonoBehaviour, IWatchScrollable
        {
            NotificationCenterWindow m_Window;
            float m_Accum;
            float m_LastTurnAt = float.NegativeInfinity;

            public void Initialize(NotificationCenterWindow window) => m_Window = window;

            public bool TryHandleWatchScroll(float rotarySteps)
            {
                if (m_Window == null || Mathf.Abs(rotarySteps) < 1e-4f)
                    return false;
                m_Accum += rotarySteps;
                if (Mathf.Abs(m_Accum) < 0.08f)
                    return true;
                var dir = m_Accum > 0f ? 1 : -1;
                m_Accum = 0f;
                if (Time.unscaledTime - m_LastTurnAt < 0.28f)
                    return true;
                m_LastTurnAt = Time.unscaledTime;
                m_Window.ChangePage(dir);
                return true;
            }
        }

        RowUi CreateRow(int slot, Vector2 pos)
        {
            const float rowW = 536f;
            const float rowH = 66f;

            var rowGo = new GameObject($"Notif Row {slot}", typeof(RectTransform));
            rowGo.transform.SetParent(m_Panel, false);
            var rect = (RectTransform)rowGo.transform;
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(rowW, rowH);

            var bg = M3.Surface("Background", rect, new Vector2(-24f, 0f), new Vector2(rowW - 52f, rowH),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, raycastTarget: true, style: VisionSurfaceStyle.Recessed);
            var stateImg = M3.Surface("State", rect, new Vector2(-24f, 0f), new Vector2(rowW - 52f, rowH),
                Color.clear, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var btn = rowGo.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = bg;
            var stateLayer = rowGo.AddComponent<M3StateLayer>();
            stateLayer.Initialize(stateImg, btn, M3Theme.OnSurface);

            var badge = M3.Surface("Badge", rect, new Vector2(-226f, 0f), new Vector2(44f, 44f),
                M3Theme.PrimaryContainer, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy);
            var initial = M3.Text("Initial", badge.rectTransform, "!", M3Theme.Type.TitleMedium,
                Vector2.zero, new Vector2(38f, 38f), Color.white, TextAlignmentOptions.Center);
            var icon = M3.RoundedScreen("Icon", rect, new Vector2(-226f, 0f), new Vector2(44f, 44f),
                M3Theme.k_ShapeFull, raycastTarget: false, rimStrength: 0.6f, sheenStrength: 0.3f);
            icon.gameObject.SetActive(false);

            var appTime = M3.Text("AppTime", rect, "", M3Theme.Type.LabelLarge,
                new Vector2(6f, 19f), new Vector2(374f, 18f), M3Theme.GlassAccent, TextAlignmentOptions.Left);
            appTime.textWrappingMode = TextWrappingModes.NoWrap;

            var rowTitle = M3.Text("Title", rect, "", M3Theme.Type.TitleMedium,
                new Vector2(6f, -1f), new Vector2(374f, 22f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            rowTitle.textWrappingMode = TextWrappingModes.NoWrap;

            var rowBody = M3.Text("Body", rect, "", M3Theme.Type.BodyMedium,
                new Vector2(6f, -21f), new Vector2(374f, 20f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            rowBody.textWrappingMode = TextWrappingModes.NoWrap;

            var captured = slot;
            btn.onClick.AddListener(() => OnRowClicked(captured));

            M3.IconButton("Dismiss", rect, "close", M3Button.Variant.Tonal,
                new Vector2(236f, 0f), () => OnRowDismissClicked(captured), 34f);

            return new RowUi
            {
                Root = rowGo,
                Icon = icon,
                Badge = badge,
                Initial = initial,
                AppTime = appTime,
                Title = rowTitle,
                Body = rowBody,
            };
        }

        void RefreshUi()
        {
            if (m_Panel == null)
                return;
            var bridge = SystemBridge.Instance;
            if (m_AccessBanner != null)
                m_AccessBanner.SetActive(bridge == null || !bridge.NotificationAccessEnabled);

            var list = bridge != null ? bridge.Notifications : null;
            var total = list != null ? list.Count : 0;
            var pages = Mathf.Max(1, (total + k_RowsPerPage - 1) / k_RowsPerPage);
            m_Page = Mathf.Clamp(m_Page, 0, pages - 1);

            if (m_PageText != null)
                m_PageText.text = $"Pagina {m_Page + 1} / {pages}";
            if (m_PrevBtn != null)
                m_PrevBtn.Interactable = m_Page > 0;
            if (m_NextBtn != null)
                m_NextBtn.Interactable = m_Page < pages - 1;
            if (m_EmptyText != null)
                m_EmptyText.gameObject.SetActive(total == 0);

            for (var i = 0; i < k_RowsPerPage; i++)
            {
                var idx = m_Page * k_RowsPerPage + i;
                var row = m_Rows[i];
                var valid = list != null && idx < total;
                row.Root.SetActive(valid);
                if (!valid)
                    continue;

                var item = list[idx];
                var appLabel = string.IsNullOrEmpty(item.AppName) ? item.PackageName : item.AppName;
                var timeStr = SystemBridge.FormatTimestamp(item.PostTimeMs);
                row.AppTime.text = string.IsNullOrEmpty(timeStr) ? appLabel : $"{appLabel} • {timeStr}";
                row.Title.text = string.IsNullOrEmpty(item.Title) ? appLabel : item.Title;
                row.Body.text = string.IsNullOrEmpty(item.Text) ? "Notifica" : item.Text;

                var iconTex = bridge.GetAppIcon(item.PackageName);
                if (iconTex != null)
                {
                    row.Icon.texture = iconTex;
                    row.Icon.gameObject.SetActive(true);
                    row.Badge.gameObject.SetActive(false);
                }
                else
                {
                    row.Icon.gameObject.SetActive(false);
                    row.Badge.gameObject.SetActive(true);
                    row.Initial.text = string.IsNullOrEmpty(appLabel) ? "!" : appLabel.Substring(0, 1).ToUpperInvariant();
                }
            }
        }

        void ChangePage(int delta)
        {
            m_Page += delta;
            RefreshUi();
        }

        void OnRowClicked(int slot)
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            var idx = m_Page * k_RowsPerPage + slot;
            if (idx < 0 || idx >= bridge.Notifications.Count)
                return;
            var item = bridge.Notifications[idx];
            var lbl = string.IsNullOrEmpty(item.AppName) ? item.PackageName : item.AppName;
            Hide();
            PhoneAppShortcutStore.LaunchPackage(m_Demos, item.PackageName, lbl);
        }

        void OnRowDismissClicked(int slot)
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            var idx = m_Page * k_RowsPerPage + slot;
            if (idx < 0 || idx >= bridge.Notifications.Count)
                return;
            bridge.DismissNotification(bridge.Notifications[idx].Key);
            RefreshUi();
        }

        void OnClearClicked()
        {
            SystemBridge.Instance?.ClearNotifications();
            RefreshUi();
        }

        void OnEnableAccessClicked()
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            if (bridge.IsShizukuReady && bridge.GrantNotificationAccessViaShizuku())
            {
                RefreshUi();
                return;
            }
            bridge.OpenNotificationAccessSettings();
        }

        void Update()
        {
            if (m_Shown && SettingsSummoner.SuppressForFullscreen)
                Hide();
        }
    }
}
