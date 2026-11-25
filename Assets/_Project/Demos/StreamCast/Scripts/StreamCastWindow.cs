using System;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.StreamCast
{
    // A single floating 3D VR screen receiving a hardware-accelerated WebRTC stream (up to 1080p 60 fps
    // H.264/VP9 + Opus stereo audio) via an internal loopback VrWebView connected to StreamReceiverServer.
    public class StreamCastWindow : MonoBehaviour
    {
        const int k_TexWidth = 1280;
        const int k_TexHeight = 720;
        const int k_ByteCount = k_TexWidth * k_TexHeight * 4;
        const float k_DefaultDistance = 1.85f;
        const float k_MinDistance = 0.85f;
        const float k_MaxDistance = 4.20f;
        // Ergonomic Apple Vision Pro default window scale (~43.5 deg FOV); Cinema button expands to ~61 deg FOV.
        const float k_DefaultScaleFactor = 0.62f;
        const float k_CinemaScaleFactor = 1.48f;
        const float k_SyncInterval = 1f / 45f;
        const float k_StatusInterval = 0.4f;

        // Borderless full-bleed 16:9 Apple Vision Pro stream screen (zero thick dark bezel).
        static readonly Vector2 k_PanelSize = new(928f, 522f);
        static readonly Vector2 k_ScreenSize = new(928f, 522f);

        int m_ChannelIndex;
        RectTransform m_Panel;
        RawImage m_Screen;
        Texture2D m_Texture;
        TMP_Text m_StatusText;
        GameObject m_WaitingOverlay;
        TMP_Text m_WaitingUrlText;
        M3Button m_MuteButton;
        M3Button m_SizeButton;
        bool m_Muted;
        bool m_CinemaScale;
        float m_BaseScale;
        float m_Distance = k_DefaultDistance;
        float m_NextSync;
        float m_NextStatus;
        Action<StreamCastWindow> m_OnClose;
        Func<Transform> m_HeadProvider;
        Func<string> m_HttpsUrlProvider;
        Func<string> m_HttpUrlProvider;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Server;
        AndroidJavaObject m_WebView;
        AndroidJavaObject m_FrontBufferObj;
        IntPtr m_FrontBufferPtr;
#endif

        public int ChannelIndex => m_ChannelIndex;
        public RectTransform Panel => m_Panel;

        public static StreamCastWindow Create(Transform parent, int channelIndex,
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJavaObject server,
#else
            object server,
#endif
            Func<Transform> headProvider,
            Func<string> httpsUrlProvider,
            Func<string> httpUrlProvider,
            Action<StreamCastWindow> onClose)
        {
            var panel = WorldUi.BorderlessWindow($"Stream Window {channelIndex + 1}", parent, k_PanelSize,
                k_DefaultDistance, k_DefaultScaleFactor);
            var window = panel.gameObject.AddComponent<StreamCastWindow>();
            window.m_ChannelIndex = channelIndex;
            window.m_Panel = panel;
            window.m_BaseScale = M3Fade.GetBaseScale(panel.gameObject).x;
            window.m_Distance = k_DefaultDistance;
            window.m_HeadProvider = headProvider;
            window.m_HttpsUrlProvider = httpsUrlProvider;
            window.m_HttpUrlProvider = httpUrlProvider;
            window.m_OnClose = onClose;
#if UNITY_ANDROID && !UNITY_EDITOR
            window.m_Server = server;
#endif
            window.BuildUi();
#if UNITY_ANDROID && !UNITY_EDITOR
            window.InitReceiverWebView();
#endif
            FloatingWindowHandle.Configure(panel, () => window.m_OnClose?.Invoke(window), restoreOnRecenter: false);
            return window;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void InitReceiverWebView()
        {
            if (m_Server == null)
                return;
            try
            {
                var receiverUrl = m_Server.Call<string>("getReceiverUrl", m_ChannelIndex);
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_WebView = new AndroidJavaObject("com.brenpoly.cardboardxr.browser.VrWebView",
                    activity, k_TexWidth, k_TexHeight, k_TexWidth, k_TexHeight, receiverUrl);
                m_FrontBufferObj = m_WebView.Call<AndroidJavaObject>("getFrontBuffer");
                if (m_FrontBufferObj != null)
                {
                    unsafe
                    {
                        m_FrontBufferPtr = (IntPtr)AndroidJNI.GetDirectBufferAddress(m_FrontBufferObj.GetRawObject());
                    }
                }
                Debug.Log($"[StreamCast] Hardware WebRTC receiver ready on Schermo {m_ChannelIndex + 1} ({receiverUrl})");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamCast] Could not start receiver WebView on Schermo {m_ChannelIndex + 1}: {e.Message}");
            }
        }
#endif

        void BuildUi()
        {
            var num = m_ChannelIndex + 1;

            // VisionOS-style detached frosted glass ornament pill floating cleanly above the borderless screen.
            const float ornamentY = 298f;
            var topBar = M3.Surface("Top Ornament", m_Panel, new Vector2(0f, ornamentY), new Vector2(780f, 52f),
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull, raycastTarget: true);
            var bar = topBar.rectTransform;

            var badge = M3.Surface("Badge", bar, new Vector2(-318f, 0f), new Vector2(118f, 36f),
                M3Theme.PrimaryContainer, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy);
            M3.Text("BadgeLabel", badge.rectTransform, $"Schermo {num}", M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(108f, 30f), M3Theme.OnPrimaryContainer);

            var statusBg = M3.Surface("Status Pill", bar, new Vector2(-30f, 0f), new Vector2(430f, 36f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Recessed);
            m_StatusText = M3.Text("Status", statusBg.rectTransform, $"In attesa su Schermo {num}...",
                M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(404f, 30f), M3Theme.OnSurface);
            m_StatusText.textWrappingMode = TextWrappingModes.NoWrap;

            m_MuteButton = M3.IconButton("Mute", bar, "volume_up", M3Button.Variant.Tonal,
                new Vector2(226f, 0f), ToggleMute, 38f);
            m_SizeButton = M3.IconButton("Size", bar, "aspect_ratio", M3Button.Variant.Tonal,
                new Vector2(282f, 0f), ToggleCinemaScale, 38f);
            M3.IconButton("Disconnect", bar, "refresh", M3Button.Variant.Tonal,
                new Vector2(338f, 0f), DisconnectStream, 38f);

            m_Texture = new Texture2D(k_TexWidth, k_TexHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            FillPlaceholder(m_Texture);

            m_Screen = M3.RoundedScreen("Stream Screen", m_Panel, Vector2.zero, k_ScreenSize, 42f,
                raycastTarget: true, rimStrength: 0.55f, sheenStrength: 0.16f);
            var screenRect = m_Screen.rectTransform;
            m_Screen.texture = m_Texture;
            m_Screen.uvRect = new Rect(0f, 1f, 1f, -1f);

            // Translucent visionOS frosted waiting card filling the rounded 16:9 viewport.
            var overlay = M3.Surface("Waiting Overlay", screenRect, Vector2.zero, k_ScreenSize,
                M3Theme.SurfaceContainer, 42f, style: VisionSurfaceStyle.Frosted);
            m_WaitingOverlay = overlay.gameObject;

            M3.Icon("Cast Icon", overlay.rectTransform, "cast", 68f, new Vector2(0f, 120f), M3Theme.GlassAccent);
            M3.Text("Waiting Title", overlay.rectTransform, $"Schermo {num}",
                M3Theme.Type.HeadlineSmall, new Vector2(0f, 44f), new Vector2(820f, 42f), M3Theme.OnSurface);

            var urlCard = M3.Surface("URL Card", overlay.rectTransform, new Vector2(0f, -58f), new Vector2(780f, 138f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            m_WaitingUrlText = M3.Text("Waiting URL", urlCard.rectTransform, BuildWaitingInstructions(),
                M3Theme.Type.TitleMedium, Vector2.zero, new Vector2(732f, 114f), M3Theme.OnSurface);
        }

        string BuildWaitingInstructions()
        {
            var num = m_ChannelIndex + 1;
            var https = m_HttpsUrlProvider != null ? m_HttpsUrlProvider() : "https://<IP-PIXEL-9>:8443";
            return $"Apri dal browser su PC / Mac / iPad / iPhone:\n{https}/?ch={num}\n\nWebRTC Hardware 60 fps • Audio Stereo Opus";
        }

        static void FillPlaceholder(Texture2D texture)
        {
            var pixels = new Color32[texture.width * texture.height];
            var fill = new Color32(12, 15, 24, 255);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = fill;
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        void Update()
        {
            var now = Time.unscaledTime;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_WebView != null && m_FrontBufferPtr != IntPtr.Zero && now >= m_NextSync)
            {
                m_NextSync = now + k_SyncInterval;
                try
                {
                    if (m_WebView.Call<bool>("syncFrontBuffer"))
                    {
                        m_Texture.LoadRawTextureData(m_FrontBufferPtr, k_ByteCount);
                        m_Texture.Apply(false);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StreamCast] Frame sync failed on Schermo {m_ChannelIndex + 1}: {e.Message}");
                }
            }

            if (m_Server != null && now >= m_NextStatus)
            {
                m_NextStatus = now + k_StatusInterval;
                try
                {
                    var connected = m_Server.Call<bool>("isChannelConnected", m_ChannelIndex);
                    var status = m_Server.Call<string>("getChannelStatus", m_ChannelIndex);
                    if (m_StatusText != null && !string.IsNullOrEmpty(status))
                        m_StatusText.text = status;
                    if (m_WaitingOverlay != null && m_WaitingOverlay.activeSelf == connected)
                        m_WaitingOverlay.SetActive(!connected);
                    if (!connected && m_WaitingUrlText != null)
                        m_WaitingUrlText.text = BuildWaitingInstructions();
                }
                catch (Exception)
                {
                }
            }
#else
            if (now >= m_NextStatus)
            {
                m_NextStatus = now + k_StatusInterval;
                if (m_WaitingUrlText != null)
                    m_WaitingUrlText.text = BuildWaitingInstructions();
            }
#endif
        }

        void ToggleMute()
        {
            m_Muted = !m_Muted;
            if (m_MuteButton != null)
            {
                m_MuteButton.Selected = m_Muted;
                var iconTmp = m_MuteButton.GetComponentInChildren<TMP_Text>(true);
                if (iconTmp != null)
                    iconTmp.text = M3Icons.Glyph(m_Muted ? "volume_off" : "volume_up");
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("setMuted", m_Muted);
#endif
        }

        void DisconnectStream()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Server?.Call("disconnectChannel", m_ChannelIndex);
#endif
            if (m_WaitingOverlay != null)
                m_WaitingOverlay.SetActive(true);
            Debug.Log($"[StreamCast] Reset Schermo {m_ChannelIndex + 1}");
        }

        void ToggleCinemaScale()
        {
            m_CinemaScale = !m_CinemaScale;
            if (m_SizeButton != null)
                m_SizeButton.Selected = m_CinemaScale;
            var factor = m_CinemaScale ? k_CinemaScaleFactor : 1f;
            M3Fade.SetBaseScale(m_Panel.gameObject, Vector3.one * (m_BaseScale * factor));
            SpatialWindowAnchor.SyncBaseScale(m_Panel);
        }

        public void AdjustDistance(float deltaMeters)
        {
            var head = m_HeadProvider?.Invoke();
            if (head == null || m_Panel == null)
                return;
            var offset = m_Panel.position - head.position;
            var currentDist = offset.sqrMagnitude > 1e-4f ? offset.magnitude : m_Distance;
            m_Distance = Mathf.Clamp(currentDist + deltaMeters, k_MinDistance, k_MaxDistance);
            var dir = offset.sqrMagnitude > 1e-4f ? offset.normalized : head.forward;
            var pos = head.position + dir * m_Distance;
            m_Panel.SetPositionAndRotation(pos, M3Theme.FacingRotation(pos, head.position));
            SpatialWindowAnchor.PinAtCurrentPose(m_Panel, 0.25f);
        }

        public void MoveInFrontOfHead()
        {
            var head = m_HeadProvider?.Invoke();
            if (head == null)
                return;
            WorldUi.PlaceInFront(m_Panel, head, m_Distance, 3f, 0f);
        }

        public void PlaceAtYaw(Transform head, float yawDegrees)
        {
            if (head == null)
                return;
            WorldUi.PlaceInFront(m_Panel, head, m_Distance, 3f, yawDegrees);
        }

        public void BringToFront(Transform head, float yawDegrees = 0f)
        {
            WorldUi.BringToFront(m_Panel, head, m_Distance, 3f, yawDegrees);
        }

        void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_FrontBufferPtr = IntPtr.Zero;
            if (m_FrontBufferObj != null)
            {
                try { m_FrontBufferObj.Dispose(); } catch (Exception) {}
                m_FrontBufferObj = null;
            }
            if (m_WebView != null)
            {
                try
                {
                    m_WebView.Call("dispose");
                    m_WebView.Dispose();
                }
                catch (Exception) {}
                m_WebView = null;
            }
            try { m_Server?.Call("disconnectChannel", m_ChannelIndex); } catch (Exception) {}
#endif
            if (m_Texture != null)
                Destroy(m_Texture);
        }
    }
}
