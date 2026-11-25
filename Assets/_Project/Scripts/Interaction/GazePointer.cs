using System.Collections.Generic;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CardboardXR.Interaction
{
    // Cardboard's gaze input. A ray from the middle of the head picks what the user looks at, and a
    // screen tap, which is what a viewer's button does, clicks it. Targets get the standard UI pointer
    // events (enter, exit, down, up and click), so Buttons on world-space canvases with a GazeCanvas
    // work, and so do colliders with handlers such as GazeHighlight. A tap that lands on screen-space
    // UI goes to that UI instead. Holding the tap invokes OnHold instead of clicking; the calibration
    // scene recenters with it. Real surfaces, such as detected planes, don't hide world-space UI or
    // clickable objects behind them: in VR those are seen and the surfaces aren't, and in passthrough
    // they draw over the surfaces. Other virtual things behind a real surface, like a virtual room's
    // walls, stay hidden, so the surface can still be pointed at. Another ray, such as a tracked
    // hand's, can point instead of the head (RayOverride).
    public class GazePointer : MonoBehaviour
    {
        [SerializeField] GazeReticle m_Reticle;
        [Tooltip("Invoked when the screen is held instead of tapped.")]
        [SerializeField] UnityEvent m_OnHold = new();
        [SerializeField] float m_MaxDistance = 20f;
        [SerializeField] LayerMask m_PhysicsMask = Physics.DefaultRaycastLayers;
        [Tooltip("Layers of real surfaces, which don't hide world-space UI or clickable objects behind them.")]
        [SerializeField] LayerMask m_RealSurfaceMask;
        [SerializeField] float m_HoldSeconds = 1f;

        static readonly List<RaycastResult> s_ScreenHits = new();
        static readonly RaycastHit[] s_Hits = new RaycastHit[16];

        PointerEventData m_EventData;
        PointerEventData m_ScreenEventData;
        GameObject m_Target;
        GameObject m_Pressed;
        bool m_Held;
        bool m_ClickCancelled;
        bool m_TapOnScreenUi;
        float m_PressTime;
        Vector3 m_PressedLocalHit;
        bool m_HasPressedLocalHit;

        public UnityEvent OnHold => m_OnHold;

        public LayerMask RealSurfaceMask
        {
            get => m_RealSurfaceMask;
            set => m_RealSurfaceMask = value;
        }

        // What the gaze is on: a collider's GameObject or a Graphic's, or null.
        public GameObject Target => m_Target;

        // The distance to the target along the ray, or the maximum distance when there's none.
        public float TargetDistance { get; private set; }


        public GazeReticle Reticle => m_Reticle;

        // Optional interceptor checked at the start of TriggerDown: return true to swallow the tap/click
        // (used by WatchGrabController so a single tap releases a grabbed window without clicking underneath).
        public static System.Func<bool> TriggerInterceptor { get; set; }

        // Optional angular offset from the center of the head view, in degrees (x = yaw +right, y = pitch +up),
        // steered by the Pixel Watch's gyroscope while connected. (0, 0) keeps the cursor centered.
        public Vector2 CursorAnglesDeg { get; set; }

        // Forces the gaze reticle & ray strictly to center forward (e.g. during VR SBS fullscreen mode).
        public static bool LockCursorToCenter { get; set; }

        // When true, skips physics colliders and spatial surfaces so only allowed overlay GazeCanvases interact.
        public static bool OverlayCanvasesOnly { get; set; }

        // Invoked on a gaze trigger click that landed on no UI target (used by VR SBS fullscreen for Cardboard taps).
        public static System.Action OnEmptyTriggerClick { get; set; }

        public Vector3 CursorLocalDirection =>
            !LockCursorToCenter && CursorAnglesDeg.sqrMagnitude > 1e-6f
                ? Quaternion.Euler(-CursorAnglesDeg.y, CursorAnglesDeg.x, 0f) * Vector3.forward
                : Vector3.forward;

        public Ray PointerRay =>
            RayOverride ?? new Ray(transform.position, transform.TransformDirection(CursorLocalDirection));

        // A ray to point with instead of the head's forward line.
        public Ray? RayOverride { get; set; }

        // Optional spatial surface provider override (for tests); defaults to SpatialSurfaceLocator.Active.
        public ISpatialSurfaceProvider SpatialSurfaceOverride { get; set; }

        ISpatialSurfaceProvider ActiveSpatialProvider => SpatialSurfaceOverride ?? SpatialSurfaceLocator.Active;

        PointerEventData EventData =>
            m_EventData ??= new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };

        void OnDisable()
        {
            Release(click: false);
            SetTarget(null, default);
            SpatialWindowAnchor.ClearActiveGaze();
        }

        void Update()
        {
            UpdateGaze();
            UpdateTrigger();
        }

        // Finds what the pointer aims at, sends enter and exit events when that changes, and moves the
        // reticle onto it.
        public void UpdateGaze()
        {
            var localDir = CursorLocalDirection;
            var ray = RayOverride ?? new Ray(transform.position, transform.TransformDirection(localDir));
            var found = Raycast(ray, out var result);
            SetTarget(found ? result.gameObject : null, result);
            SpatialWindowAnchor.SetActiveGaze(ray, m_Target);
            TargetDistance = found ? result.distance : m_MaxDistance;
            if (m_Reticle == null)
                return;
            // The reticle follows the gaze/watch ray, and hides while a 6DoF hand ray overrides it.
            var showReticle = !RayOverride.HasValue;
            if (m_Reticle.gameObject.activeSelf != showReticle)
                m_Reticle.gameObject.SetActive(showReticle);
            m_Reticle.SetTarget(TargetDistance, IsInteractive(m_Target), localDir);
        }

        // Presses the target, as the viewer's button touching the screen does.
        public void TriggerDown()
        {
            if (m_Held)
                return;
            if (TriggerInterceptor != null && TriggerInterceptor())
                return;

            m_Held = true;
            m_ClickCancelled = false;
            m_PressTime = Time.unscaledTime;
            var eventData = EventData;
            eventData.eligibleForClick = true;
            eventData.pointerPressRaycast = eventData.pointerCurrentRaycast;
            eventData.rawPointerPress = m_Target;
            // Like the UI input modules: the press goes to the nearest down handler up the hierarchy,
            // or else to the click handler.
            m_Pressed = null;
            m_HasPressedLocalHit = false;
            if (m_Target != null)
            {
                m_Pressed = ExecuteEvents.ExecuteHierarchy(m_Target, eventData, ExecuteEvents.pointerDownHandler);
                if (m_Pressed == null)
                    m_Pressed = ExecuteEvents.GetEventHandler<IPointerClickHandler>(m_Target);
                if (m_Pressed != null)
                {
                    m_PressedLocalHit = m_Pressed.transform.InverseTransformPoint(eventData.pointerPressRaycast.worldPosition);
                    m_HasPressedLocalHit = true;
                }
            }
            eventData.pointerPress = m_Pressed;
            Debug.Log($"[Gaze] Pressed {(m_Target != null ? m_Target.name : "nothing")} {TargetDistance:F2} m away");
        }

        const float k_QuickTapGraceSeconds = 0.35f;
        const float k_QuickTapGraceMaxAngleDeg = 3.5f;

        // Releases the press, and clicks if the gaze is still on what was pressed (or drifted slightly
        // onto empty space between buttons during a quick tap).
        public void TriggerUp() => Release(click: true);

        bool IsWithinQuickTapGrace(GameObject pressed)
        {
            if (pressed == null || !pressed.activeInHierarchy)
                return false;
            if (Time.unscaledTime - m_PressTime > k_QuickTapGraceSeconds)
                return false;
            if (!m_HasPressedLocalHit)
                return false;
            var ray = PointerRay;
            var worldHit = pressed.transform.TransformPoint(m_PressedLocalHit);
            var toHit = worldHit - ray.origin;
            if (toHit.sqrMagnitude <= 1e-6f)
                return true;
            return Vector3.Angle(ray.direction, toHit) <= k_QuickTapGraceMaxAngleDeg;
        }

        void Release(bool click)
        {
            if (!m_Held)
                return;

            m_Held = false;
            var eventData = EventData;
            if (m_Pressed != null)
                ExecuteEvents.Execute(m_Pressed, eventData, ExecuteEvents.pointerUpHandler);

            var clickHandler = m_Target != null ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(m_Target) : null;
            if (click && !m_ClickCancelled && eventData.eligibleForClick && m_Pressed != null)
            {
                var targetClick = m_Pressed == clickHandler
                    ? clickHandler
                    : (clickHandler == null && IsWithinQuickTapGrace(m_Pressed)
                        ? ExecuteEvents.GetEventHandler<IPointerClickHandler>(m_Pressed)
                        : null);
                if (targetClick != null)
                {
                    ExecuteEvents.Execute(targetClick, eventData, ExecuteEvents.pointerClickHandler);
                    Debug.Log($"[Gaze] Clicked {targetClick.name}");
                }
            }
            else if (click && !m_ClickCancelled && eventData.eligibleForClick &&
                     m_Pressed == null && m_Target == null && OnEmptyTriggerClick != null)
            {
                OnEmptyTriggerClick.Invoke();
            }

            eventData.eligibleForClick = false;
            eventData.pointerPress = null;
            eventData.rawPointerPress = null;
            m_Pressed = null;
            m_HasPressedLocalHit = false;
            // Pressing a Button selects it, which would keep it tinted after the gaze moves on.
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        void UpdateTrigger()
        {
            var pointer = Pointer.current;
            if (pointer == null)
                return;

            if (pointer.press.wasPressedThisFrame)
            {
                m_TapOnScreenUi = IsOverScreenUi(pointer.position.ReadValue());
                if (!m_TapOnScreenUi)
                    TriggerDown();
            }

            if (m_Held && !m_ClickCancelled && Time.unscaledTime - m_PressTime >= m_HoldSeconds)
            {
                // A hold replaces the click.
                m_ClickCancelled = true;
                m_OnHold.Invoke();
                Debug.Log("[Gaze] Held");
            }

            if (pointer.press.wasReleasedThisFrame && !m_TapOnScreenUi)
                TriggerUp();
        }

        // The nearest collider or GazeCanvas graphic along the ray, except that a real surface doesn't
        // hide a graphic or clickable object behind it.
        bool Raycast(Ray ray, out RaycastResult result)
        {
            result = default;
            // How far away a graphic can be and still be hit.
            var canvasReach = m_MaxDistance;
            var hitWasRealSurface = false;
            if (!OverlayCanvasesOnly && PhysicsRaycast(ray, out var hit))
            {
                hitWasRealSurface = IsRealSurface(hit.collider);
                if (!hitWasRealSurface)
                    canvasReach = hit.distance;
                result = new RaycastResult
                {
                    gameObject = hit.collider.gameObject,
                    distance = hit.distance,
                    worldPosition = hit.point,
                    worldNormal = hit.normal,
                };
            }

            var canvases = GazeCanvas.Active;
            for (var i = 0; i < canvases.Count; i++)
            {
                if (!canvases[i].Raycast(ray, canvasReach, out var graphic, out var distance))
                    continue;
                canvasReach = distance;
                hitWasRealSurface = false;
                result = new RaycastResult
                {
                    gameObject = graphic.gameObject,
                    distance = distance,
                    worldPosition = ray.GetPoint(distance),
                    worldNormal = -graphic.transform.forward,
                };
            }

            var spatial = OverlayCanvasesOnly ? null : ActiveSpatialProvider;
            if (spatial != null && spatial.IsPlacementActive)
            {
                if (result.gameObject == null || hitWasRealSurface)
                {
                    if (spatial.TryRaycast(ray, out var surfaceTarget, out var point, out var normal, out var distance))
                    {
                        result = new RaycastResult
                        {
                            gameObject = surfaceTarget,
                            distance = distance,
                            worldPosition = point,
                            worldNormal = normal,
                        };
                        spatial.NotifyHitResult(true);
                    }
                    else
                    {
                        spatial.NotifyHitResult(false);
                        if (hitWasRealSurface)
                            result = default;
                    }
                }
                else
                {
                    spatial.NotifyHitResult(false);
                }
            }

            return result.gameObject != null;
        }

        // The nearest collider along the ray, or, if that's a real surface, the nearest clickable
        // virtual object behind it if there is one.
        bool PhysicsRaycast(Ray ray, out RaycastHit hit)
        {
            hit = default;
            var count = Physics.RaycastNonAlloc(ray, s_Hits, m_MaxDistance, m_PhysicsMask, QueryTriggerInteraction.Ignore);
            var nearest = -1;
            var nearestClickable = -1;
            for (var i = 0; i < count; i++)
            {
                if (nearest < 0 || Nearer(s_Hits[i], s_Hits[nearest]))
                    nearest = i;
                if (!IsRealSurface(s_Hits[i].collider) && IsInteractive(s_Hits[i].collider.gameObject) &&
                    (nearestClickable < 0 || s_Hits[i].distance < s_Hits[nearestClickable].distance))
                    nearestClickable = i;
            }
            if (nearest < 0)
                return false;

            hit = s_Hits[IsRealSurface(s_Hits[nearest].collider) && nearestClickable >= 0 ? nearestClickable : nearest];
            return true;
        }

        bool IsRealSurface(Collider collider) => (m_RealSurfaceMask.value & (1 << collider.gameObject.layer)) != 0;

        // Hits this close along the ray, in meters, count as level, and a real surface wins among
        // them. The virtual room's floor is lined up with the detected one, so the two are coplanar,
        // and which was nearer changed from frame to frame on rounding alone: every change sent exit
        // and enter events, and the floor's highlight flickered. Centimeters, not millimeters, since
        // at a shallow angle a few millimeters
        // between the floors becomes centimeters along the ray.
        const float k_LevelDistance = 0.05f;

        bool Nearer(RaycastHit a, RaycastHit b)
        {
            if (Mathf.Abs(a.distance - b.distance) <= k_LevelDistance && IsRealSurface(a.collider) != IsRealSurface(b.collider))
                return IsRealSurface(a.collider);
            return a.distance < b.distance;
        }

        // Like BaseInputModule.HandlePointerExitAndEnter: objects the gaze left get exit events and
        // the new ones get enter events, up to the ancestor they share.
        void SetTarget(GameObject target, RaycastResult result)
        {
            var eventData = EventData;
            eventData.pointerCurrentRaycast = result;
            if (target == m_Target)
                return;

            var shared = SharedAncestor(m_Target, target);
            for (var t = m_Target != null ? m_Target.transform : null; t != null && t != shared; t = t.parent)
                ExecuteEvents.Execute(t.gameObject, eventData, ExecuteEvents.pointerExitHandler);

            m_Target = target;
            eventData.pointerEnter = target;
            for (var t = target != null ? target.transform : null; t != null && t != shared; t = t.parent)
                ExecuteEvents.Execute(t.gameObject, eventData, ExecuteEvents.pointerEnterHandler);
        }

        static Transform SharedAncestor(GameObject a, GameObject b)
        {
            if (a == null || b == null)
                return null;
            for (var t = a.transform; t != null; t = t.parent)
            {
                if (b.transform.IsChildOf(t))
                    return t;
            }
            return null;
        }

        // Whether clicking the target would do something, which grows the reticle into a ring.
        static bool IsInteractive(GameObject target)
        {
            if (target == null)
                return false;
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            if (handler == null)
                return false;
            return !handler.TryGetComponent<Selectable>(out var selectable) || selectable.IsInteractable();
        }

        // Screen-space UI, such as the viewer setup button, takes taps before the gaze does.
        bool IsOverScreenUi(Vector2 position)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null)
                return false;
            m_ScreenEventData ??= new PointerEventData(eventSystem);
            m_ScreenEventData.position = position;
            // World-space canvases have no GraphicRaycaster, so only screen-space UI can be hit.
            eventSystem.RaycastAll(m_ScreenEventData, s_ScreenHits);
            return s_ScreenHits.Count > 0;
        }
    }
}
