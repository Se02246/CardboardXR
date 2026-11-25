using System;
using CardboardXR.Demos.Video;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.Demos.Browser
{
    // A floating 3D desktop browser window in world space, backed by an Android WebView (VrWebView.java).
    // Look anywhere on the web surface and double-pinch on the Pixel Watch (or tap the screen) to click
    // that spot on the desktop page. Logs [Browser].
    public class BrowserWindow : MonoBehaviour, IWatchScrollable
    {
        const int k_ViewWidth = 1600;
        const int k_ViewHeight = 900;
        const int k_TexWidth = 1440;
        const int k_TexHeight = 810;
        const int k_ByteCount = k_TexWidth * k_TexHeight * 4;
        const float k_DefaultDistance = 1.85f;
        const float k_MinDistance = 0.85f;
        const float k_MaxDistance = 4.20f;
        // Ergonomic Apple Vision Pro default window scale (~43.5 deg FOV): fits cleanly inside
        // the sharp view with room for side-by-side screens; Cinema button expands to ~61 deg FOV.
        const float k_DefaultScaleFactor = 0.62f;
        const float k_CinemaScaleFactor = 1.48f;
        const float k_CaptureInterval = 1f / 24f;
        const float k_MetadataInterval = 0.5f;

        // Borderless full-bleed 16:9 Apple Vision Pro cinema screen (zero thick dark bezel).
        static readonly Vector2 k_PanelSize = new(928f, 522f);
        static readonly Vector2 k_ScreenSize = new(928f, 522f);

        RectTransform m_Panel;
        Canvas m_Canvas;
        GraphicRaycaster m_Raycaster;
        RawImage m_Screen;
        Texture2D m_Texture;
        TMP_Text m_AddressText;
        M3Button m_SizeButton;
        M3Button m_Vr360Button;
        bool m_DesktopMode = true;
        bool m_CinemaScale;
        bool m_Vr360Active;
        string m_CurrentTitle = "";
        string m_CurrentUrl = "";
        string m_DismissedVrKey;
        float m_BaseScale;
        float m_Distance = k_DefaultDistance;
        float m_NextCapture;
        float m_NextMetadata;
        Action<BrowserWindow> m_OnFocus;
        Action<BrowserWindow> m_OnClose;
        Action<BrowserWindow, bool> m_OnVr360Changed;
        Func<Transform> m_HeadProvider;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_WebView;
        AndroidJavaObject m_FrontBufferObj;
        IntPtr m_FrontBufferPtr;
#endif

        public RectTransform Panel => m_Panel;
        public Texture VideoTexture => m_Texture;
        public bool IsVr360Active => m_Vr360Active;
        public VideoFormat InferredVrFormat => BrowserRules.InferVrVideoFormat(m_CurrentTitle, m_CurrentUrl);

        public static BrowserWindow Create(Transform parent, Func<Transform> headProvider, string initialUrl,
            Action<BrowserWindow> onFocus, Action<BrowserWindow> onClose,
            Action<BrowserWindow, bool> onVr360Changed = null)
        {
            var panel = WorldUi.BorderlessWindow("Browser Window", parent, k_PanelSize, k_DefaultDistance, k_DefaultScaleFactor);
            var window = panel.gameObject.AddComponent<BrowserWindow>();
            window.m_Panel = panel;
            window.m_Canvas = panel.GetComponent<Canvas>();
            window.m_Raycaster = panel.GetComponent<GraphicRaycaster>();
            window.m_BaseScale = M3Fade.GetBaseScale(panel.gameObject).x;
            window.m_Distance = k_DefaultDistance;
            window.m_HeadProvider = headProvider;
            window.m_OnFocus = onFocus;
            window.m_OnClose = onClose;
            window.m_OnVr360Changed = onVr360Changed;
            window.m_CurrentUrl = initialUrl ?? "";
            window.BuildUi(initialUrl);
            window.InitWebView(initialUrl);
            FloatingWindowHandle.Configure(panel, () => window.m_OnClose?.Invoke(window), restoreOnRecenter: false);
            return window;
        }

        void BuildUi(string initialUrl)
        {
            // VisionOS-style detached frosted glass control pill floating cleanly above the borderless screen.
            const float ornamentY = 298f;
            var topBar = M3.Surface("Top Ornament", m_Panel, new Vector2(0f, ornamentY), new Vector2(824f, 52f),
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull, raycastTarget: true);
            var bar = topBar.rectTransform;

            M3.IconButton("Back", bar, "chevron_left", M3Button.Variant.Tonal, new Vector2(-376f, 0f), GoBack, 38f);
            M3.IconButton("Reload", bar, "refresh", M3Button.Variant.Tonal, new Vector2(-328f, 0f), Reload, 38f);

            var addressBg = M3.Surface("Address Pill", bar, new Vector2(-86f, 0f), new Vector2(408f, 36f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Recessed);
            m_AddressText = M3.Text("Address", addressBg.rectTransform, initialUrl, M3Theme.Type.BodyMedium,
                Vector2.zero, new Vector2(380f, 30f), M3Theme.OnSurface);
            m_AddressText.textWrappingMode = TextWrappingModes.NoWrap;

            M3.IconButton("Play Pause", bar, "play_arrow", M3Button.Variant.Tonal, new Vector2(152f, 0f), TogglePlayPause, 38f);
            m_Vr360Button = M3.IconButton("VR 360", bar, "360", M3Button.Variant.Tonal,
                new Vector2(204f, 0f), ToggleVr360Manual, 38f);
            M3.Key("Zoom Out", bar, "−", M3Button.Variant.Tonal, new Vector2(258f, 0f), new Vector2(42f, 38f), () => AdjustZoom(-12));
            M3.Key("Zoom In", bar, "+", M3Button.Variant.Tonal, new Vector2(310f, 0f), new Vector2(42f, 38f), () => AdjustZoom(12));
            m_SizeButton = M3.IconButton("Size", bar, "aspect_ratio", M3Button.Variant.Tonal,
                new Vector2(366f, 0f), ToggleCinemaScale, 38f);

            m_Texture = new Texture2D(k_TexWidth, k_TexHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            FillPlaceholder(m_Texture);

            // Full-bleed borderless 16:9 cinema viewport with anti-aliased rounded corners & hairline glass rim.
            m_Screen = M3.RoundedScreen("Web Screen", m_Panel, Vector2.zero, k_ScreenSize, 42f,
                raycastTarget: true, rimStrength: 0.55f, sheenStrength: 0.16f);
            m_Screen.texture = m_Texture;
            // Android RGBA buffer rows are top-to-bottom; flipping V in the RawImage UV avoids any CPU row copy.
            m_Screen.uvRect = new Rect(0f, 1f, 1f, -1f);

            var surface = m_Screen.gameObject.AddComponent<BrowserSurface>();
            surface.Initialize(this, m_Screen.rectTransform);
        }

        static void FillPlaceholder(Texture2D texture)
        {
            var pixels = new Color32[texture.width * texture.height];
            var fill = new Color32(22, 24, 32, 255);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = fill;
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        void InitWebView(string initialUrl)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_WebView = new AndroidJavaObject("com.brenpoly.cardboardxr.browser.VrWebView",
                    activity, k_ViewWidth, k_ViewHeight, k_TexWidth, k_TexHeight, initialUrl);
                m_FrontBufferObj = m_WebView.Call<AndroidJavaObject>("getFrontBuffer");
                if (m_FrontBufferObj != null)
                {
                    unsafe
                    {
                        m_FrontBufferPtr = (IntPtr)AndroidJNI.GetDirectBufferAddress(m_FrontBufferObj.GetRawObject());
                    }
                }
                Debug.Log($"[Browser] Opened floating window: {initialUrl}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Browser] Could not create Android WebView: {e.Message}");
            }
#else
            Debug.Log($"[Browser] Opened floating window (Editor preview): {initialUrl}");
#endif
        }

        void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_WebView == null || m_FrontBufferPtr == IntPtr.Zero || Time.unscaledTime < m_NextCapture)
                return;
            m_NextCapture = Time.unscaledTime + k_CaptureInterval;
            try
            {
                if (m_WebView.Call<bool>("syncFrontBuffer"))
                {
                    m_Texture.LoadRawTextureData(m_FrontBufferPtr, k_ByteCount);
                    m_Texture.Apply(false);
                }
                if (Time.unscaledTime >= m_NextMetadata)
                {
                    m_NextMetadata = Time.unscaledTime + k_MetadataInterval;
                    var title = m_WebView.Call<string>("getTitle") ?? "";
                    var url = m_WebView.Call<string>("getUrl") ?? "";
                    m_CurrentTitle = title;
                    m_CurrentUrl = url;
                    if (!string.IsNullOrEmpty(title))
                        m_AddressText.text = title;
                    else if (!string.IsNullOrEmpty(url))
                        m_AddressText.text = url;

                    var vrDetected = m_WebView.Call<bool>("isVr360VideoDetected");
                    var dismissKey = !string.IsNullOrEmpty(url) ? url : title;
                    if (!m_Vr360Active && vrDetected && m_DismissedVrKey != dismissKey)
                        SetVr360Mode(true, userDismissed: false);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Browser] Capture failed: {e.Message}");
            }
#endif
        }

        public void SetWindowVisualVisible(bool visible)
        {
            if (m_Canvas != null)
                m_Canvas.enabled = visible;
            if (m_Raycaster != null)
                m_Raycaster.enabled = visible;
            var handle = GetComponent<FloatingWindowHandle>();
            if (handle != null)
                handle.enabled = visible;
        }

        void ToggleVr360Manual()
        {
            m_OnFocus?.Invoke(this);
            SetVr360Mode(!m_Vr360Active, userDismissed: m_Vr360Active);
        }

        public void SetVr360Mode(bool active, bool userDismissed = false)
        {
            if (m_Vr360Active == active)
                return;
            m_Vr360Active = active;
            if (active)
                m_DismissedVrKey = null;
            else if (userDismissed)
                m_DismissedVrKey = !string.IsNullOrEmpty(m_CurrentUrl) ? m_CurrentUrl : m_CurrentTitle;
            if (m_Vr360Button != null)
                m_Vr360Button.Selected = active;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("setVr360Mode", active);
#endif
            Debug.Log($"[Browser] VR 360 mode {(active ? "ON" : "OFF")} ({m_CurrentUrl})");
            m_OnVr360Changed?.Invoke(this, active);
        }

        public void LoadUrl(string url)
        {
            m_OnFocus?.Invoke(this);
            m_DismissedVrKey = null;
            if (m_Vr360Active)
                SetVr360Mode(false, userDismissed: false);
            m_CurrentUrl = url ?? "";
            m_AddressText.text = url;
            Debug.Log($"[Browser] Loading {url}");
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("loadUrl", url);
#endif
        }

        public void HandleSurfaceClick(Vector2 normalized)
        {
            m_OnFocus?.Invoke(this);
            Debug.Log($"[Browser] Click ({normalized.x:F2}, {normalized.y:F2})");
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("clickNormalized", normalized.x, normalized.y);
#endif
        }

        public void Scroll(int deltaY)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("scroll", deltaY);
#endif
        }

        public void ScrollDrag(int deltaX, int deltaY, float normU, float normV)
        {
            m_OnFocus?.Invoke(this);
            if (deltaX == 0 && deltaY == 0)
                return;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("scrollDrag", deltaX, deltaY, normU, normV);
#endif
        }

        public void TouchDown(float normU, float normV)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("touchDown", normU, normV);
#endif
        }

        public void TouchMove(float normU, float normV)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("touchMove", normU, normV);
#endif
        }

        public void TouchUp(float normU, float normV, bool cancel)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("touchUp", normU, normV, cancel);
#endif
        }

        public void AdjustZoom(int deltaPercent)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("adjustZoom", deltaPercent);
#endif
        }

        public bool TryHandleWatchScroll(float rotarySteps)
        {
            var deltaPx = Mathf.RoundToInt(rotarySteps * 220f);
            if (deltaPx == 0)
                return false;
            Scroll(deltaPx);
            return true;
        }

        // True if the webpage scrolled in rotarySteps's direction; false if there is nothing to scroll.
        public bool TryScrollRotary(float rotarySteps)
        {
            m_OnFocus?.Invoke(this);
            var deltaPx = Mathf.RoundToInt(rotarySteps * 220f);
            if (deltaPx == 0)
                return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_WebView != null)
                return m_WebView.Call<bool>("tryScroll", deltaPx);
#endif
            return false;
        }

        public void AdjustDistance(float deltaMeters)
        {
            m_OnFocus?.Invoke(this);
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

        public void TypeText(string text, bool submit)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("typeText", text ?? "", submit);
#endif
        }

        public void ClearInput()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("clearInput");
#endif
        }

        void GoBack()
        {
            m_OnFocus?.Invoke(this);
            if (m_Vr360Active)
                SetVr360Mode(false, userDismissed: false);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("goBack");
#endif
        }

        void GoForward()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("goForward");
#endif
        }

        void Reload()
        {
            m_OnFocus?.Invoke(this);
            m_DismissedVrKey = null;
            if (m_Vr360Active)
                SetVr360Mode(false, userDismissed: false);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("reload");
#endif
        }

        public void TogglePlayPause()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_WebView?.Call("togglePlayPause");
#endif
        }

        void ToggleCinemaScale()
        {
            m_OnFocus?.Invoke(this);
            m_CinemaScale = !m_CinemaScale;
            if (m_SizeButton != null)
                m_SizeButton.Selected = m_CinemaScale;
            var factor = m_CinemaScale ? k_CinemaScaleFactor : 1f;
            M3Fade.SetBaseScale(m_Panel.gameObject, Vector3.one * (m_BaseScale * factor));
            SpatialWindowAnchor.SyncBaseScale(m_Panel);
            Debug.Log($"[Browser] Cinema size {(m_CinemaScale ? "on" : "off")}");
        }

        public void MoveInFrontOfHead()
        {
            m_OnFocus?.Invoke(this);
            var head = m_HeadProvider?.Invoke();
            if (head == null)
                return;
            WorldUi.PlaceInFront(m_Panel, head, m_Distance, 3f, 0f);
            Debug.Log("[Browser] Window moved in front of gaze");
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
                catch (Exception)
                {
                }
                m_WebView = null;
            }
#endif
            if (m_Texture != null)
                Destroy(m_Texture);
        }
    }

    // Receives gaze clicks (from the Pixel Watch double pinch, hand pinch or screen tap) and
    // direct 1:1 touchscreen drag scrolling strictly inside the web viewport.
    public class BrowserSurface : MonoBehaviour, IPointerClickHandler, IVisualScrollTarget
    {
        BrowserWindow m_Window;
        RectTransform m_Rect;

        public RectTransform ScrollRectTransform => m_Rect;
        public bool CanVisualScroll => m_Window != null && m_Rect != null;

        public void Initialize(BrowserWindow window, RectTransform rect)
        {
            m_Window = window;
            m_Rect = rect;
        }

        public void OnVisualTouchBegin(Vector2 localPoint)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = BrowserRules.RectToNormalized(m_Rect.rect, localPoint);
            m_Window.TouchDown(n.x, n.y);
        }

        public void OnVisualTouchMove(Vector2 localPoint, Vector2 localDelta)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = BrowserRules.RectToNormalized(m_Rect.rect, localPoint);
            m_Window.TouchMove(n.x, n.y);
        }

        public void OnVisualTouchEnd(Vector2 localPoint, bool cancelled)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = BrowserRules.RectToNormalized(m_Rect.rect, localPoint);
            m_Window.TouchUp(n.x, n.y, cancelled);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var worldPoint = eventData.pointerCurrentRaycast.worldPosition;
            if (worldPoint == Vector3.zero)
                worldPoint = m_Rect.position;
            var local2D = (Vector2)m_Rect.InverseTransformPoint(worldPoint);
            var normalized = BrowserRules.RectToNormalized(m_Rect.rect, local2D);
            m_Window.HandleSurfaceClick(normalized);
        }
    }
}
