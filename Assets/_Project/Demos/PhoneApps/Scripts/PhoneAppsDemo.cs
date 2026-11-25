using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.PhoneApps
{
    // Opens up to 3 real installed Android phone apps simultaneously via Shizuku VirtualDisplays.
    // Apps launch in vertical portrait mode with a live Rotate button, 1:1 pinch touch/scroll,
    // real Android app icons in the launcher, and full concurrent coexistence with other apps.
    public class PhoneAppsDemo : Demo, IWatchRotaryHandler, IPinchHandler
    {
        const float k_LauncherDistance = 1.52f;
        const float k_LauncherPitch = -4f;
        const float k_StatusPollSeconds = 0.75f;
        const float k_AsyncDataPollSeconds = 0.25f;

        const int k_StatusNotInstalled = 0;
        const int k_StatusNotRunning = 1;
        const int k_StatusPermissionRequired = 2;
        const int k_StatusConnecting = 3;
        const int k_StatusReady = 4;

        struct AppCatalogItem
        {
            public string PackageName;
            public string ActivityName;
            public string Label;
            public Color Accent;
            public int ColorRgb;
            public int AndroidIndex;
        }

        sealed class LauncherTile
        {
            public GameObject Root;
            public Image OpenRing;
            public M3Button StarButton;
            public Image BadgeBg;
            public TMP_Text BadgeInitials;
            public Image IconBacking;
            public RawImage IconImage;
            public TMP_Text Label;
            public int SlotOffset;
        }

        static readonly Color[] s_FallbackColors =
        {
            new(0.15f, 0.39f, 0.92f),
            new(0.02f, 0.59f, 0.41f),
            new(0.86f, 0.15f, 0.47f),
            new(0.85f, 0.47f, 0.02f),
            new(0.49f, 0.23f, 0.93f),
            new(0.03f, 0.57f, 0.70f),
        };

        readonly List<PhoneAppWindow> m_Windows = new();
        readonly List<AppCatalogItem> m_Catalog = new();
        readonly List<LauncherTile> m_Tiles = new();
        readonly Dictionary<string, Texture2D> m_IconCache = new();
        readonly Dictionary<string, byte[]> m_IconPngCache = new();

        CoreServices m_Core;
        RectTransform m_Launcher;
        PhoneAppKeyboard m_Keyboard;
        PhoneAppWindow m_Active;
        PhoneAppWindow m_FullscreenWindow;
        bool m_FullscreenRestoresPortrait;
        RectTransform m_ExitFullscreenBar;
        bool m_ExitFullscreenShown;
        float m_ExitFullscreenYaw;
        IPinchHandler m_PreviousPinchHandler;

        TMP_Text m_CountBadge;
        TMP_Text m_StatusText;
        RectTransform m_StatusTextRect;
        M3Button m_StatusActionBtn;
        TMP_Text m_PageText;
        M3Button m_PrevButton;
        M3Button m_NextButton;

        struct PendingReplace
        {
            public bool Active;
            public string PackageName;
            public string ActivityName;
            public string Label;
            public Color Accent;
        }

        int m_Page;
        int m_LastStatus = -1;
        int m_KnownAppListVersion = -1;
        int m_KnownIconsVersion = -1;
        string m_StatusNote = "";
        StarredPhoneApp? m_PendingLaunch;
        float m_NextStatusPoll;
        float m_NextAsyncPoll;

        RectTransform m_LimitPopup;
        TMP_Text m_LimitSubtitle;
        readonly GameObject[] m_LimitSlotRows = new GameObject[PhoneAppsRules.MaxWindows];
        readonly RawImage[] m_LimitSlotIcons = new RawImage[PhoneAppsRules.MaxWindows];
        readonly Image[] m_LimitSlotBadges = new Image[PhoneAppsRules.MaxWindows];
        readonly TMP_Text[] m_LimitSlotBadgeInitials = new TMP_Text[PhoneAppsRules.MaxWindows];
        readonly TMP_Text[] m_LimitSlotLabels = new TMP_Text[PhoneAppsRules.MaxWindows];
        bool m_LimitPopupShown;
        float m_LimitPopupYaw;
        bool m_LimitPopupFollowing;
        float m_LimitPopupYawSpeed;
        Vector3 m_LimitPopupCenter;
        Vector3 m_LimitPopupCenterVel;
        PendingReplace m_PendingReplace;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Host;
#endif

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            Application.runInBackground = true;
            var silentLaunch = PhoneAppShortcutStore.PendingDirectLaunch.HasValue;
            BuildLauncher(startHidden: silentLaunch);
            PhoneAppShortcutStore.DirectLaunchHandler = LaunchOrFocusStarredApp;
            PhoneAppShortcutStore.IsPackageOpenHandler = pkg => FindOpenWindow(pkg) != null;
            if (silentLaunch)
            {
                m_PendingLaunch = PhoneAppShortcutStore.PendingDirectLaunch.Value;
                PhoneAppShortcutStore.PendingDirectLaunch = null;
            }
            m_Keyboard = PhoneAppKeyboard.Create(transform, () => m_Core != null ? m_Core.Head : null, GetActiveWindow);
            SettingsSummoner.SetLookDownExtra("keyboard", ToggleKeyboard);
            SettingsSummoner.RecenterAction = RecenterAll;

            InitHost();
            RefreshCatalogFromHost();
            RefreshStatusUi(force: true);
            RefreshPage();

            Debug.Log("[PhoneApps] Ready: Shizuku phone app launcher (portrait default, rotatable, up to 3 windows)");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (core != null)
                m_Core = core;
            if (m_FullscreenWindow != null)
                ExitFullscreen();
            if (PhoneAppShortcutStore.PendingDirectLaunch.HasValue)
            {
                var pending = PhoneAppShortcutStore.PendingDirectLaunch.Value;
                PhoneAppShortcutStore.PendingDirectLaunch = null;
                if (LaunchOrFocusStarredApp(pending))
                    return;
                m_PendingLaunch = pending;
                RefreshStatusFromHost(forceUi: true);
                return;
            }
            if (m_Launcher != null && core != null && core.Head != null)
                WorldUi.BringToFront(m_Launcher, core.Head, k_LauncherDistance, k_LauncherPitch);
            RefreshStatusFromHost(forceUi: true);
        }

        public override void OnDemoExit(CoreServices core)
        {
            HideLimitPopup(immediate: true);
            if (m_LimitPopup != null)
            {
                Destroy(m_LimitPopup.gameObject);
                m_LimitPopup = null;
            }
            if (m_FullscreenWindow != null)
                ExitFullscreen();
            if (m_ExitFullscreenBar != null)
            {
                Destroy(m_ExitFullscreenBar.gameObject);
                m_ExitFullscreenBar = null;
            }
            if (PhoneAppShortcutStore.DirectLaunchHandler != null)
                PhoneAppShortcutStore.DirectLaunchHandler = null;
            PhoneAppShortcutStore.IsPackageOpenHandler = null;
            m_PendingLaunch = null;
            SettingsSummoner.ClearLookDownExtra();
            if (SettingsSummoner.RecenterAction == RecenterAll)
                SettingsSummoner.RecenterAction = null;
            if (ReferenceEquals(PinchRouter.RotaryHandler, this))
                PinchRouter.RotaryHandler = null;

            if (m_Keyboard != null)
            {
                Destroy(m_Keyboard.gameObject);
                m_Keyboard = null;
            }
            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null)
                {
                    m_Windows[i].DisposeSession();
                    Destroy(m_Windows[i].gameObject);
                }
            }
            m_Windows.Clear();
            m_Active = null;

            foreach (var tex in m_IconCache.Values)
            {
                if (tex != null)
                    Destroy(tex);
            }
            m_IconCache.Clear();
            m_IconPngCache.Clear();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Host != null)
            {
                try
                {
                    m_Host.Call("dispose");
                    m_Host.Dispose();
                }
                catch (Exception) { }
                m_Host = null;
            }
#endif
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
                RefreshStatusFromHost(forceUi: true);
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused)
                RefreshStatusFromHost(forceUi: true);
        }

        void Update()
        {
            if (m_FullscreenWindow != null)
                UpdateFullscreenLookDown();
            var now = Time.unscaledTime;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Host != null && now >= m_NextAsyncPoll)
            {
                m_NextAsyncPoll = now + k_AsyncDataPollSeconds;
                try
                {
                    var appVer = m_Host.Call<int>("getAppListVersion");
                    var iconsVer = m_Host.Call<int>("getIconsVersion");
                    if (appVer != m_KnownAppListVersion)
                    {
                        m_KnownAppListVersion = appVer;
                        m_KnownIconsVersion = iconsVer;
                        RefreshCatalogFromHost();
                        RefreshPage();
                    }
                    else if (iconsVer != m_KnownIconsVersion)
                    {
                        m_KnownIconsVersion = iconsVer;
                        RefreshPage();
                    }
                }
                catch (Exception) { }
            }
#endif
            if (m_PendingLaunch.HasValue)
            {
                var pending = m_PendingLaunch.Value;
                if (GetHostStatusCode() == k_StatusReady)
                {
                    m_PendingLaunch = null;
                    LaunchOrFocusStarredApp(pending);
                }
                else if (GetHostStatusCode() is k_StatusNotInstalled or k_StatusNotRunning or k_StatusPermissionRequired)
                {
                    m_PendingLaunch = null;
                    SettingsPanelHub.OpenShizukuSetup();
                    return;
                }
                if (now >= m_NextStatusPoll)
                {
                    m_NextStatusPoll = now + 0.20f;
                    RefreshStatusFromHost(forceUi: false);
                }
                return;
            }
            if (now < m_NextStatusPoll)
                return;
            m_NextStatusPoll = now + k_StatusPollSeconds;
            RefreshStatusFromHost(forceUi: false);
        }

        void InitHost()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Host = new AndroidJavaObject("com.brenpoly.cardboardxr.phoneapps.PhoneAppHost", activity);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PhoneApps] Could not initialize PhoneAppHost: {e.Message}");
            }
#endif
        }

        void RefreshCatalogFromHost()
        {
            m_Catalog.Clear();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Host != null)
            {
                try
                {
                    var count = m_Host.Call<int>("getAppCount");
                    for (var i = 0; i < count; i++)
                    {
                        var pkg = m_Host.Call<string>("getAppPackage", i);
                        var act = m_Host.Call<string>("getAppActivity", i);
                        var label = m_Host.Call<string>("getAppLabel", i);
                        var rgb = m_Host.Call<int>("getAppColorRgb", i);
                        m_Catalog.Add(new AppCatalogItem
                        {
                            PackageName = pkg,
                            ActivityName = act,
                            Label = string.IsNullOrWhiteSpace(label) ? pkg : label,
                            Accent = RgbToColor(rgb),
                            ColorRgb = rgb,
                            AndroidIndex = i,
                        });
                    }
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PhoneApps] App query failed: {e.Message}");
                }
            }
#endif
            // Editor preview catalog so grid layout and portrait/landscape preview work in Editor tests.
            var preview = new (string pkg, string label)[]
            {
                ("com.android.chrome", "Chrome"),
                ("com.zhiliaoapp.musically", "TikTok"),
                ("com.whatsapp", "WhatsApp"),
                ("com.instagram.android", "Instagram"),
                ("com.google.android.youtube", "YouTube"),
                ("com.spotify.music", "Spotify"),
                ("org.telegram.messenger", "Telegram"),
                ("com.twitter.android", "X"),
                ("com.reddit.frontpage", "Reddit"),
                ("com.google.android.gm", "Gmail"),
                ("com.google.android.apps.maps", "Maps"),
                ("com.android.settings", "Impostazioni"),
            };
            for (var i = 0; i < preview.Length; i++)
            {
                m_Catalog.Add(new AppCatalogItem
                {
                    PackageName = preview[i].pkg,
                    ActivityName = "",
                    Label = preview[i].label,
                    Accent = s_FallbackColors[i % s_FallbackColors.Length],
                    ColorRgb = ColorToRgb(s_FallbackColors[i % s_FallbackColors.Length]),
                    AndroidIndex = i,
                });
            }
        }

        static int ColorToRgb(Color c) =>
            ((Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f) & 0xFF) << 16) |
            ((Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f) & 0xFF) << 8) |
            (Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f) & 0xFF);

        static Color RgbToColor(int rgb) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        void LateUpdate()
        {
            UpdateLimitPopupFollow();
        }

        void BuildLauncher(bool startHidden = false)
        {
            m_Launcher = WorldUi.Panel("PhoneApps Launcher", transform, new Vector2(840f, 560f), k_LauncherDistance,
                radius: 44f, scaleFactor: 0.68f);

            // Header row
            const float headerY = 232f;
            M3.Icon("Phone Icon", m_Launcher, "smartphone", 28f, new Vector2(-372f, headerY), M3Theme.GlassAccent);
            var title = M3.Text("Title", m_Launcher, "App Telefono", M3Theme.Type.TitleLarge,
                new Vector2(-170f, headerY), new Vector2(360f, 38f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            m_CountBadge = M3.Text("Open Count", m_Launcher, "0 / 3 aperte", M3Theme.Type.LabelLarge,
                new Vector2(158f, headerY), new Vector2(170f, 32f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Right);
            M3.IconButton("Reload", m_Launcher, "refresh", M3Button.Variant.Tonal,
                new Vector2(308f, headerY), OnReloadClicked, 44f);
            M3.IconButton("Keyboard", m_Launcher, "keyboard", M3Button.Variant.Tonal,
                new Vector2(364f, headerY), ToggleKeyboard, 44f);

            // Shizuku recessed glass status banner
            const float bannerY = 172f;
            var banner = M3.Surface("Status Banner", m_Launcher, new Vector2(0f, bannerY), new Vector2(780f, 54f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Recessed);
            m_StatusText = M3.Text("Status Text", banner.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(-96f, 0f), new Vector2(548f, 38f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            m_StatusText.textWrappingMode = TextWrappingModes.NoWrap;
            m_StatusTextRect = m_StatusText.rectTransform;

            m_StatusActionBtn = M3.Button("Status Action", banner.rectTransform, "Autorizza", M3Button.Variant.Filled,
                new Vector2(286f, 0f), OnStatusActionClicked, width: 176f);

            // 4 x 3 App Grid
            const int cols = 4;
            const float cellW = 186f;
            const float cellH = 98f;
            const float gapX = 12f;
            var totalW = cols * cellW + (cols - 1) * gapX;
            var startX = -totalW * 0.5f + cellW * 0.5f;
            var rowYPositions = new[] { 80f, -28f, -136f };

            for (var i = 0; i < PhoneAppsRules.AppsPerPage; i++)
            {
                var row = i / cols;
                var col = i % cols;
                var pos = new Vector2(startX + col * (cellW + gapX), rowYPositions[row]);
                m_Tiles.Add(CreateTile(i, pos, new Vector2(cellW, cellH)));
            }

            // Footer controls
            const float footerY = -234f;
            m_PrevButton = M3.IconButton("Prev Page", m_Launcher, "chevron_left", M3Button.Variant.Tonal,
                new Vector2(-290f, footerY), () => ChangePage(-1), 46f);
            m_PageText = M3.Text("Page Label", m_Launcher, "Pagina 1 / 1", M3Theme.Type.LabelLarge,
                new Vector2(-150f, footerY), new Vector2(180f, 34f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_NextButton = M3.IconButton("Next Page", m_Launcher, "chevron_right", M3Button.Variant.Tonal,
                new Vector2(-10f, footerY), () => ChangePage(1), 46f);
            M3.Button("Arrange", m_Launcher, "Disponi", M3Button.Variant.Tonal,
                new Vector2(260f, footerY), RecenterAll, "apps", width: 200f);

            WorldUi.PlaceInFront(m_Launcher, m_Core.Head, k_LauncherDistance, k_LauncherPitch);
            FloatingWindowHandle.Configure(m_Launcher, OnLauncherCloseClicked, restoreOnRecenter: false);
            m_Launcher.gameObject.AddComponent<PhoneAppsLauncherScrollTarget>().Initialize(this);
            if (startHidden && m_Launcher != null)
                m_Launcher.gameObject.SetActive(false);
        }

        sealed class PhoneAppsLauncherScrollTarget : MonoBehaviour, IWatchScrollable
        {
            PhoneAppsDemo m_Demo;
            float m_Accum;
            float m_LastTurnAt = float.NegativeInfinity;

            public void Initialize(PhoneAppsDemo demo) => m_Demo = demo;

            public bool TryHandleWatchScroll(float rotarySteps)
            {
                if (m_Demo == null || Mathf.Abs(rotarySteps) < 1e-4f)
                    return false;
                m_Accum += rotarySteps;
                if (Mathf.Abs(m_Accum) < 0.08f)
                    return true;
                var dir = m_Accum > 0f ? 1 : -1;
                m_Accum = 0f;
                if (Time.unscaledTime - m_LastTurnAt < 0.28f)
                    return true;
                m_LastTurnAt = Time.unscaledTime;
                m_Demo.ChangePage(dir);
                return true;
            }
        }

        LauncherTile CreateTile(int slotOffset, Vector2 pos, Vector2 size)
        {
            var rootGo = new GameObject($"App Slot {slotOffset}", typeof(RectTransform));
            rootGo.transform.SetParent(m_Launcher, false);
            var root = (RectTransform)rootGo.transform;
            root.anchoredPosition = pos;
            root.sizeDelta = size;

            var hitTarget = M3.Surface("Hit Target", root, Vector2.zero, size, Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: true, style: VisionSurfaceStyle.Pure);
            var openRing = M3.Surface("Open Ring", root, new Vector2(0f, 14f), new Vector2(72f, 72f),
                M3Theme.Primary, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.OutlineRing,
                rimStrength: 2.4f, sheenStrength: 0f);
            openRing.enabled = false;
            var stateImg = M3.Surface("State Layer", root, new Vector2(0f, 14f), new Vector2(76f, 76f), Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);

            var button = rootGo.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitTarget;

            var stateLayer = rootGo.AddComponent<M3StateLayer>();
            stateLayer.Initialize(stateImg, button, Color.white);

            var badgeBg = M3.Surface("Icon Badge", root, new Vector2(0f, 14f), new Vector2(62f, 62f),
                M3Theme.PrimaryContainer, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy);
            var badgeInit = M3.Text("Initials", badgeBg.rectTransform, "", M3Theme.Type.TitleMedium,
                Vector2.zero, new Vector2(54f, 54f), Color.white, TextAlignmentOptions.Center);

            var iconBacking = M3.Surface("Icon Backing", root, new Vector2(0f, 14f), new Vector2(62f, 62f),
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy,
                rimStrength: 0.72f, sheenStrength: 0.85f);
            iconBacking.gameObject.SetActive(false);

            var iconImg = M3.RoundedScreen("App Icon", root, new Vector2(0f, 14f), new Vector2(62f, 62f),
                M3Theme.k_ShapeFull, raycastTarget: false, rimStrength: 0.72f, sheenStrength: 0.38f);
            iconImg.gameObject.SetActive(false);

            var label = M3.Text("Label", root, "", M3Theme.Type.LabelLarge, new Vector2(0f, -32f),
                new Vector2(size.x - 16f, 24f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            label.textWrappingMode = TextWrappingModes.NoWrap;

            var capturedSlot = slotOffset;
            button.onClick.AddListener(() => OnTileClicked(capturedSlot));

            var starBtn = M3.IconButton("Star Toggle", root, "star_border", M3Button.Variant.Tonal,
                new Vector2(36f, 38f), () => OnStarClicked(capturedSlot), 30f);

            return new LauncherTile
            {
                Root = rootGo,
                OpenRing = openRing,
                StarButton = starBtn,
                BadgeBg = badgeBg,
                BadgeInitials = badgeInit,
                IconBacking = iconBacking,
                IconImage = iconImg,
                Label = label,
                SlotOffset = slotOffset,
            };
        }

        void RefreshStatusFromHost(bool forceUi)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Host?.Call("refreshStatus");
#endif
            RefreshStatusUi(forceUi);
        }

        void RefreshStatusUi(bool force)
        {
            var status = GetHostStatusCode();
            if (!force && status == m_LastStatus && string.IsNullOrEmpty(m_StatusNote))
                return;
            m_LastStatus = status;

            var showButton = status != k_StatusReady;
            if (m_StatusActionBtn != null)
                m_StatusActionBtn.gameObject.SetActive(showButton);

            if (m_StatusTextRect != null)
            {
                m_StatusTextRect.anchoredPosition = showButton ? new Vector2(-96f, 0f) : Vector2.zero;
                m_StatusTextRect.sizeDelta = showButton ? new Vector2(548f, 38f) : new Vector2(740f, 38f);
            }

            if (!string.IsNullOrEmpty(m_StatusNote) && status == k_StatusReady)
            {
                m_StatusText.text = m_StatusNote;
                m_StatusText.color = M3Theme.Primary;
                return;
            }

            switch (status)
            {
                case k_StatusNotInstalled:
                    m_StatusText.text = "Shizuku non trovato • Installa Shizuku sul telefono";
                    m_StatusText.color = M3Theme.Error;
                    m_StatusActionBtn.Text = "Riprova";
                    break;
                case k_StatusNotRunning:
                    m_StatusText.text = "Shizuku non attivo • Avvia il servizio in Shizuku";
                    m_StatusText.color = M3Theme.Error;
                    m_StatusActionBtn.Text = "Apri Shizuku";
                    break;
                case k_StatusPermissionRequired:
                    m_StatusText.text = "Permesso Shizuku richiesto per aprire le app";
                    m_StatusText.color = M3Theme.Primary;
                    m_StatusActionBtn.Text = "Autorizza";
                    break;
                case k_StatusConnecting:
                    m_StatusText.text = "Connessione al servizio Shizuku in corso...";
                    m_StatusText.color = M3Theme.OnSurfaceVariant;
                    m_StatusActionBtn.Text = "Aggiorna";
                    break;
                case k_StatusReady:
                    m_StatusText.text = "Shizuku pronto • Scegli un'app (apre in verticale, max 3)";
                    m_StatusText.color = M3Theme.OnSurfaceVariant;
                    break;
                default:
                    m_StatusText.text = GetHostStatusMessage();
                    m_StatusText.color = M3Theme.Error;
                    m_StatusActionBtn.Text = "Riprova";
                    break;
            }
        }

        int GetHostStatusCode()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return m_Host != null ? m_Host.Call<int>("getStatusCode") : k_StatusNotRunning;
#else
            return k_StatusReady;
#endif
        }

        string GetHostStatusMessage()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return m_Host != null ? (m_Host.Call<string>("getStatusMessage") ?? "Errore Shizuku") : "Errore host";
#else
            return "Anteprima Editor";
#endif
        }

        void OnStatusActionClicked()
        {
            m_StatusNote = "";
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Host != null)
            {
                m_Host.Call("requestPermission");
                RefreshStatusFromHost(forceUi: true);
            }
#else
            RefreshStatusUi(force: true);
#endif
        }

        void OnReloadClicked()
        {
            m_StatusNote = "";
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Host?.Call("reloadAppList");
            m_Host?.Call("refreshStatus");
#endif
            RefreshCatalogFromHost();
            m_Page = PhoneAppsRules.ClampPage(m_Page, m_Catalog.Count);
            RefreshStatusUi(force: true);
            RefreshPage();
        }

        void ChangePage(int delta)
        {
            var pages = PhoneAppsRules.PageCount(m_Catalog.Count);
            m_Page = Mathf.Clamp(m_Page + delta, 0, pages - 1);
            RefreshPage();
        }

        void RefreshPage()
        {
            var pageCount = PhoneAppsRules.PageCount(m_Catalog.Count);
            m_Page = PhoneAppsRules.ClampPage(m_Page, m_Catalog.Count);
            if (m_PageText != null)
                m_PageText.text = $"Pagina {m_Page + 1} / {pageCount}";
            if (m_PrevButton != null)
                m_PrevButton.Interactable = m_Page > 0;
            if (m_NextButton != null)
                m_NextButton.Interactable = m_Page < pageCount - 1;

            var start = m_Page * PhoneAppsRules.AppsPerPage;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Host?.Call("prepareIconsAsync", start, PhoneAppsRules.AppsPerPage);
#endif
            for (var i = 0; i < m_Tiles.Count; i++)
            {
                var tile = m_Tiles[i];
                var idx = start + i;
                var visible = idx < m_Catalog.Count;
                tile.Root.SetActive(visible);
                if (!visible)
                    continue;

                var item = m_Catalog[idx];
                tile.Label.text = item.Label;
                if (tile.OpenRing != null)
                    tile.OpenRing.enabled = FindOpenWindow(item.PackageName) != null;

                var iconTex = TryGetOrLoadAppIcon(item);
                if (tile.StarButton != null)
                {
                    var starred = PhoneAppShortcutStore.IsStarred(item.PackageName);
                    tile.StarButton.Selected = false;
                    tile.StarButton.Icon = starred ? "star" : "star_border";
                    if (tile.StarButton.IconGraphic != null)
                        tile.StarButton.IconGraphic.color = starred ? M3Theme.StarGold : M3Theme.OnSurfaceVariant;
                    if (tile.StarButton.Container != null)
                        tile.StarButton.Container.color = starred ? M3Theme.PrimaryContainer : M3Theme.SecondaryContainer;
                }
                if (iconTex != null)
                {
                    tile.IconImage.texture = iconTex;
                    tile.IconBacking.gameObject.SetActive(true);
                    tile.IconImage.gameObject.SetActive(true);
                    tile.BadgeBg.gameObject.SetActive(false);
                }
                else
                {
                    tile.IconBacking.gameObject.SetActive(false);
                    tile.IconImage.gameObject.SetActive(false);
                    tile.BadgeBg.gameObject.SetActive(true);
                    tile.BadgeBg.color = item.Accent;
                    tile.BadgeInitials.text = PhoneAppsRules.Initials(item.Label);
                }
            }

            if (m_CountBadge != null)
                m_CountBadge.text = $"{m_Windows.Count} / {PhoneAppsRules.MaxWindows} aperte";
        }

        Texture2D TryGetOrLoadAppIcon(AppCatalogItem item)
        {
            if (string.IsNullOrEmpty(item.PackageName))
                return null;
            if (m_IconCache.TryGetValue(item.PackageName, out var cached))
                return cached;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Host == null)
                return null;
            try
            {
                var bytes = m_Host.Call<byte[]>("getAppIconPngIfCached", item.AndroidIndex);
                if (bytes != null && bytes.Length > 0)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                    if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                    {
                        m_IconCache[item.PackageName] = tex;
                        m_IconPngCache[item.PackageName] = bytes;
                        return tex;
                    }
                    Destroy(tex);
                }
            }
            catch (Exception) { }
#endif
            return null;
        }

        void OnStarClicked(int slotOffset)
        {
            var idx = m_Page * PhoneAppsRules.AppsPerPage + slotOffset;
            if (idx < 0 || idx >= m_Catalog.Count)
                return;

            var item = m_Catalog[idx];
            var iconTex = TryGetOrLoadAppIcon(item);
            m_IconPngCache.TryGetValue(item.PackageName, out var pngBytes);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pngBytes == null && m_Host != null)
            {
                try { pngBytes = m_Host.Call<byte[]>("getAppIconPngIfCached", item.AndroidIndex); }
                catch (Exception) { }
            }
#endif
            var nowStarred = PhoneAppShortcutStore.ToggleStar(
                item.PackageName, item.ActivityName, item.Label, item.ColorRgb, iconTex, pngBytes);
            m_StatusNote = nowStarred
                ? $"{item.Label} aggiunta alla lista app generale"
                : $"{item.Label} rimossa dalla lista app generale";
            RefreshPage();
            RefreshStatusUi(force: true);
        }

        bool LaunchOrFocusStarredApp(StarredPhoneApp app)
        {
            if (GetHostStatusCode() != k_StatusReady)
                return false;

            var pkg = app.PackageName;
            var act = app.ActivityName ?? "";
            var lbl = string.IsNullOrWhiteSpace(app.Label) ? pkg : app.Label;
            var accent = app.Tint;
            for (var i = 0; i < m_Catalog.Count; i++)
            {
                if (m_Catalog[i].PackageName == pkg)
                {
                    if (!string.IsNullOrEmpty(m_Catalog[i].ActivityName))
                        act = m_Catalog[i].ActivityName;
                    lbl = m_Catalog[i].Label;
                    accent = m_Catalog[i].Accent;
                    break;
                }
            }

            var existing = FindOpenWindow(pkg);
            if (existing != null)
            {
                BringWindowToFront(existing);
                return true;
            }

            if (PhoneAppsRules.ShouldPromptReplaceWindow(m_Windows.Count, false))
            {
                ShowLimitPopup(pkg, act, lbl, accent);
                return true;
            }

            return OpenAppWindow(pkg, act, lbl, accent);
        }

        void OnTileClicked(int slotOffset)
        {
            var idx = m_Page * PhoneAppsRules.AppsPerPage + slotOffset;
            if (idx < 0 || idx >= m_Catalog.Count)
                return;

            var status = GetHostStatusCode();
            if (status != k_StatusReady)
            {
                OnStatusActionClicked();
                return;
            }

            var item = m_Catalog[idx];
            var existing = FindOpenWindow(item.PackageName);
            if (existing != null)
            {
                BringWindowToFront(existing);
                m_StatusNote = "";
                RefreshStatusUi(force: true);
                return;
            }

            if (PhoneAppsRules.ShouldPromptReplaceWindow(m_Windows.Count, false))
            {
                ShowLimitPopup(item.PackageName, item.ActivityName, item.Label, item.Accent);
                return;
            }

            m_StatusNote = "";
            OpenAppWindow(item.PackageName, item.ActivityName, item.Label, item.Accent);
        }

        bool OpenAppWindow(string packageName, string activityName, string label, Color accent, float? yawOverride = null)
        {
            object hostObj = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            hostObj = m_Host;
#endif
            var win = PhoneAppWindow.Create(
                transform,
                () => m_Core != null ? m_Core.Head : null,
                () => m_Windows.Count,
                hostObj,
                packageName,
                activityName,
                label,
                accent,
                OnWindowFocused,
                OnWindowClosed,
                ToggleKeyboard,
                ToggleFullscreen);

            var slotIndex = m_Windows.Count;
            m_Windows.Add(win);
            m_Active = win;

            if (m_Core != null && m_Core.Head != null)
                win.PlaceAtYaw(m_Core.Head, yawOverride ?? 0f);

            HideLauncherIfShown();
            RefreshPage();
            RefreshStatusUi(force: true);
            return true;
        }

        void HideLauncherIfShown()
        {
            if (m_Launcher != null && M3Fade.IsShown(m_Launcher.gameObject))
                M3Fade.Hide(m_Launcher.gameObject);
        }

        void BringWindowToFront(PhoneAppWindow win)
        {
            if (win == null)
                return;
            m_Active = win;
            win.Relaunch();
            HideLauncherIfShown();
            if (m_Core != null && m_Core.Head != null)
                WorldUi.BringToFront(win.Panel, m_Core.Head, 1.65f, 0f, 0f);
        }

        void ShowLimitPopup(string pkg, string activity, string label, Color accent)
        {
            EnsureLimitPopup();
            m_PendingReplace = new PendingReplace
            {
                Active = true,
                PackageName = pkg,
                ActivityName = activity,
                Label = string.IsNullOrEmpty(label) ? pkg : label,
                Accent = accent,
            };
            if (m_LimitSubtitle != null)
                m_LimitSubtitle.text = $"Per aprire {m_PendingReplace.Label}, chiudine una tra le app aperte:";

            for (var i = 0; i < PhoneAppsRules.MaxWindows; i++)
            {
                var hasWin = i < m_Windows.Count && m_Windows[i] != null;
                if (m_LimitSlotRows[i] != null)
                    m_LimitSlotRows[i].SetActive(hasWin);
                if (!hasWin)
                    continue;

                var win = m_Windows[i];
                var winPkg = win.PackageName;
                if (m_LimitSlotLabels[i] != null)
                    m_LimitSlotLabels[i].text = $"Chiudi {win.AppLabel}";

                Texture2D iconTex = null;
                if (!string.IsNullOrEmpty(winPkg) && !m_IconCache.TryGetValue(winPkg, out iconTex))
                    iconTex = SystemBridge.Instance != null ? SystemBridge.Instance.GetAppIcon(winPkg) : null;
                if (iconTex == null && !string.IsNullOrEmpty(winPkg))
                    iconTex = PhoneAppShortcutStore.GetIcon(winPkg);

                if (iconTex != null)
                {
                    m_LimitSlotIcons[i].texture = iconTex;
                    m_LimitSlotIcons[i].gameObject.SetActive(true);
                    m_LimitSlotBadges[i].gameObject.SetActive(false);
                }
                else
                {
                    m_LimitSlotIcons[i].gameObject.SetActive(false);
                    m_LimitSlotBadges[i].gameObject.SetActive(true);
                    m_LimitSlotBadgeInitials[i].text = string.IsNullOrEmpty(win.AppLabel)
                        ? "?"
                        : win.AppLabel.Substring(0, 1).ToUpperInvariant();
                }
            }

            if (m_Core != null && m_Core.Head != null)
            {
                m_LimitPopupCenter = m_Core.Head.position;
                m_LimitPopupCenterVel = Vector3.zero;
                m_LimitPopupYaw = HeadYaw(m_Core.Head);
                m_LimitPopupFollowing = false;
                m_LimitPopupYawSpeed = 0f;
                PlaceLimitPopup();
            }
            m_LimitPopupShown = true;
            M3Fade.Show(m_LimitPopup.gameObject);
        }

        void EnsureLimitPopup()
        {
            if (m_LimitPopup != null)
                return;
            var size = new Vector2(520f, 396f);
            m_LimitPopup = M3.Canvas("Limit Popup", transform, size, M3Theme.k_OverlayDistance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapePanel);
            m_LimitPopup.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_LimitPopup);

            var title = M3.Text("Title", m_LimitPopup, "Troppe app aperte (3 / 3)", M3Theme.Type.TitleLarge,
                new Vector2(0f, 152f), new Vector2(460f, 34f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            m_LimitSubtitle = M3.Text("Subtitle", m_LimitPopup, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, 114f), new Vector2(460f, 36f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);

            var rowY = new[] { 50f, -18f, -86f };
            for (var i = 0; i < PhoneAppsRules.MaxWindows; i++)
            {
                var rowGo = new GameObject($"Close Slot {i}", typeof(RectTransform));
                rowGo.transform.SetParent(m_LimitPopup, false);
                var rowRect = (RectTransform)rowGo.transform;
                rowRect.anchoredPosition = new Vector2(0f, rowY[i]);
                rowRect.sizeDelta = new Vector2(452f, 56f);

                var bg = M3.Surface("Background", rowRect, Vector2.zero, new Vector2(452f, 56f),
                    M3Theme.GlassRecessed, M3Theme.k_ShapeFull, raycastTarget: true, style: VisionSurfaceStyle.Recessed);
                var stateImg = M3.Surface("State", rowRect, Vector2.zero, new Vector2(452f, 56f),
                    Color.clear, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
                var btn = rowGo.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = bg;
                var stateLayer = rowGo.AddComponent<M3StateLayer>();
                stateLayer.Initialize(stateImg, btn, M3Theme.OnSurface);

                var badgeBg = M3.Surface("Badge", rowRect, new Vector2(-190f, 0f), new Vector2(40f, 40f),
                    M3Theme.PrimaryContainer, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy);
                var initTxt = M3.Text("Initial", badgeBg.rectTransform, "?", M3Theme.Type.TitleMedium,
                    Vector2.zero, new Vector2(36f, 36f), Color.white, TextAlignmentOptions.Center);
                var iconRaw = M3.RoundedScreen("Icon", rowRect, new Vector2(-190f, 0f), new Vector2(40f, 40f),
                    M3Theme.k_ShapeFull, raycastTarget: false, rimStrength: 0.6f, sheenStrength: 0.3f);
                iconRaw.gameObject.SetActive(false);

                var labelTxt = M3.Text("Label", rowRect, "", M3Theme.Type.TitleMedium,
                    new Vector2(22f, 0f), new Vector2(356f, 36f), M3Theme.OnSurface, TextAlignmentOptions.Left);
                labelTxt.textWrappingMode = TextWrappingModes.NoWrap;

                var slot = i;
                btn.onClick.AddListener(() => OnLimitSlotClicked(slot));

                m_LimitSlotRows[i] = rowGo;
                m_LimitSlotBadges[i] = badgeBg;
                m_LimitSlotBadgeInitials[i] = initTxt;
                m_LimitSlotIcons[i] = iconRaw;
                m_LimitSlotLabels[i] = labelTxt;
            }

            M3.Button("Cancel", m_LimitPopup, "Annulla", M3Button.Variant.Tonal,
                new Vector2(0f, -152f), OnLimitCancelClicked, "close", width: 180f);

            UiRenderQueue.ApplyRecursive(m_LimitPopup, UiRenderQueue.k_PalmOverlayQueue);
            m_LimitPopup.gameObject.SetActive(false);
        }

        void OnLimitSlotClicked(int slot)
        {
            var req = m_PendingReplace;
            HideLimitPopup(immediate: false);
            if (!req.Active)
                return;
            if (slot >= 0 && slot < m_Windows.Count && m_Windows[slot] != null)
            {
                var victim = m_Windows[slot];
                if (m_FullscreenWindow == victim)
                    ExitFullscreen();
                m_Windows.Remove(victim);
                if (m_Active == victim)
                    m_Active = m_Windows.Count > 0 ? m_Windows[m_Windows.Count - 1] : null;
                victim.DisposeSession();
                Destroy(victim.gameObject);
            }
            if (m_Windows.Count < PhoneAppsRules.MaxWindows)
                OpenAppWindow(req.PackageName, req.ActivityName, req.Label, req.Accent, yawOverride: 0f);
        }

        void OnLimitCancelClicked() => HideLimitPopup(immediate: false);

        void HideLimitPopup(bool immediate)
        {
            m_PendingReplace = default;
            m_LimitPopupShown = false;
            if (m_LimitPopup == null)
                return;
            if (immediate)
                m_LimitPopup.gameObject.SetActive(false);
            else
                M3Fade.Hide(m_LimitPopup.gameObject);
        }

        void UpdateLimitPopupFollow()
        {
            if (!m_LimitPopupShown || m_LimitPopup == null || m_Core == null || m_Core.Head == null)
                return;
            var dt = Time.unscaledDeltaTime;
            m_LimitPopupCenter = Vector3.SmoothDamp(m_LimitPopupCenter, m_Core.Head.position, ref m_LimitPopupCenterVel,
                0.25f, Mathf.Infinity, dt);
            var halfWidthMeters = 520f * 0.5f * M3Theme.DpToMeters(1f, M3Theme.k_OverlayDistance);
            var deadZone = Mathf.Atan2(halfWidthMeters, M3Theme.k_OverlayDistance) * Mathf.Rad2Deg + SettingsSummoner.k_FollowMargin;
            m_LimitPopupYaw = SettingsSummoner.FollowYaw(m_LimitPopupYaw, HeadYaw(m_Core.Head), deadZone,
                ref m_LimitPopupFollowing, ref m_LimitPopupYawSpeed, dt);
            PlaceLimitPopup();
        }

        void PlaceLimitPopup()
        {
            if (m_LimitPopup == null)
                return;
            var dir = Quaternion.Euler(0f, m_LimitPopupYaw, 0f) * Vector3.forward;
            var pos = m_LimitPopupCenter + dir * M3Theme.k_OverlayDistance;
            m_LimitPopup.SetPositionAndRotation(pos, M3Theme.FacingRotation(pos, m_LimitPopupCenter));
        }

        PhoneAppWindow FindOpenWindow(string packageName)
        {
            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null && m_Windows[i].PackageName == packageName)
                    return m_Windows[i];
            }
            return null;
        }

        PhoneAppWindow GetActiveWindow()
        {
            if (m_Active == null && m_Windows.Count > 0)
                m_Active = m_Windows[m_Windows.Count - 1];
            return m_Active;
        }

        void OnWindowFocused(PhoneAppWindow window) => m_Active = window;

        void OnWindowClosed(PhoneAppWindow window)
        {
            if (window == null)
                return;
            if (m_FullscreenWindow == window)
                ExitFullscreen();
            m_Windows.Remove(window);
            if (m_Active == window)
                m_Active = m_Windows.Count > 0 ? m_Windows[m_Windows.Count - 1] : null;
            window.DisposeSession();
            Destroy(window.gameObject);
            if (m_Windows.Count == 0 && (m_Launcher == null || !M3Fade.IsShown(m_Launcher.gameObject)))
            {
                m_Core?.Demos?.CloseApp("phoneapps");
                return;
            }
            m_StatusNote = "";
            RefreshPage();
            RefreshStatusUi(force: true);
        }

        void OnLauncherCloseClicked()
        {
            if (m_Windows.Count > 0)
            {
                if (m_Launcher != null)
                    M3Fade.Hide(m_Launcher.gameObject);
                return;
            }
            m_Core?.Demos?.CloseApp("phoneapps");
        }

        void ToggleKeyboard() => m_Keyboard?.Toggle();

        void RecenterAll()
        {
            if (m_Launcher != null && !M3Fade.IsShown(m_Launcher.gameObject))
                M3Fade.Show(m_Launcher.gameObject);
            if (m_Core != null && m_Core.Head != null)
            {
                WorldUi.PlaceInFront(m_Launcher, m_Core.Head, k_LauncherDistance, k_LauncherPitch);
                FloatingWindowHandle.Configure(m_Launcher, OnLauncherCloseClicked, restoreOnRecenter: false);
                for (var i = 0; i < m_Windows.Count; i++)
                {
                    if (m_Windows[i] != null)
                        m_Windows[i].PlaceAtYaw(m_Core.Head, PhoneAppsRules.WindowSlotYaw(i));
                }
            }
            if (m_Keyboard != null && m_Keyboard.IsOpen)
                m_Keyboard.Show();
        }

        public bool TryHandleRotary(GameObject gazeTarget, float rotarySteps) => false;

        void ToggleFullscreen(PhoneAppWindow window)
        {
            if (m_FullscreenWindow == window)
                ExitFullscreen();
            else
                EnterFullscreen(window);
        }

        void EnterFullscreen(PhoneAppWindow window)
        {
            if (window == null || m_Core == null || m_Core.Rig == null)
                return;
            if (m_FullscreenWindow != null)
                ExitFullscreen();

            WatchGrabController.Release();
            HideLimitPopup(immediate: true);
            if (m_Keyboard != null && m_Keyboard.IsOpen)
                m_Keyboard.Hide();

            m_FullscreenWindow = window;
            m_FullscreenRestoresPortrait = !window.IsLandscape;
            if (!window.IsLandscape)
                window.SetOrientation(true);
            window.IsFullscreenVr = true;
            window.TextureChanged += OnFullscreenTextureChanged;

            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null)
                    m_Windows[i].PauseCapture = m_Windows[i] != window;
            }

            m_Core.Rig.SetFullscreenBackdrop(window.ScreenTexture, flipY: true);

            GazePointer.LockCursorToCenter = true;
            if (m_Core.Gaze != null)
                m_Core.Gaze.CursorAnglesDeg = Vector2.zero;
            GazePointer.OverlayCanvasesOnly = true;
            GazeCanvas.AllowedLayerMask = 1 << StereoRig.FullscreenOverlayLayer;
            GazePointer.OnEmptyTriggerClick = OnFullscreenEmptyTap;

            SettingsSummoner.SuppressForFullscreen = true;
            SettingsSummoner.FullscreenExitAction = ExitFullscreen;
            m_PreviousPinchHandler = PinchRouter.Handler;
            PinchRouter.Handler = this;

            EnsureExitFullscreenBar();
            ShowExitFullscreenBar(false, immediate: true);
            Debug.Log($"[PhoneApps] Entered VR fullscreen mode for {window.AppLabel}");
        }

        void ExitFullscreen()
        {
            var win = m_FullscreenWindow;
            m_FullscreenWindow = null;
            var restorePortrait = m_FullscreenRestoresPortrait;
            m_FullscreenRestoresPortrait = false;

            if (win != null)
            {
                win.TextureChanged -= OnFullscreenTextureChanged;
                win.IsFullscreenVr = false;
                if (restorePortrait)
                    win.SetOrientation(false);
            }

            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null)
                    m_Windows[i].PauseCapture = false;
            }

            if (m_Core != null && m_Core.Rig != null)
                m_Core.Rig.ClearFullscreenBackdrop();

            GazePointer.LockCursorToCenter = false;
            GazePointer.OverlayCanvasesOnly = false;
            GazeCanvas.AllowedLayerMask = ~0;
            if (GazePointer.OnEmptyTriggerClick == OnFullscreenEmptyTap)
                GazePointer.OnEmptyTriggerClick = null;

            SettingsSummoner.SuppressForFullscreen = false;
            if (SettingsSummoner.FullscreenExitAction == ExitFullscreen)
                SettingsSummoner.FullscreenExitAction = null;

            if (ReferenceEquals(PinchRouter.Handler, this))
                PinchRouter.Handler = m_PreviousPinchHandler;
            m_PreviousPinchHandler = null;

            ShowExitFullscreenBar(false, immediate: true);
            if (win != null && win.Panel != null && m_Core != null && m_Core.Head != null)
                WorldUi.PlaceInFront(win.Panel, m_Core.Head, 1.65f, 0f);
            Debug.Log("[PhoneApps] Exited VR fullscreen mode");
        }

        void EnsureExitFullscreenBar()
        {
            if (m_ExitFullscreenBar != null)
                return;
            m_ExitFullscreenBar = M3.Canvas("Exit Fullscreen Bar", transform, new Vector2(316f, 68f),
                PhoneAppsRules.FullscreenExitButtonDistance, M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            m_ExitFullscreenBar.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_ExitFullscreenBar);
            M3.Button("Exit Fullscreen", m_ExitFullscreenBar, "Esci da Schermo Intero",
                M3Button.Variant.Filled, Vector2.zero, ExitFullscreen, "close", 292f);
            SetLayerRecursive(m_ExitFullscreenBar, StereoRig.FullscreenOverlayLayer);
            UiRenderQueue.ApplyRecursive(m_ExitFullscreenBar, UiRenderQueue.k_PalmOverlayQueue);
            m_ExitFullscreenBar.gameObject.SetActive(false);
        }

        void UpdateFullscreenLookDown()
        {
            if (m_Core == null || m_Core.Head == null)
                return;
            var head = m_Core.Head;
            var pitchDown = Mathf.Asin(Mathf.Clamp(-head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            var wantShow = PhoneAppsRules.ShouldShowFullscreenExit(m_ExitFullscreenShown, pitchDown);
            if (wantShow && !m_ExitFullscreenShown)
            {
                m_ExitFullscreenYaw = HeadYaw(head);
                PlaceExitFullscreenBar(head.position);
                ShowExitFullscreenBar(true);
            }
            else if (!wantShow && m_ExitFullscreenShown)
            {
                ShowExitFullscreenBar(false);
            }
            else if (m_ExitFullscreenShown)
            {
                PlaceExitFullscreenBar(head.position);
            }
        }

        void ShowExitFullscreenBar(bool show, bool immediate = false)
        {
            m_ExitFullscreenShown = show;
            if (m_ExitFullscreenBar == null)
                return;
            if (immediate && !show)
                m_ExitFullscreenBar.gameObject.SetActive(false);
            else
                M3Fade.SetShown(m_ExitFullscreenBar.gameObject, show);
        }

        void PlaceExitFullscreenBar(Vector3 eyes)
        {
            if (m_ExitFullscreenBar == null)
                return;
            var dir = Quaternion.Euler(PhoneAppsRules.FullscreenExitButtonPitchDeg, m_ExitFullscreenYaw, 0f) * Vector3.forward;
            var pos = eyes + dir * PhoneAppsRules.FullscreenExitButtonDistance;
            m_ExitFullscreenBar.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - eyes, Vector3.up));
        }

        static float HeadYaw(Transform head)
        {
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            return flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized).eulerAngles.y : 0f;
        }

        static void SetLayerRecursive(Transform root, int layer)
        {
            if (root == null)
                return;
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        void OnFullscreenTextureChanged(Texture2D texture)
        {
            if (m_FullscreenWindow != null && m_Core != null && m_Core.Rig != null && texture != null)
                m_Core.Rig.SetFullscreenBackdrop(texture, flipY: true);
        }

        void OnFullscreenEmptyTap()
        {
            if (m_FullscreenWindow != null && PinchRouter.TryConsumeClickLockout())
                m_FullscreenWindow.SendVrCardboardTriggerTap();
        }

        public bool TryBeginPinch(GameObject gazeTarget, PinchSource source)
        {
            if (m_FullscreenWindow == null)
                return false;
            if (PinchRouter.TryConsumeClickLockout())
                m_FullscreenWindow.SendVrCardboardTriggerTap();
            return true;
        }

        public void EndPinch() { }
    }
}
