using System;
using System.Collections.Generic;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.Hands
{
    // Apple visionOS-style bottom window chrome & external curved corner resize handle:
    //   - Left circular dot ("close"): closes the window/card.
    //   - Center translucent grab pill ("• ━━━━━"): 1 click grabs the window to move in 3D
    //     space (Watch gyro or head gaze + crown distance), 1 click anywhere releases it.
    //   - Right circular dot ("push_pin"): toggles room-anchored ARCore lock vs follow-me floating.
    //   - Bottom-right external curved arc ("╯"): 1 click starts drag-to-resize, 2nd click anywhere
    //     finishes resizing.
    public class FloatingWindowHandle : MonoBehaviour
    {
        const float k_MinWindowWidth = 180f;
        const float k_BarWidth = 268f;
        const float k_BarHeight = 44f;
        const float k_BarOffsetBelow = 30f;
        const float k_CornerSize = 54f;
        const float k_CornerOffsetOut = 10f;
        const float k_CornerArcRadius = 36f;
        const float k_CornerStrokeRest = 2.8f;
        const float k_CornerStrokeHover = 3.6f;
        const float k_CornerStrokeActive = 4.2f;
        const float k_RestingAlpha = 0f;
        const float k_GraceSeconds = 0.55f;
        const float k_FadeInSpeed = 9f;
        const float k_FadeOutSpeed = 5.5f;
        static readonly Vector2 k_PillRestSize = new(156f, 10f);
        static readonly Vector2 k_PillHoverSize = new(172f, 13f);
        static readonly Vector2 k_PillHeldSize = new(176f, 14f);

        static readonly HashSet<Transform> s_Excluded = new();
        static readonly List<FloatingWindowHandle> s_All = new();
        static Transform s_CurrentHead;
        static GazePointer s_CurrentGaze;

        sealed class HoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public bool Hovered { get; private set; }
            public void OnPointerEnter(PointerEventData eventData) => Hovered = true;
            public void OnPointerExit(PointerEventData eventData) => Hovered = false;
            void OnDisable() => Hovered = false;
        }

        RectTransform m_Panel;
        RectTransform m_Bar;
        CanvasGroup m_Group;
        RectTransform m_TopOrnament;
        CanvasGroup m_TopOrnamentGroup;
        Image m_MovePillImage;
        RectTransform m_MovePillRect;
        HoverRelay m_MoveHover;
        M3Button m_FloatButton;
        RectTransform m_ResizeCorner;
        CanvasGroup m_ResizeGroup;
        Image m_ResizeArcImage;
        VisionSurfaceEffect m_ResizeArcFx;
        HoverRelay m_ResizeHover;
        Action m_OnClose;
        bool m_IsSettingsPanel;
        bool m_RestoreOnRecenter = true;
        bool m_ClosedByHandle;
        float m_LastHoverTime = float.NegativeInfinity;
        float m_Alpha = k_RestingAlpha;
        float m_LastPanelWidth = -1f;
        float m_LastPanelHeight = -1f;

        public RectTransform Panel => m_Panel;
        public bool IsSettingsPanel => m_IsSettingsPanel;
        public bool RestoreOnRecenter => m_RestoreOnRecenter;
        public bool WasClosedByHandle => m_ClosedByHandle;

        public static IReadOnlyList<FloatingWindowHandle> All => s_All;

        public static void Exclude(Transform target)
        {
            s_Excluded.RemoveWhere(t => t == null);
            if (target != null)
                s_Excluded.Add(target);
        }

        public static bool IsExcluded(Transform target) =>
            target != null && s_Excluded.Contains(target);

        // Optional explicit configuration for windows with custom close logic (e.g. BrowserWindow, BrowserKeyboard, Settings).
        public static FloatingWindowHandle Configure(RectTransform panel, Action onClose,
            bool isSettingsPanel = false, bool restoreOnRecenter = true)
        {
            if (panel == null)
                return null;
            var handle = panel.GetComponent<FloatingWindowHandle>();
            if (handle == null)
                handle = panel.gameObject.AddComponent<FloatingWindowHandle>();
            handle.m_OnClose = onClose;
            handle.m_IsSettingsPanel = isSettingsPanel;
            handle.m_RestoreOnRecenter = restoreOnRecenter;
            handle.EnsureBuilt();
            return handle;
        }

        // Called once a frame from PinchInput to auto-attach handles to any active world-space window
        // and update their visibility and hover animation against the current gaze ray.
        public static void UpdateAll(GazePointer gaze)
        {
            if (gaze == null)
                return;
            s_CurrentHead = gaze.transform;
            s_CurrentGaze = gaze;

            // Attach automatically to any active GazeCanvas window that isn't excluded.
            var canvases = GazeCanvas.Active;
            for (var i = 0; i < canvases.Count; i++)
            {
                var canvas = canvases[i];
                if (canvas == null)
                    continue;
                var tr = canvas.transform;
                if (s_Excluded.Contains(tr) ||
                    SettingsSummoner.IsLookDownTarget(canvas.gameObject) ||
                    SettingsSummoner.IsLookUpTarget(canvas.gameObject) ||
                    SettingsSummoner.IsPalmButtonTarget(canvas.gameObject) ||
                    SettingsSummoner.IsOnPanel(canvas.gameObject))
                    continue;
                var rect = tr as RectTransform;
                if (rect == null || rect.rect.width < k_MinWindowWidth)
                    continue;
                if (canvas.GetComponent<FloatingWindowHandle>() == null)
                {
                    var h = canvas.gameObject.AddComponent<FloatingWindowHandle>();
                    h.EnsureBuilt();
                }
            }

            var ray = gaze.PointerRay;
            var now = Time.unscaledTime;
            var dt = Time.unscaledDeltaTime;
            for (var i = s_All.Count - 1; i >= 0; i--)
            {
                var h = s_All[i];
                if (h == null)
                {
                    s_All.RemoveAt(i);
                    continue;
                }
                if (!h.gameObject.activeInHierarchy)
                    continue;
                h.Tick(ray, now, dt);
            }
        }

        public static void RestoreClosedPanels()
        {
            for (var i = 0; i < s_All.Count; i++)
            {
                var h = s_All[i];
                if (h == null || !h.m_ClosedByHandle || !h.m_RestoreOnRecenter)
                    continue;
                h.m_ClosedByHandle = false;
                M3Fade.Show(h.gameObject);
            }
        }

        void Awake()
        {
            if (!s_All.Contains(this))
                s_All.Add(this);
        }

        void OnDestroy()
        {
            s_All.Remove(this);
        }

        void EnsureBuilt()
        {
            if (m_Bar != null)
                return;
            m_Panel = transform as RectTransform;
            if (m_Panel == null)
                return;

            // Apple visionOS detached bottom chrome: circular close dot (•) + sleek translucent grab pill (━━━━━) + pin dot.
            m_Bar = M3.Group("Window Floating Handle", m_Panel, Vector2.zero, new Vector2(k_BarWidth, k_BarHeight));
            m_Group = m_Bar.gameObject.AddComponent<CanvasGroup>();
            SetChromeGroup(m_Group, k_RestingAlpha, false);

            M3.IconButton("Close Window", m_Bar, "close", M3Button.Variant.Tonal,
                new Vector2(-114f, 0f), OnCloseClicked, 34f);

            var moveHit = M3.Surface("Move Window", m_Bar, Vector2.zero, new Vector2(168f, 44f),
                Color.clear, M3Theme.k_ShapeFull, raycastTarget: true, style: VisionSurfaceStyle.Pure);
            var moveBtn = moveHit.gameObject.AddComponent<Button>();
            moveBtn.transition = Selectable.Transition.None;
            moveBtn.targetGraphic = moveHit;
            moveBtn.onClick.AddListener(OnMoveClicked);
            m_MoveHover = moveHit.gameObject.AddComponent<HoverRelay>();

            m_MovePillImage = M3.Surface("Grab Pill", moveHit.rectTransform, Vector2.zero, k_PillRestSize,
                M3Theme.GlassRestingBar, M3Theme.k_ShapeFull, raycastTarget: false,
                style: VisionSurfaceStyle.Glossy, rimStrength: 0.90f, sheenStrength: 0.95f);
            m_MovePillRect = m_MovePillImage.rectTransform;

            m_FloatButton = M3.IconButton("Toggle Floating", m_Bar, "push_pin", M3Button.Variant.Tonal,
                new Vector2(114f, 0f), OnFloatToggleClicked, 34f);

            // Apple visionOS bottom-right external curved corner arc ("╯") for 1-click toggle drag-to-resize.
            m_ResizeCorner = M3.Group("Window Resize Corner", m_Panel, Vector2.zero, new Vector2(k_CornerSize, k_CornerSize));
            m_ResizeGroup = m_ResizeCorner.gameObject.AddComponent<CanvasGroup>();
            SetChromeGroup(m_ResizeGroup, k_RestingAlpha, false);

            var resizeHit = M3.Surface("Resize Hit", m_ResizeCorner, Vector2.zero, new Vector2(k_CornerSize, k_CornerSize),
                Color.clear, M3Theme.k_ShapeFull, raycastTarget: true, style: VisionSurfaceStyle.Pure);
            var resizeBtn = resizeHit.gameObject.AddComponent<Button>();
            resizeBtn.transition = Selectable.Transition.None;
            resizeBtn.targetGraphic = resizeHit;
            resizeBtn.onClick.AddListener(OnResizeClicked);
            m_ResizeHover = resizeHit.gameObject.AddComponent<HoverRelay>();

            m_ResizeArcImage = M3.Surface("Corner Arc", m_ResizeCorner, Vector2.zero, new Vector2(k_CornerSize, k_CornerSize),
                M3Theme.GlassRestingBar, k_CornerArcRadius, raycastTarget: false, style: VisionSurfaceStyle.CornerArc,
                rimStrength: k_CornerStrokeRest, sheenStrength: 0.9f);
            m_ResizeArcFx = m_ResizeArcImage.GetComponent<VisionSurfaceEffect>();

            UpdateBarPosition();
            EnsureTopOrnament();
        }

        void EnsureTopOrnament()
        {
            if (m_TopOrnament != null || m_Panel == null)
                return;
            var found = m_Panel.Find("Top Ornament") as RectTransform;
            if (found == null)
                return;
            m_TopOrnament = found;
            m_TopOrnamentGroup = found.GetComponent<CanvasGroup>();
            if (m_TopOrnamentGroup == null)
                m_TopOrnamentGroup = found.gameObject.AddComponent<CanvasGroup>();
            SetChromeGroup(m_TopOrnamentGroup, m_Alpha, m_Alpha > 0.15f);
        }

        public static float ComputeTargetChromeAlpha(bool isHeld, bool isResizing, bool isGazed, float secondsSinceLastGaze) =>
            isHeld || isResizing || isGazed || secondsSinceLastGaze <= k_GraceSeconds ? 1f : k_RestingAlpha;

        void UpdateBarPosition()
        {
            if (m_Panel == null || m_Bar == null)
                return;
            var r = m_Panel.rect;
            if (Mathf.Approximately(r.width, m_LastPanelWidth) && Mathf.Approximately(r.height, m_LastPanelHeight))
                return;
            m_LastPanelWidth = r.width;
            m_LastPanelHeight = r.height;
            m_Bar.anchoredPosition = new Vector2(0f, -r.height * 0.5f - k_BarOffsetBelow);
            if (m_ResizeCorner != null)
                m_ResizeCorner.anchoredPosition = new Vector2(r.width * 0.5f + k_CornerOffsetOut, -r.height * 0.5f - k_CornerOffsetOut);
        }

        void Tick(Ray ray, float now, float dt)
        {
            EnsureBuilt();
            if (m_Bar == null || m_Panel == null)
                return;
            UpdateBarPosition();
            EnsureTopOrnament();

            var canvasDisabled = m_Panel.TryGetComponent<Canvas>(out var canvas) && !canvas.enabled;
            if (canvasDisabled || SpatialWindowAnchor.IsStowed(m_Panel))
            {
                m_Alpha = 0f;
                SetChromeGroup(m_Group, 0f, false);
                SetChromeGroup(m_ResizeGroup, 0f, false);
                SetChromeGroup(m_TopOrnamentGroup, 0f, false);
                return;
            }

            var isHeld = m_IsSettingsPanel
                ? WatchGrabController.IsHoldingSettingsPanel
                : WatchGrabController.IsHolding(m_Panel);
            var isResizing = WatchGrabController.IsResizingPanel(m_Panel);

            if (m_FloatButton != null)
                m_FloatButton.Selected = SpatialWindowAnchor.IsFloating(m_Panel);

            var moveHovered = m_MoveHover != null && m_MoveHover.Hovered;
            var resizeHovered = m_ResizeHover != null && m_ResizeHover.Hovered;
            var isGazed = moveHovered || resizeHovered || IsGazeInRevealZone(ray);
            if (isHeld || isResizing || isGazed)
                m_LastHoverTime = now;

            var t = 1f - Mathf.Exp(-dt * 14f);
            if (m_MovePillImage != null && m_MovePillRect != null)
            {
                var targetSize = isHeld ? k_PillHeldSize : (moveHovered ? k_PillHoverSize : k_PillRestSize);
                var targetColor = isHeld ? M3Theme.GlassAccent : (moveHovered ? Color.white : M3Theme.GlassRestingBar);
                m_MovePillRect.sizeDelta = Vector2.Lerp(m_MovePillRect.sizeDelta, targetSize, t);
                m_MovePillImage.color = Color.Lerp(m_MovePillImage.color, targetColor, t);
            }

            if (m_ResizeArcImage != null)
            {
                var targetColor = isResizing ? M3Theme.GlassAccent : (resizeHovered ? Color.white : M3Theme.GlassRestingBar);
                m_ResizeArcImage.color = Color.Lerp(m_ResizeArcImage.color, targetColor, t);
                if (m_ResizeArcFx != null)
                {
                    var targetStroke = isResizing ? k_CornerStrokeActive : (resizeHovered ? k_CornerStrokeHover : k_CornerStrokeRest);
                    m_ResizeArcFx.RimStrength = Mathf.Lerp(m_ResizeArcFx.RimStrength, targetStroke, t);
                }
            }

            var targetAlpha = ComputeTargetChromeAlpha(isHeld, isResizing, isGazed, now - m_LastHoverTime);
            var fadeSpeed = targetAlpha > m_Alpha ? k_FadeInSpeed : k_FadeOutSpeed;
            m_Alpha = Mathf.MoveTowards(m_Alpha, targetAlpha, dt * fadeSpeed);
            var active = m_Alpha > 0.15f;
            SetChromeGroup(m_Group, m_Alpha, active);
            SetChromeGroup(m_ResizeGroup, m_Alpha, active);
            SetChromeGroup(m_TopOrnamentGroup, m_Alpha, active);
        }

        static void SetChromeGroup(CanvasGroup group, float alpha, bool interactive)
        {
            if (group == null)
                return;
            group.alpha = alpha;
            group.blocksRaycasts = interactive;
            group.interactable = interactive;
        }

        bool IsGazeInRevealZone(Ray ray)
        {
            if (s_CurrentGaze != null && s_CurrentGaze.Target != null && s_CurrentGaze.Target.transform.IsChildOf(m_Panel))
                return true;

            if (Vector3.Dot(ray.direction, m_Panel.forward) <= 0.05f)
                return false;
            var plane = new Plane(m_Panel.forward, m_Panel.position);
            if (!plane.Raycast(ray, out var dist) || dist <= 0.1f || dist > 10f)
                return false;

            if (s_CurrentGaze != null && s_CurrentGaze.Target != null &&
                !s_CurrentGaze.Target.transform.IsChildOf(m_Panel) &&
                s_CurrentGaze.TargetDistance < dist - 0.05f)
            {
                return false;
            }

            var local = (Vector2)m_Panel.InverseTransformPoint(ray.GetPoint(dist));
            var r = m_Panel.rect;
            var halfW = r.width * 0.5f;
            var halfH = r.height * 0.5f;

            var topWidthHalf = m_TopOrnament != null && m_TopOrnament.gameObject.activeSelf
                ? m_TopOrnament.rect.width * 0.5f + 16f
                : 0f;
            var reachX = Mathf.Max(halfW + 38f, Mathf.Max(k_BarWidth * 0.5f + 22f, topWidthHalf));
            if (Mathf.Abs(local.x) > reachX)
                return false;

            var minY = -halfH - k_BarOffsetBelow - k_BarHeight * 0.5f - 22f;
            var topExtra = 24f;
            if (m_TopOrnament != null && m_TopOrnament.gameObject.activeSelf)
            {
                var ornamentTop = m_TopOrnament.anchoredPosition.y + m_TopOrnament.rect.height * 0.5f;
                topExtra = Mathf.Max(topExtra, (ornamentTop - halfH) + 22f);
            }
            var maxY = halfH + topExtra;

            return local.y >= minY && local.y <= maxY;
        }

        void OnCloseClicked()
        {
            if (WatchGrabController.IsResizingPanel(m_Panel) ||
                (m_IsSettingsPanel ? WatchGrabController.IsHoldingSettingsPanel : WatchGrabController.IsHolding(m_Panel)))
            {
                WatchGrabController.Release();
            }

            if (m_OnClose != null)
            {
                M3Fade.Hide(gameObject, m_OnClose);
                return;
            }

            m_ClosedByHandle = true;
            M3Fade.Hide(gameObject);
            Debug.Log($"[UI] Closed floating card {name}");
        }

        void OnMoveClicked()
        {
            if (s_CurrentHead == null)
                return;
            WatchGrabController.ToggleGrabPanel(m_Panel, s_CurrentHead, m_IsSettingsPanel);
        }

        void OnResizeClicked()
        {
            if (s_CurrentHead == null)
                return;
            WatchGrabController.ToggleResizePanel(m_Panel, s_CurrentHead,
                s_CurrentGaze != null ? s_CurrentGaze.PointerRay : (Ray?)null);
        }

        void OnFloatToggleClicked()
        {
            if (m_Panel == null || m_IsSettingsPanel)
                return;
            var isFloating = SpatialWindowAnchor.ToggleFloating(m_Panel, s_CurrentHead);
            if (m_FloatButton != null)
                m_FloatButton.Selected = isFloating;
        }
    }
}
