using System;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.Demos.PhoneApps
{
    // Floating 3D window rendering a live real Android app on a Shizuku VirtualDisplay.
    // Opens in vertical smartphone orientation by default with a Rotate button to toggle
    // dynamically between vertical (portrait) and horizontal (landscape). Logs [PhoneApps].
    public class PhoneAppWindow : MonoBehaviour, IWatchScrollable
    {
        const float k_DefaultDistance = 1.65f;
        const float k_DefaultScaleFactor = 0.62f;

        RectTransform m_Panel;
        RectTransform m_TopBar;
        RectTransform m_TitleRect;
        RectTransform m_ScreenRect;
        RawImage m_Screen;
        Texture2D m_Texture;
        TMP_Text m_TitleText;
        GameObject m_OverlayPill;
        TMP_Text m_OverlayText;
        M3Button m_BackButton;
        M3Button m_VrFullscreenButton;
        M3Button m_RotateButton;
        M3Button m_KeyboardButton;

        string m_PackageName;
        string m_ActivityName;
        string m_AppLabel;
        Color m_AccentColor;
        bool m_Landscape;
        bool m_IsFullscreenVr;
        bool m_HasFirstFrame;
        int m_LoggedDisplayId = -1;
        string m_LastLoggedError = "";
        int m_TexWidth;
        int m_TexHeight;
        float m_Distance = k_DefaultDistance;
        float m_NextCapture;
        float m_NextDiagPoll;
        float m_PendingRotarySteps;
        float m_NextRotarySwipeAt;

        Func<Transform> m_HeadProvider;
        Func<int> m_WindowCountProvider;
        Action<PhoneAppWindow> m_OnFocus;
        Action<PhoneAppWindow> m_OnClose;
        Action m_OnToggleKeyboard;
        Action<PhoneAppWindow> m_OnToggleFullscreen;

        static int s_StaggerSlot;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Session;
        AndroidJavaObject m_Buffer0Obj;
        AndroidJavaObject m_Buffer1Obj;
        IntPtr m_Buffer0Ptr;
        IntPtr m_Buffer1Ptr;
#endif

        public RectTransform Panel => m_Panel;
        public string PackageName => m_PackageName;
        public string AppLabel => m_AppLabel;
        public bool IsLandscape => m_Landscape;
        public Texture2D ScreenTexture => m_Texture;
        public event Action<Texture2D> TextureChanged;

        public bool IsFullscreenVr
        {
            get => m_IsFullscreenVr;
            set
            {
                m_IsFullscreenVr = value;
                if (m_VrFullscreenButton != null)
                    m_VrFullscreenButton.Selected = value;
            }
        }

        public bool PauseCapture { get; set; }

        public static PhoneAppWindow Create(
            Transform parent,
            Func<Transform> headProvider,
            Func<int> windowCountProvider,
            object androidHost,
            string packageName,
            string activityName,
            string label,
            Color accentColor,
            Action<PhoneAppWindow> onFocus,
            Action<PhoneAppWindow> onClose,
            Action onToggleKeyboard,
            Action<PhoneAppWindow> onToggleFullscreen = null)
        {
            var initialSize = PhoneAppsRules.PanelSize(landscape: false);
            var panel = WorldUi.BorderlessWindow($"PhoneApp - {label}", parent, initialSize, k_DefaultDistance, k_DefaultScaleFactor);
            var win = panel.gameObject.AddComponent<PhoneAppWindow>();
            win.m_Panel = panel;
            win.m_Distance = k_DefaultDistance;
            win.m_PackageName = packageName;
            win.m_ActivityName = activityName;
            win.m_AppLabel = string.IsNullOrWhiteSpace(label) ? packageName : label;
            win.m_AccentColor = accentColor;
            win.m_Landscape = false;
            win.m_HeadProvider = headProvider;
            win.m_WindowCountProvider = windowCountProvider;
            win.m_OnFocus = onFocus;
            win.m_OnClose = onClose;
            win.m_OnToggleKeyboard = onToggleKeyboard;
            win.m_OnToggleFullscreen = onToggleFullscreen;

            win.BuildUi();
            win.ApplyOrientationLayout();
            win.InitSession(androidHost);
            FloatingWindowHandle.Configure(panel, () => win.m_OnClose?.Invoke(win), restoreOnRecenter: false);
            SpatialWindowAnchor.SyncBaseScale(panel);
            return win;
        }

        void BuildUi()
        {
            var topBarImage = M3.Surface("Top Ornament", m_Panel,
                new Vector2(0f, PhoneAppsRules.TopBarY(m_Landscape)),
                new Vector2(PhoneAppsRules.TopBarWidth(m_Landscape), 50f),
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull, raycastTarget: true);
            m_TopBar = topBarImage.rectTransform;

            m_BackButton = M3.IconButton("Back", m_TopBar, "chevron_left", M3Button.Variant.Tonal,
                Vector2.zero, PressBack, 38f);

            m_TitleText = M3.Text("App Title", m_TopBar, m_AppLabel, M3Theme.Type.TitleMedium,
                Vector2.zero, new Vector2(184f, 34f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            m_TitleText.textWrappingMode = TextWrappingModes.NoWrap;
            m_TitleRect = m_TitleText.rectTransform;

            m_VrFullscreenButton = M3.IconButton("VR Fullscreen", m_TopBar, "360", M3Button.Variant.Tonal,
                Vector2.zero, OnVrFullscreenClicked, 38f);
            m_RotateButton = M3.IconButton("Rotate", m_TopBar, "refresh", M3Button.Variant.Tonal,
                Vector2.zero, ToggleOrientation, 38f);
            m_KeyboardButton = M3.IconButton("Keyboard", m_TopBar, "keyboard", M3Button.Variant.Tonal,
                Vector2.zero, OnKeyboardClicked, 38f);

            var dims = PhoneAppsRules.DisplaySize(m_Landscape);
            AllocateTextures(dims.x, dims.y);

            m_Screen = M3.RoundedScreen("Phone App Screen", m_Panel, Vector2.zero, PhoneAppsRules.ScreenSize(m_Landscape),
                42f, raycastTarget: true, rimStrength: 0.58f, sheenStrength: 0.18f);
            m_ScreenRect = m_Screen.rectTransform;
            m_Screen.texture = m_Texture;
            // Android RGBA buffer rows are top-to-bottom; flipping V avoids CPU row copies.
            m_Screen.uvRect = new Rect(0f, 1f, 1f, -1f);

            m_Screen.gameObject.AddComponent<PhoneAppSurface>().Initialize(this, m_ScreenRect);

            var overlayPill = M3.Surface("Overlay Pill", m_Panel, Vector2.zero, new Vector2(320f, 68f),
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            m_OverlayPill = overlayPill.gameObject;
            m_OverlayText = M3.Text("Status Overlay", overlayPill.rectTransform, $"Avvio {m_AppLabel}...",
                M3Theme.Type.BodyMedium, Vector2.zero, new Vector2(288f, 52f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            m_OverlayText.textWrappingMode = TextWrappingModes.Normal;
        }

        void AllocateTextures(int width, int height)
        {
            if (m_Texture != null)
            {
                Destroy(m_Texture);
                m_Texture = null;
            }

            m_TexWidth = width;
            m_TexHeight = height;
            m_Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!m_HasFirstFrame)
                FillPlaceholder(m_Texture);

            if (m_Screen != null)
                m_Screen.texture = m_Texture;
            TextureChanged?.Invoke(m_Texture);
        }

        static void FillPlaceholder(Texture2D texture)
        {
            var pixels = new Color32[texture.width * texture.height];
            var bg = new Color32(14, 17, 25, 255);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = bg;
            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        void ApplyOrientationLayout()
        {
            m_Panel.sizeDelta = PhoneAppsRules.PanelSize(m_Landscape);
            if (m_ScreenRect != null)
                m_ScreenRect.sizeDelta = PhoneAppsRules.ScreenSize(m_Landscape);

            if (m_TopBar != null)
            {
                m_TopBar.anchoredPosition = new Vector2(0f, PhoneAppsRules.TopBarY(m_Landscape));
                m_TopBar.sizeDelta = new Vector2(PhoneAppsRules.TopBarWidth(m_Landscape), 50f);
            }

            if (m_Landscape)
            {
                SetAnchoredX(m_BackButton, -346f);
                m_TitleRect.anchoredPosition = new Vector2(-56f, 0f);
                m_TitleRect.sizeDelta = new Vector2(510f, 34f);
                SetAnchoredX(m_VrFullscreenButton, 246f);
                SetAnchoredX(m_RotateButton, 296f);
                SetAnchoredX(m_KeyboardButton, 346f);
            }
            else
            {
                SetAnchoredX(m_BackButton, -182f);
                m_TitleRect.anchoredPosition = new Vector2(-46f, 0f);
                m_TitleRect.sizeDelta = new Vector2(216f, 34f);
                SetAnchoredX(m_VrFullscreenButton, 90f);
                SetAnchoredX(m_RotateButton, 136f);
                SetAnchoredX(m_KeyboardButton, 182f);
            }

            if (m_RotateButton != null)
                m_RotateButton.Selected = m_Landscape;
        }

        static void SetAnchoredX(M3Button button, float x)
        {
            if (button != null && button.transform is RectTransform rt)
                rt.anchoredPosition = new Vector2(x, 0f);
        }

        void InitSession(object androidHost)
        {
            m_NextCapture = Time.unscaledTime + (s_StaggerSlot++ % 3) * 0.016f;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidHost is AndroidJavaObject hostObj)
            {
                try
                {
                    m_Session = hostObj.Call<AndroidJavaObject>("createSession", m_PackageName, m_ActivityName ?? "", m_Landscape);
                    m_Buffer0Obj = m_Session.Call<AndroidJavaObject>("getBuffer", 0);
                    m_Buffer1Obj = m_Session.Call<AndroidJavaObject>("getBuffer", 1);
                    unsafe
                    {
                        if (m_Buffer0Obj != null)
                            m_Buffer0Ptr = (IntPtr)AndroidJNI.GetDirectBufferAddress(m_Buffer0Obj.GetRawObject());
                        if (m_Buffer1Obj != null)
                            m_Buffer1Ptr = (IntPtr)AndroidJNI.GetDirectBufferAddress(m_Buffer1Obj.GetRawObject());
                    }
                    Debug.Log($"[PhoneApps] Opened virtual app window: {m_PackageName} (vertical)");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PhoneApps] Failed to start session for {m_PackageName}: {e.Message}");
                }
            }
#else
            Debug.Log($"[PhoneApps] Opened window (Editor preview): {m_PackageName} (vertical)");
#endif
        }

        void Update()
        {
            if (Mathf.Abs(m_PendingRotarySteps) > 0.01f && Time.unscaledTime >= m_NextRotarySwipeAt)
                FlushRotaryScroll();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (PauseCapture || m_Session == null || (m_Buffer0Ptr == IntPtr.Zero && m_Buffer1Ptr == IntPtr.Zero) || Time.unscaledTime < m_NextCapture)
                return;
            var count = m_WindowCountProvider != null ? m_WindowCountProvider() : 1;
            m_NextCapture = Time.unscaledTime + (m_IsFullscreenVr
                ? PhoneAppsRules.FullscreenCaptureInterval
                : PhoneAppsRules.CaptureIntervalForWindowCount(count));
            try
            {
                var packed = m_Session.Call<int>("acquireFrontBufferPacked");
                if (packed >= 0)
                {
                    var bufIdx = (packed >> 28) & 1;
                    var w = (packed >> 14) & 0x3FFF;
                    var h = packed & 0x3FFF;
                    var ptr = bufIdx == 1 ? m_Buffer1Ptr : m_Buffer0Ptr;
                    var bytes = w * h * 4;
                    if (ptr != IntPtr.Zero && w > 0 && h > 0 && bytes <= PhoneAppsRules.MaxBufferBytes)
                    {
                        if (!m_HasFirstFrame)
                        {
                            m_HasFirstFrame = true;
                            if (m_OverlayPill != null)
                                m_OverlayPill.SetActive(false);
                            Debug.Log($"[PhoneApps] First live frame received for {m_PackageName} ({w}x{h})");
                        }
                        if (w != m_TexWidth || h != m_TexHeight)
                            AllocateTextures(w, h);
                        m_Texture.LoadRawTextureData(ptr, bytes);
                        m_Texture.Apply(false);
                    }
                }
                else if (!m_HasFirstFrame && Time.unscaledTime >= m_NextDiagPoll)
                {
                    m_NextDiagPoll = Time.unscaledTime + 0.5f;
                    var displayId = m_Session.Call<int>("getDisplayId");
                    if (displayId >= 0 && displayId != m_LoggedDisplayId)
                    {
                        m_LoggedDisplayId = displayId;
                        Debug.Log($"[PhoneApps] VirtualDisplay ready for {m_PackageName}: displayId={displayId}");
                    }
                    var err = m_Session.Call<string>("getLastError") ?? "";
                    if (!string.IsNullOrEmpty(err) && err != m_LastLoggedError)
                    {
                        m_LastLoggedError = err;
                        Debug.LogWarning($"[PhoneApps] Session message ({m_PackageName}): {err}");
                        if (m_OverlayText != null)
                        {
                            m_OverlayText.color = M3Theme.Error;
                            m_OverlayText.text = err;
                            if (m_OverlayPill != null)
                                m_OverlayPill.SetActive(true);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PhoneApps] Capture failed ({m_PackageName}): {e.Message}");
            }
#endif
        }

        public void ToggleOrientation() => SetOrientation(!m_Landscape);

        public void SetOrientation(bool landscape)
        {
            m_OnFocus?.Invoke(this);
            if (m_Landscape == landscape)
                return;
            m_Landscape = landscape;
            ApplyOrientationLayout();
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("setLandscape", m_Landscape);
#else
            var dims = PhoneAppsRules.DisplaySize(m_Landscape);
            AllocateTextures(dims.x, dims.y);
#endif
            Debug.Log($"[PhoneApps] Rotated {m_PackageName} -> {(m_Landscape ? "Orizzontale" : "Verticale")}");
        }

        public void SendVrCardboardTriggerTap() => HandleSurfaceClick(PhoneAppsRules.VrFullscreenTapPoint);

        void OnVrFullscreenClicked()
        {
            m_OnFocus?.Invoke(this);
            m_OnToggleFullscreen?.Invoke(this);
        }

        public void PressBack()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("pressBack");
#endif
        }

        public void Relaunch()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("relaunchApp");
#endif
        }

        public void TypeText(string text, bool submit)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("typeText", text ?? "", submit);
#endif
        }

        void OnKeyboardClicked()
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("hideSystemKeyboard");
#endif
            m_OnToggleKeyboard?.Invoke();
        }

        public bool TryHandleWatchScroll(float rotarySteps)
        {
            if (Mathf.Abs(rotarySteps) < 1e-4f)
                return false;
            m_PendingRotarySteps += rotarySteps;
            if (Time.unscaledTime >= m_NextRotarySwipeAt)
                FlushRotaryScroll();
            return true;
        }

        void FlushRotaryScroll()
        {
            var steps = m_PendingRotarySteps;
            m_PendingRotarySteps = 0f;
            if (Mathf.Abs(steps) < 0.01f)
                return;
            var deltaNormY = Mathf.Clamp(steps * 0.24f, -0.58f, 0.58f);
            m_NextRotarySwipeAt = Time.unscaledTime + 0.14f;
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("scrollSwipe", deltaNormY);
#endif
        }

        public void HandleSurfaceClick(Vector2 normalized)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("clickNormalized", normalized.x, normalized.y);
#endif
        }

        public void TouchDown(float normU, float normV)
        {
            m_OnFocus?.Invoke(this);
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("touchDown", normU, normV);
#endif
        }

        public void TouchMove(float normU, float normV)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("touchMove", normU, normV);
#endif
        }

        public void TouchUp(float normU, float normV, bool cancel)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Session?.Call("touchUp", normU, normV, cancel);
#endif
        }

        public void PlaceAtYaw(Transform head, float yawDegrees)
        {
            if (head == null)
                return;
            WorldUi.PlaceInFront(m_Panel, head, m_Distance, 3f, yawDegrees);
        }

        public void DisposeSession()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Buffer0Ptr = IntPtr.Zero;
            m_Buffer1Ptr = IntPtr.Zero;
            if (m_Buffer0Obj != null)
            {
                try { m_Buffer0Obj.Dispose(); } catch (Exception) { }
                m_Buffer0Obj = null;
            }
            if (m_Buffer1Obj != null)
            {
                try { m_Buffer1Obj.Dispose(); } catch (Exception) { }
                m_Buffer1Obj = null;
            }
            if (m_Session != null)
            {
                try
                {
                    m_Session.Call("dispose");
                    m_Session.Dispose();
                }
                catch (Exception) { }
                m_Session = null;
            }
#endif
        }

        void OnDestroy()
        {
            DisposeSession();
            if (m_Texture != null)
                Destroy(m_Texture);
        }
    }

    // Receives gaze clicks (quick pinch / watch double-pinch / screen tap) and 1:1 continuous
    // touch dragging (for feeds like TikTok, web scrolling in Chrome, chats in WhatsApp, etc.).
    public class PhoneAppSurface : MonoBehaviour, IPointerClickHandler, IVisualScrollTarget
    {
        PhoneAppWindow m_Window;
        RectTransform m_Rect;

        public RectTransform ScrollRectTransform => m_Rect;
        public bool CanVisualScroll => m_Window != null && m_Rect != null;

        public void Initialize(PhoneAppWindow window, RectTransform rect)
        {
            m_Window = window;
            m_Rect = rect;
        }

        public void OnVisualTouchBegin(Vector2 localPoint)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = PhoneAppsRules.RectToNormalized(m_Rect.rect, localPoint);
            m_Window.TouchDown(n.x, n.y);
        }

        public void OnVisualTouchMove(Vector2 localPoint, Vector2 localDelta)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = PhoneAppsRules.RectToNormalized(m_Rect.rect, localPoint);
            m_Window.TouchMove(n.x, n.y);
        }

        public void OnVisualTouchEnd(Vector2 localPoint, bool cancelled)
        {
            if (m_Window == null || m_Rect == null)
                return;
            var n = PhoneAppsRules.RectToNormalized(m_Rect.rect, localPoint);
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
            var normalized = PhoneAppsRules.RectToNormalized(m_Rect.rect, local2D);
            m_Window.HandleSurfaceClick(normalized);
        }
    }
}
