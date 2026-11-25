using CardboardXR.Interaction;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CardboardXR.Hands
{
    // Implemented by world-space UI panels or scrollable surfaces (such as Web Cinema's BrowserSurface,
    // AppLibrary, and VideoDemo) that support direct 1:1 touch-style dragging via visual pinch.
    public interface IVisualScrollTarget
    {
        RectTransform ScrollRectTransform { get; }
        bool CanVisualScroll { get; }
        void OnVisualTouchBegin(Vector2 localPoint);
        void OnVisualTouchMove(Vector2 localPoint, Vector2 localDelta);
        void OnVisualTouchEnd(Vector2 localPoint, bool cancelled);
    }

    // Tracks continuous visual pinches (camera 3DoF/Ultrawide or 6DoF HandTracker) as direct 1:1
    // touchscreen manipulation. The gaze picks the surface; the hand's motion, angular and scaled
    // by k_DragGain, drags it from anywhere in the camera's view; the drag lasts until release,
    // while quick stationary pinches fire a normal tap click on release.
    public static class VisualPinchScroller
    {
        const float k_MinTouchSlopDp = 38f;
        const float k_MaxTouchSlopDp = 56f;
        const float k_TouchSlopWidthFrac = 0.075f;
        const float k_DragGain = 1.5f;
        const float k_MomentumDecay = 6.5f;
        const float k_MinMomentumSpeed = 35f;
        const float k_MaxMomentumSpeed = 2400f;
        const float k_MaxRayDistance = 20f;

        static bool s_Active;
        static bool s_Dragging;
        static GazePointer s_Gaze;
        static GameObject s_StartTarget;
        static Vector3 s_StartWorldPoint;
        static float s_StartTime;
        static float s_TouchSlopDp = k_MinTouchSlopDp;
        static IVisualScrollTarget s_ScrollTarget;
        static ScrollRect s_ScrollRect;
        static RectTransform s_SurfaceRect;

        static Ray s_PinchRay;
        static bool s_HasPinchRay;
        static Ray s_StartAimRay;
        static Vector3 s_StartPinchDir;
        static Vector2 s_AnchorLocal;
        static Vector2 s_LastLocal;
        static Vector2 s_LocalVelocity;
        static Vector2 s_LocalMomentum;

        public static bool IsActive => s_Active;
        public static bool IsDragging => s_Dragging;
        public static bool IsPinchHeld { get; private set; }

        public static void SyncPinchRay(Ray ray)
        {
            s_PinchRay = ray;
            s_HasPinchRay = true;
        }

        public static bool TryRaycastScrollable(Ray pinchRay, out Graphic hitGraphic,
            out IVisualScrollTarget customTarget, out ScrollRect scrollRect,
            out RectTransform surfaceRect, out Vector2 localPoint)
        {
            hitGraphic = null;
            customTarget = null;
            scrollRect = null;
            surfaceRect = null;
            localPoint = Vector2.zero;

            var canvases = GazeCanvas.Active;
            var bestDist = k_MaxRayDistance;
            for (var i = 0; i < canvases.Count; i++)
            {
                var c = canvases[i];
                if (c == null)
                    continue;
                if (!c.Raycast(pinchRay, bestDist, out var graphic, out var dist))
                    continue;
                bestDist = dist;
                hitGraphic = graphic;
            }
            if (hitGraphic == null)
                return false;

            var custom = hitGraphic.GetComponentInParent<IVisualScrollTarget>();
            if (custom != null && (!custom.CanVisualScroll || custom.ScrollRectTransform == null))
                custom = null;
            var rect = custom == null ? hitGraphic.GetComponentInParent<ScrollRect>() : null;
            if (custom == null && rect == null)
                return false;

            var targetRect = custom != null
                ? custom.ScrollRectTransform
                : (rect.viewport != null ? rect.viewport : rect.transform as RectTransform);
            if (targetRect == null)
                return false;

            if (!TryProjectOntoRect(pinchRay, targetRect, out localPoint))
                return false;
            if (!targetRect.rect.Contains(localPoint))
                return false;

            customTarget = custom;
            scrollRect = rect;
            surfaceRect = targetRect;
            return true;
        }

        static bool TryFindGazeSurface(Ray gazeRay, GameObject gazeTarget,
            out IVisualScrollTarget customTarget, out ScrollRect scrollRect,
            out RectTransform surfaceRect, out Vector2 localPoint)
        {
            customTarget = null;
            scrollRect = null;
            surfaceRect = null;
            localPoint = Vector2.zero;
            if (gazeTarget == null)
                return false;

            var custom = gazeTarget.GetComponentInParent<IVisualScrollTarget>();
            if (custom != null && (!custom.CanVisualScroll || custom.ScrollRectTransform == null))
                custom = null;
            var rect = custom == null ? gazeTarget.GetComponentInParent<ScrollRect>() : null;
            if (custom == null && rect == null)
                return false;

            var targetRect = custom != null
                ? custom.ScrollRectTransform
                : (rect.viewport != null ? rect.viewport : rect.transform as RectTransform);
            if (targetRect == null || !TryProjectOntoRect(gazeRay, targetRect, out localPoint))
                return false;

            customTarget = custom;
            scrollRect = rect;
            surfaceRect = targetRect;
            return true;
        }

        static Ray DragRay(Ray pinchRay)
        {
            var turn = Quaternion.SlerpUnclamped(
                Quaternion.identity,
                Quaternion.FromToRotation(s_StartPinchDir, pinchRay.direction),
                k_DragGain);
            return new Ray(s_StartAimRay.origin, turn * s_StartAimRay.direction);
        }

        public static bool TryBegin(PinchSource source, GazePointer gaze, GameObject gazeTarget, bool gazeIsButton = false)
        {
            if (source == PinchSource.Watch || gaze == null || !s_HasPinchRay)
                return false;

            if (gazeIsButton && gazeTarget != null)
            {
                var m3Button = gazeTarget.GetComponentInParent<M3Button>();
                if (m3Button != null && m3Button.ButtonVariant != M3Button.Variant.ListItem)
                    return false;
            }

            Ray aim;
            if (TryFindGazeSurface(gaze.PointerRay, gazeTarget, out var custom, out var rect, out var surfaceRect, out var localPoint))
            {
                aim = gaze.PointerRay;
            }
            else if (TryRaycastScrollable(s_PinchRay, out _, out custom, out rect, out surfaceRect, out localPoint) &&
                     (gazeTarget == null || gazeTarget.transform.IsChildOf(surfaceRect) || surfaceRect.IsChildOf(gazeTarget.transform)))
            {
                aim = s_PinchRay;
            }
            else
            {
                return false;
            }

            s_Active = true;
            s_Dragging = false;
            s_Gaze = gaze;
            s_StartTarget = gazeTarget;
            s_StartWorldPoint = gaze.PointerRay.GetPoint(gaze.TargetDistance);
            s_StartTime = Time.unscaledTime;
            s_TouchSlopDp = Mathf.Clamp(surfaceRect.rect.width * k_TouchSlopWidthFrac, k_MinTouchSlopDp, k_MaxTouchSlopDp);
            s_ScrollTarget = custom;
            s_ScrollRect = rect;
            s_SurfaceRect = surfaceRect;
            s_StartAimRay = aim;
            s_StartPinchDir = s_PinchRay.direction;
            s_AnchorLocal = localPoint;
            s_LastLocal = localPoint;
            s_LocalVelocity = Vector2.zero;
            s_LocalMomentum = Vector2.zero;
            return true;
        }

        public static void UpdateHand(Ray pinchRay, bool hasPinchRay, bool pinchHeld, float dt)
        {
            if (hasPinchRay)
            {
                s_PinchRay = pinchRay;
                s_HasPinchRay = true;
            }
            else if (!pinchHeld)
            {
                s_HasPinchRay = false;
            }
            IsPinchHeld = pinchHeld;

            if (s_Active)
            {
                if (!pinchHeld)
                {
                    End();
                    return;
                }
                if (s_SurfaceRect == null || !s_SurfaceRect.gameObject.activeInHierarchy)
                {
                    End(cancelled: true, suppressClick: true);
                    return;
                }
                if (!hasPinchRay)
                    return;

                if (!TryProjectOntoRect(DragRay(pinchRay), s_SurfaceRect, out var local2D))
                    return;

                if (!s_Dragging)
                {
                    if (Vector2.Distance(local2D, s_AnchorLocal) >= s_TouchSlopDp)
                    {
                        s_Dragging = true;
                        var initialDelta = local2D - s_AnchorLocal;
                        if (s_ScrollTarget != null)
                        {
                            if (IsValidTarget(s_ScrollTarget))
                            {
                                s_ScrollTarget.OnVisualTouchBegin(s_AnchorLocal);
                                s_ScrollTarget.OnVisualTouchMove(local2D, initialDelta);
                            }
                        }
                        else
                        {
                            ApplyScrollRectDelta(initialDelta);
                        }
                        s_LastLocal = local2D;
                        if (dt > 0.001f)
                            s_LocalVelocity = initialDelta / Mathf.Max(dt, 0.008f);
                    }
                    return;
                }

                var delta = local2D - s_LastLocal;
                s_LastLocal = local2D;
                if (delta.sqrMagnitude > 1e-6f)
                {
                    if (s_ScrollTarget != null)
                    {
                        if (IsValidTarget(s_ScrollTarget))
                            s_ScrollTarget.OnVisualTouchMove(local2D, delta);
                    }
                    else
                    {
                        ApplyScrollRectDelta(delta);
                    }
                    if (dt > 0.001f)
                    {
                        var instVel = delta / Mathf.Max(dt, 0.005f);
                        s_LocalVelocity = Vector2.Lerp(s_LocalVelocity, instVel, 1f - Mathf.Exp(-dt * 14f));
                    }
                }
                else if (dt > 0f)
                {
                    s_LocalVelocity = Vector2.Lerp(s_LocalVelocity, Vector2.zero, 1f - Mathf.Exp(-dt * 12f));
                }
                return;
            }

            if (s_ScrollRect != null && s_LocalMomentum.sqrMagnitude > k_MinMomentumSpeed * k_MinMomentumSpeed && dt > 0f)
            {
                s_LocalMomentum *= Mathf.Exp(-dt * k_MomentumDecay);
                ApplyScrollRectDelta(s_LocalMomentum * dt);
                if (s_LocalMomentum.sqrMagnitude <= k_MinMomentumSpeed * k_MinMomentumSpeed)
                {
                    s_LocalMomentum = Vector2.zero;
                    s_ScrollRect = null;
                }
            }
        }

        public static void End(bool cancelled = false, bool suppressClick = false)
        {
            if (!s_Active)
                return;
            var wasDragging = s_Dragging;
            var gaze = s_Gaze;
            var target = s_StartTarget;
            var worldPoint = s_StartWorldPoint;
            var scrollTarget = s_ScrollTarget;
            var scrollRect = s_ScrollRect;
            var lastLocal = s_LastLocal;
            var velocity = s_LocalVelocity;

            s_Active = false;
            s_Dragging = false;
            s_Gaze = null;
            s_StartTarget = null;
            s_SurfaceRect = null;
            s_ScrollTarget = null;

            if (!wasDragging)
            {
                s_LocalMomentum = Vector2.zero;
                s_ScrollRect = null;
                if (!suppressClick && !cancelled)
                    FireDeferredClick(gaze, target, worldPoint);
                return;
            }

            if (scrollTarget != null && IsValidTarget(scrollTarget))
                scrollTarget.OnVisualTouchEnd(lastLocal, cancelled);

            if (scrollRect != null && !cancelled)
            {
                s_ScrollRect = scrollRect;
                s_LocalMomentum = Vector2.ClampMagnitude(velocity, k_MaxMomentumSpeed);
            }
            else
            {
                s_LocalMomentum = Vector2.zero;
                s_ScrollRect = null;
            }
        }

        public static void Reset()
        {
            s_Active = false;
            s_Dragging = false;
            s_HasPinchRay = false;
            s_StartAimRay = default;
            s_StartPinchDir = Vector3.forward;
            s_Gaze = null;
            s_StartTarget = null;
            s_StartTime = 0f;
            s_TouchSlopDp = k_MinTouchSlopDp;
            s_ScrollTarget = null;
            s_ScrollRect = null;
            s_SurfaceRect = null;
            s_LocalVelocity = Vector2.zero;
            s_LocalMomentum = Vector2.zero;
            IsPinchHeld = false;
        }

        static bool TryProjectOntoRect(Ray ray, RectTransform rect, out Vector2 localPoint)
        {
            localPoint = Vector2.zero;
            if (rect == null || Vector3.Dot(ray.direction, rect.forward) <= 0f)
                return false;
            var plane = new Plane(rect.forward, rect.position);
            if (!plane.Raycast(ray, out var distance) || distance <= 0f || distance > k_MaxRayDistance)
                return false;
            localPoint = rect.InverseTransformPoint(ray.GetPoint(distance));
            return true;
        }

        static bool IsValidTarget(IVisualScrollTarget target) =>
            target != null && !(target is Object unityObj && unityObj == null);

        static void ApplyScrollRectDelta(Vector2 localDelta)
        {
            if (s_ScrollRect == null || s_ScrollRect.content == null)
                return;
            var content = s_ScrollRect.content;
            var viewport = s_ScrollRect.viewport != null ? s_ScrollRect.viewport : (RectTransform)s_ScrollRect.transform;
            var pos = content.anchoredPosition;
            if (s_ScrollRect.horizontal)
                pos.x += localDelta.x;
            if (s_ScrollRect.vertical)
                pos.y += localDelta.y;

            var maxX = Mathf.Max(0f, content.rect.width - viewport.rect.width);
            var maxY = Mathf.Max(0f, content.rect.height - viewport.rect.height);
            pos.x = Mathf.Clamp(pos.x, -maxX, 0f);
            pos.y = Mathf.Clamp(pos.y, 0f, maxY);
            content.anchoredPosition = pos;
        }

        static void FireDeferredClick(GazePointer gaze, GameObject startTarget, Vector3 worldPoint)
        {
            if (gaze == null)
                return;
            if (!PinchRouter.TryConsumeClickLockout())
                return;

            var target = gaze.Target != null ? gaze.Target : startTarget;
            if (target == null)
                return;

            if (gaze.Target == startTarget)
            {
                Debug.Log($"[Pinch] Quick pinch tap -> clicking {target.name}");
                gaze.TriggerDown();
                gaze.TriggerUp();
                return;
            }

            var clickHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(startTarget);
            if (clickHandler == null)
                return;
            var eventData = new PointerEventData(EventSystem.current)
            {
                button = PointerEventData.InputButton.Left,
                eligibleForClick = true,
                pointerCurrentRaycast = new RaycastResult
                {
                    gameObject = startTarget,
                    worldPosition = worldPoint,
                },
            };
            Debug.Log($"[Pinch] Deferred pinch tap -> clicking {clickHandler.name}");
            ExecuteEvents.Execute(clickHandler, eventData, ExecuteEvents.pointerClickHandler);
        }
    }
}
