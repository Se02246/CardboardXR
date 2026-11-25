using System.Collections.Generic;
using CardboardXR.Demos.Video;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos.Browser
{
    // Web Cinema: opens up to 3 floating desktop browser windows in your room (or in a dark VR theater),
    // with one-click shortcuts for YouTube, Netflix, Prime Video, Disney+, Twitch, Plex and Google,
    // and automatic / manual 360/VR180 spherical playback for immersive web videos. Logs [Browser].
    public class BrowserDemo : Demo, IWatchRotaryHandler
    {
        const int k_MaxWindows = 3;
        const float k_DockDistance = 1.45f;
        const float k_DockPitch = -30f;

        readonly List<BrowserWindow> m_Windows = new();
        CoreServices m_Core;
        RectTransform m_Dock;
        BrowserKeyboard m_Keyboard;
        BrowserWindow m_Active;
        M3Button m_RoomButton;

        VideoSphere m_Sphere;
        Material m_SphereMat;
        BrowserWindow m_VrWindow;
        VideoFormat m_VrFormat = VideoFormat.Mono360;
        RectTransform m_VrControls;
        M3Button m_VrFormatButton;
        bool m_VrControlsShown;
        bool m_WasKeyboardOpenBeforeVr;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            BuildDock();
            BuildVr360Sphere();
            BuildVr360Controls();
            m_Keyboard = BrowserKeyboard.Create(transform, () => m_Core != null ? m_Core.Head : null, GetActiveWindow);
            SettingsSummoner.SetLookDownExtra("keyboard", ToggleKeyboard);
            SettingsSummoner.RecenterAction = RecenterAll;
            EnvironmentManager.IsolationChanged += OnIsolationChanged;
            PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler = OpenExternalUrl;
            var initialUrl = PhoneAppShortcutStore.VrBrowserRouter.PendingUrl;
            PhoneAppShortcutStore.VrBrowserRouter.PendingUrl = null;
            OpenWindow(string.IsNullOrEmpty(initialUrl) ? BrowserRules.Shortcuts[0].Url : initialUrl);
            Debug.Log("[Browser] Ready: floating desktop browser with shortcuts, 360 VR player and floating keyboard");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (core != null)
                m_Core = core;
            if (!string.IsNullOrEmpty(PhoneAppShortcutStore.VrBrowserRouter.PendingUrl))
            {
                var pendingUrl = PhoneAppShortcutStore.VrBrowserRouter.PendingUrl;
                PhoneAppShortcutStore.VrBrowserRouter.PendingUrl = null;
                OpenExternalUrl(pendingUrl);
                return;
            }
            if (m_VrWindow != null)
            {
                ShowVrControls(true);
                return;
            }
            if (m_Dock != null && core != null && core.Head != null)
                WorldUi.BringToFront(m_Dock, core.Head, k_DockDistance, k_DockPitch);
            if (m_Windows.Count == 0)
            {
                OpenWindow(BrowserRules.Shortcuts[0].Url);
                return;
            }
            if (core != null && core.Head != null)
            {
                for (var i = 0; i < m_Windows.Count; i++)
                    m_Windows[i].BringToFront(core.Head, BrowserRules.WindowSlotYaw(i, m_Windows.Count));
            }
        }

        public override void OnDemoExit(CoreServices core)
        {
            if (PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler == OpenExternalUrl)
                PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler = null;
            ExitVr360Mode(restoreFlatUi: false);
            EnvironmentManager.IsolationChanged -= OnIsolationChanged;
            SettingsSummoner.ClearLookDownExtra();
            if (SettingsSummoner.RecenterAction == RecenterAll)
                SettingsSummoner.RecenterAction = null;
            if (ReferenceEquals(PinchRouter.RotaryHandler, this))
                PinchRouter.RotaryHandler = null;
            if (m_Sphere != null)
            {
                m_Sphere.Clicked -= OnVrSphereClicked;
                Destroy(m_Sphere.gameObject);
                m_Sphere = null;
            }
            if (m_SphereMat != null)
            {
                Destroy(m_SphereMat);
                m_SphereMat = null;
            }
            if (m_VrControls != null)
            {
                Destroy(m_VrControls.gameObject);
                m_VrControls = null;
            }
            if (m_Keyboard != null)
            {
                Destroy(m_Keyboard.gameObject);
                m_Keyboard = null;
            }
            foreach (var window in m_Windows)
            {
                if (window != null)
                    Destroy(window.gameObject);
            }
            m_Windows.Clear();
            m_Active = null;
        }

        void BuildVr360Sphere()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
            if (shader == null)
                return;
            m_SphereMat = new Material(shader) { name = "Web Cinema 360 Material", color = Color.white };
            m_SphereMat.SetFloat("_Cull", 0f);

            var sphereGo = new GameObject("Web Cinema 360 Sphere");
            sphereGo.SetActive(false);
            sphereGo.transform.SetParent(transform, false);
            m_Sphere = sphereGo.AddComponent<VideoSphere>();
            var matField = typeof(VideoSphere).GetField("m_Material",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            matField?.SetValue(m_Sphere, m_SphereMat);
            sphereGo.SetActive(true);
            m_Sphere.Clicked += OnVrSphereClicked;
            m_Sphere.Show(null, default);
        }

        void BuildVr360Controls()
        {
            m_VrControls = WorldUi.Panel("Web 360 Controls", transform, new Vector2(464f, 76f), k_DockDistance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            FloatingWindowHandle.Exclude(m_VrControls);
            M3.Button("VrPlay", m_VrControls, "Play / Pausa", M3Button.Variant.Tonal, new Vector2(-148f, 0f),
                () => m_VrWindow?.TogglePlayPause(), "play_arrow", width: 140f);
            m_VrFormatButton = M3.Button("VrFormat", m_VrControls, "360° 2D", M3Button.Variant.Tonal,
                new Vector2(4f, 0f), CycleVrFormat, "aspect_ratio", width: 136f);
            M3.Button("VrExit", m_VrControls, "Esci 360°", M3Button.Variant.Filled, new Vector2(152f, 0f),
                () => ExitVr360Mode(restoreFlatUi: true), "close", width: 132f);
            m_VrControls.gameObject.SetActive(false);
        }

        void OnVrSphereClicked() => ShowVrControls(!m_VrControlsShown);

        void ShowVrControls(bool show)
        {
            m_VrControlsShown = show;
            if (m_VrControls == null)
                return;
            if (show && m_Core != null && m_Core.Head != null)
            {
                var headPitch = Mathf.Asin(Mathf.Clamp(m_Core.Head.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                WorldUi.PlaceInFront(m_VrControls, m_Core.Head, k_DockDistance, Mathf.Max(headPitch - 22f, -58f));
            }
            M3Fade.SetShown(m_VrControls.gameObject, show);
        }

        void CycleVrFormat()
        {
            if (m_VrWindow == null || m_Sphere == null)
                return;
            m_VrFormat = m_VrFormat.Next();
            RefreshVrSphere();
            if (m_VrFormatButton != null)
                m_VrFormatButton.Text = FormatLabel(m_VrFormat);
            Debug.Log($"[Browser] 360 VR format -> {m_VrFormat}");
        }

        static string FormatLabel(VideoFormat format) => format switch
        {
            { Projection: Projection.Full360, Layout: StereoLayout.Mono } => "360° 2D",
            { Projection: Projection.Full360, Layout: StereoLayout.TopBottom } => "360° TB",
            { Projection: Projection.Full360, Layout: StereoLayout.SideBySide } => "360° SBS",
            { Projection: Projection.Half180, Layout: StereoLayout.SideBySide } => "180° SBS",
            _ => "180° 2D",
        };

        void RefreshVrSphere()
        {
            if (m_Sphere == null || m_VrWindow == null)
                return;
            m_Sphere.Show(m_VrWindow.VideoTexture, m_VrFormat, flipVertical: true);
        }

        void OnWindowVr360Changed(BrowserWindow window, bool active)
        {
            if (active)
                EnterVr360Mode(window);
            else if (window == m_VrWindow)
                ExitVr360Mode(restoreFlatUi: true);
        }

        void EnterVr360Mode(BrowserWindow window)
        {
            if (window == null || m_Sphere == null)
                return;
            if (m_VrWindow != null && m_VrWindow != window)
                m_VrWindow.SetVr360Mode(false, userDismissed: false);
            m_VrWindow = window;
            m_VrFormat = window.InferredVrFormat;
            if (m_VrFormatButton != null)
                m_VrFormatButton.Text = FormatLabel(m_VrFormat);

            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null)
                    m_Windows[i].SetWindowVisualVisible(false);
            }
            if (m_Dock != null && m_Dock.gameObject.activeSelf)
                m_Dock.gameObject.SetActive(false);
            m_WasKeyboardOpenBeforeVr = m_Keyboard != null && m_Keyboard.IsOpen;
            if (m_WasKeyboardOpenBeforeVr)
                m_Keyboard.Hide();

            if (m_Core != null && m_Core.Passthrough != null)
                m_Core.Passthrough.SetIsolated(true);
            VirtualEnvironment.SetAllHidden(true);

            RefreshVrSphere();
            ShowVrControls(true);
            Debug.Log($"[Browser] Entered 360 VR video mode ({m_VrFormat})");
        }

        void ExitVr360Mode(bool restoreFlatUi)
        {
            var prev = m_VrWindow;
            m_VrWindow = null;
            if (prev != null && prev.IsVr360Active)
                prev.SetVr360Mode(false, userDismissed: true);
            if (m_Sphere != null)
                m_Sphere.Show(null, default);
            ShowVrControls(false);
            if (prev != null && m_Core != null && m_Core.Passthrough != null)
            {
                m_Core.Passthrough.SetIsolated(EnvironmentManager.IsIsolated);
                VirtualEnvironment.SetAllHidden(m_Core.Passthrough.IsOn || EnvironmentManager.IsIsolated);
            }

            if (!restoreFlatUi)
                return;
            for (var i = 0; i < m_Windows.Count; i++)
            {
                if (m_Windows[i] != null)
                    m_Windows[i].SetWindowVisualVisible(true);
            }
            if (m_Dock != null)
                M3Fade.Show(m_Dock.gameObject);
            if (m_WasKeyboardOpenBeforeVr && m_Keyboard != null)
                m_Keyboard.Show();
            m_WasKeyboardOpenBeforeVr = false;
            Debug.Log("[Browser] Exited 360 VR video mode");
        }

        BrowserWindow GetActiveWindow()
        {
            if (m_Active == null && m_Windows.Count > 0)
                m_Active = m_Windows[m_Windows.Count - 1];
            return m_Active;
        }

        void ToggleKeyboard() => m_Keyboard?.Toggle();

        void BuildDock()
        {
            m_Dock = WorldUi.Panel("Browser Dock", transform, new Vector2(880f, 136f), k_DockDistance, radius: 46f, scaleFactor: 0.72f);

            // Top row: sleek app pills & Cerca (Google) highlighted at the end
            var shortcuts = BrowserRules.Shortcuts;
            const float rowY = 30f;
            const float spacing = 120f;
            var startX = -(shortcuts.Length - 1) * spacing * 0.5f;
            for (var i = 0; i < shortcuts.Length; i++)
            {
                var shortcut = shortcuts[i];
                var isSearch = i == shortcuts.Length - 1;
                var x = startX + i * spacing;
                M3.Button(shortcut.Label, m_Dock, shortcut.Label,
                    isSearch ? M3Button.Variant.Filled : M3Button.Variant.Tonal,
                    new Vector2(x, rowY), () => OpenShortcut(shortcut.Url), width: 112f);
            }

            // Bottom row: + Finestra, Tastiera, Stanza AR / Sala Cinema
            const float bottomY = -30f;
            M3.Button("New Window", m_Dock, "+ Finestra", M3Button.Variant.Filled,
                new Vector2(-216f, bottomY), () => OpenWindow(BrowserRules.Shortcuts[0].Url), "add", 180f);
            M3.Button("Keyboard", m_Dock, "Tastiera", M3Button.Variant.Tonal,
                new Vector2(0f, bottomY), ToggleKeyboard, "keyboard", 180f);
            m_RoomButton = M3.Button("Room Toggle", m_Dock, EnvironmentManager.IsIsolated ? "Ambiente 360°" : "Stanza AR",
                M3Button.Variant.Tonal, new Vector2(216f, bottomY), TogglePassthrough, "landscape", 180f);
            m_RoomButton.Selected = EnvironmentManager.IsIsolated;

            WorldUi.PlaceInFront(m_Dock, m_Core.Head, k_DockDistance, k_DockPitch);
            FloatingWindowHandle.Configure(m_Dock, () => m_Core?.Demos?.CloseApp("browser"), restoreOnRecenter: false);
        }

        bool OpenExternalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (m_VrWindow != null)
                ExitVr360Mode(restoreFlatUi: true);
            if (m_Windows.Count < k_MaxWindows)
            {
                OpenWindow(url);
                return true;
            }
            var target = GetActiveWindow();
            if (target != null)
            {
                target.LoadUrl(url);
                if (m_Core != null && m_Core.Head != null)
                    target.BringToFront(m_Core.Head, 0f);
                return true;
            }
            return false;
        }

        void OpenShortcut(string url)
        {
            if (m_Active == null && m_Windows.Count > 0)
                m_Active = m_Windows[m_Windows.Count - 1];
            if (m_Active == null)
            {
                OpenWindow(url);
                return;
            }
            m_Active.LoadUrl(url);
        }

        void OpenWindow(string url)
        {
            if (m_Windows.Count >= k_MaxWindows)
            {
                OpenShortcut(url);
                return;
            }
            var window = BrowserWindow.Create(transform, () => m_Core != null ? m_Core.Head : null,
                url, OnWindowFocused, OnWindowClosed, OnWindowVr360Changed);
            var slotIndex = m_Windows.Count;
            m_Windows.Add(window);
            m_Active = window;
            // Place ONLY the new window so previously positioned windows stay locked in space!
            if (m_Core != null && m_Core.Head != null)
            {
                var yaw = slotIndex == 0 ? 0f : (slotIndex == 1 ? 42f : -42f);
                window.PlaceAtYaw(m_Core.Head, yaw);
            }
        }

        void OnWindowFocused(BrowserWindow window) => m_Active = window;

        void OnWindowClosed(BrowserWindow window)
        {
            if (window == null)
                return;
            if (window == m_VrWindow)
                ExitVr360Mode(restoreFlatUi: true);
            m_Windows.Remove(window);
            if (m_Active == window)
                m_Active = m_Windows.Count > 0 ? m_Windows[m_Windows.Count - 1] : null;
            Destroy(window.gameObject);
        }

        void RecenterAll()
        {
            if (m_VrWindow != null)
            {
                ShowVrControls(true);
                return;
            }
            if (m_Dock != null && !M3Fade.IsShown(m_Dock.gameObject))
                M3Fade.Show(m_Dock.gameObject);
            ArrangeWindows();
            if (m_Keyboard != null && m_Keyboard.IsOpen)
                m_Keyboard.Show();
        }

        void ArrangeWindows()
        {
            if (m_Core == null || m_Core.Head == null)
                return;
            for (var i = 0; i < m_Windows.Count; i++)
            {
                var yaw = BrowserRules.WindowSlotYaw(i, m_Windows.Count);
                m_Windows[i].PlaceAtYaw(m_Core.Head, yaw);
            }
            WorldUi.PlaceInFront(m_Dock, m_Core.Head, k_DockDistance, k_DockPitch);
            FloatingWindowHandle.Configure(m_Dock, () => m_Core?.Demos?.CloseApp("browser"), restoreOnRecenter: false);
        }

        public bool TryHandleRotary(GameObject gazeTarget, float rotarySteps)
        {
            // Crown always resizes the window under gaze in-place (handled by PinchRouter.ResizeFloatingWindow),
            // and switches to push/pull distance only while dragging via "Sposta" (WatchGrabController).
            return false;
        }

        void TogglePassthrough()
        {
            EnvironmentManager.ToggleGlobal();
            SyncRoomButton(EnvironmentManager.IsIsolated);
        }

        void OnIsolationChanged(bool isolated) => SyncRoomButton(isolated);

        void SyncRoomButton(bool isolated)
        {
            if (m_RoomButton != null)
            {
                m_RoomButton.Selected = isolated;
                m_RoomButton.Text = isolated ? "Ambiente 360°" : "Stanza AR";
            }
        }
    }
}
