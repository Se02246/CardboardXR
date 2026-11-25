using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.StreamCast
{
    // Schermi Live: multi-instance local stream receiver supporting up to 4 simultaneous floating
    // 3D VR screens from Windows, macOS, Linux, iPadOS, iOS, Android, and Apple AirPlay video.
    // Logs [StreamCast].
    public class StreamCastDemo : Demo, IWatchRotaryHandler
    {
        const int k_MaxScreens = 4;
        const float k_DockDistance = 1.45f;
        const float k_DockPitch = -30f;

        readonly List<StreamCastWindow> m_Windows = new();
        CoreServices m_Core;
        RectTransform m_Dock;
        TMP_Text m_AddressBanner;
        M3Button m_AddButton;
        M3Button m_RoomButton;
        float m_NextBannerRefresh;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Server;
#endif

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            StartServer();
            BuildDock();
            SettingsSummoner.RecenterAction = RecenterAll;
            EnvironmentManager.IsolationChanged += OnIsolationChanged;
            AddScreenInstance();
            Debug.Log("[StreamCast] Multi-stream receiver ready (up to 4 simultaneous screens)");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (core != null)
                m_Core = core;
            if (m_Dock != null && core != null && core.Head != null)
                WorldUi.BringToFront(m_Dock, core.Head, k_DockDistance, k_DockPitch);
            if (m_Windows.Count == 0)
            {
                AddScreenInstance();
                return;
            }
            if (core != null && core.Head != null)
            {
                for (var i = 0; i < m_Windows.Count; i++)
                {
                    var yaw = m_Windows.Count == 1 ? 0f
                        : (m_Windows.Count == 2 ? (i == 0 ? -34f : 34f)
                        : (i == 0 ? 0f : (i == 1 ? -42f : 42f)));
                    m_Windows[i].BringToFront(core.Head, yaw);
                }
            }
        }

        public override void OnDemoExit(CoreServices core)
        {
            EnvironmentManager.IsolationChanged -= OnIsolationChanged;
            if (SettingsSummoner.RecenterAction == RecenterAll)
                SettingsSummoner.RecenterAction = null;
            if (ReferenceEquals(PinchRouter.RotaryHandler, this))
                PinchRouter.RotaryHandler = null;

            foreach (var w in m_Windows)
            {
                if (w != null)
                    Destroy(w.gameObject);
            }
            m_Windows.Clear();
            StopServer();
        }

        void StartServer()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Server = new AndroidJavaObject("com.brenpoly.cardboardxr.stream.StreamReceiverServer", activity);
                m_Server.Call("start");
                Debug.Log($"[StreamCast] Server listening at {GetHttpsUrl()} and {GetHttpUrl()}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamCast] Could not start StreamReceiverServer: {e.Message}");
            }
#endif
        }

        void StopServer()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Server != null)
            {
                try
                {
                    m_Server.Call("stop");
                    m_Server.Dispose();
                }
                catch (Exception)
                {
                }
                m_Server = null;
            }
#endif
        }

        string GetHttpsUrl()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Server != null)
            {
                try { return m_Server.Call<string>("getHttpsUrl"); } catch (Exception) {}
            }
#endif
            return "https://192.168.1.x:8443";
        }

        string GetHttpUrl()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Server != null)
            {
                try { return m_Server.Call<string>("getHttpUrl"); } catch (Exception) {}
            }
#endif
            return "http://192.168.1.x:8765";
        }

        void BuildDock()
        {
            m_Dock = WorldUi.Panel("Stream Dock", transform, new Vector2(700f, 132f), k_DockDistance, radius: 46f, scaleFactor: 0.72f);

            m_AddressBanner = M3.Text("Address Banner", m_Dock, BuildBannerText(), M3Theme.Type.TitleMedium,
                new Vector2(0f, 32f), new Vector2(656f, 40f), M3Theme.OnSurface);

            const float bottomY = -30f;
            m_AddButton = M3.Button("Add Screen", m_Dock, "+ Nuovo Schermo (1/4)", M3Button.Variant.Filled,
                new Vector2(-128f, bottomY), AddScreenInstance, "add", 248f);
            m_RoomButton = M3.Button("Room Toggle", m_Dock, EnvironmentManager.IsIsolated ? "Ambiente 360°" : "Stanza AR",
                M3Button.Variant.Tonal, new Vector2(148f, bottomY), TogglePassthrough, "landscape", 210f);
            m_RoomButton.Selected = EnvironmentManager.IsIsolated;

            WorldUi.PlaceInFront(m_Dock, m_Core.Head, k_DockDistance, k_DockPitch);
            FloatingWindowHandle.Configure(m_Dock, () => m_Core?.Demos?.CloseApp("streamcast"), restoreOnRecenter: false);
        }

        string BuildBannerText() => $"{GetHttpsUrl()}   •   WebRTC 60 fps";

        void Update()
        {
            if (m_AddressBanner != null && Time.unscaledTime >= m_NextBannerRefresh)
            {
                m_NextBannerRefresh = Time.unscaledTime + 2f;
                m_AddressBanner.text = BuildBannerText();
            }
        }

        int FindFreeChannel()
        {
            for (var ch = 0; ch < k_MaxScreens; ch++)
            {
                var used = false;
                for (var i = 0; i < m_Windows.Count; i++)
                {
                    if (m_Windows[i] != null && m_Windows[i].ChannelIndex == ch)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used)
                    return ch;
            }
            return -1;
        }

        void AddScreenInstance()
        {
            var channel = FindFreeChannel();
            if (channel < 0)
            {
                Debug.Log("[StreamCast] Maximum 4 simultaneous screens reached");
                return;
            }

            var window = StreamCastWindow.Create(
                transform,
                channel,
#if UNITY_ANDROID && !UNITY_EDITOR
                m_Server,
#else
                null,
#endif
                () => m_Core != null ? m_Core.Head : null,
                GetHttpsUrl,
                GetHttpUrl,
                OnWindowClosed);
            var slotIndex = m_Windows.Count;
            m_Windows.Add(window);
            RefreshAddButton();
            // Keep existing windows locked where the user placed them; only place the new screen!
            if (m_Core != null && m_Core.Head != null)
            {
                var yaw = slotIndex switch { 0 => 0f, 1 => 44f, 2 => -44f, _ => 82f };
                window.PlaceAtYaw(m_Core.Head, yaw);
            }
            Debug.Log($"[StreamCast] Opened Schermo {channel + 1} ({m_Windows.Count}/{k_MaxScreens})");
        }

        void OnWindowClosed(StreamCastWindow window)
        {
            if (window == null)
                return;
            m_Windows.Remove(window);
            Destroy(window.gameObject);
            RefreshAddButton();
        }

        void RefreshAddButton()
        {
            if (m_AddButton == null)
                return;
            m_AddButton.Text = m_Windows.Count >= k_MaxScreens
                ? $"Max {k_MaxScreens}/{k_MaxScreens} Schermi"
                : $"+ Nuovo Schermo ({m_Windows.Count}/{k_MaxScreens})";
        }

        void RecenterAll()
        {
            if (m_Dock != null && !M3Fade.IsShown(m_Dock.gameObject))
                M3Fade.Show(m_Dock.gameObject);
            ArrangeWindows();
        }

        void ArrangeWindows()
        {
            if (m_Core == null || m_Core.Head == null)
                return;
            const float stepYaw = 40f;
            var startYaw = -(m_Windows.Count - 1) * stepYaw * 0.5f;
            for (var i = 0; i < m_Windows.Count; i++)
            {
                var yaw = startYaw + i * stepYaw;
                m_Windows[i].PlaceAtYaw(m_Core.Head, yaw);
            }
            if (m_Dock != null)
                WorldUi.PlaceInFront(m_Dock, m_Core.Head, k_DockDistance, k_DockPitch);
            FloatingWindowHandle.Configure(m_Dock, () => m_Core?.Demos?.CloseApp("streamcast"), restoreOnRecenter: false);
        }

        public bool TryHandleRotary(GameObject gazeTarget, float rotarySteps)
        {
            // Crown always resizes the screen under gaze in-place (handled by PinchRouter.ResizeFloatingWindow),
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
