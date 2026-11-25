using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    public enum PlacementSurfaceFilter
    {
        HorizontalAny,
        FloorOnly,
    }

    public enum SpatialHitQuality
    {
        None,
        EstimatedFloor,
        FeatureOrEstimated,
        Depth,
        PlanePolygon,
    }

    public struct SpatialSurfaceHit
    {
        public bool Valid;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public SpatialHitQuality Quality;
        public ARPlane Plane;
    }

    public struct RaycastCandidate
    {
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public TrackableType HitType;
        public ARPlane Plane;
    }

    // Allows Interaction assembly (GazePointer) to query spatial placement surfaces without
    // creating a circular dependency.
    public interface ISpatialSurfaceProvider
    {
        bool IsPlacementActive { get; }
        bool TryRaycast(Ray ray, out GameObject target, out Vector3 point, out Vector3 normal, out float distance);
        void NotifyHitResult(bool won);
    }

    // Leaf click receiver so clicks on real surfaces only invoke SpatialSurfaceLocator without
    // catching pointer events bubbling from other children under XR Origin.
    internal sealed class SpatialSurfaceClickTarget : MonoBehaviour, IPointerClickHandler
    {
        SpatialSurfaceLocator m_Owner;

        internal void Initialize(SpatialSurfaceLocator owner) => m_Owner = owner;

        public void OnPointerClick(PointerEventData eventData) => m_Owner?.PlaceAtCurrentHit();
    }

    // Modern hybrid spatial surface locator for Cube Sandbox and Portal:
    //   1. Native ARRaycastManager against ARPlane polygons + ARCore Depth API + estimated planes + feature points.
    //   2. Direct tracked ARPlane boundary polygon raycast (ensures exact table edges on both Android and iOS).
    //   3. Instant calibrated floor plane fallback while 6DoF is tracking so placement works immediately (< 1s).
    // Renders a sleek 3D placement reticle flush on the targeted real surface and enables Android Depth API
    // on demand only while a placement demo is active. Logs [Spatial].
    [DefaultExecutionOrder(-75)]
    public class SpatialSurfaceLocator : MonoBehaviour, ISpatialSurfaceProvider
    {
        const float k_MinRangeAny = 0.35f;
        const float k_MaxRangeAny = 6.50f;
        const float k_MinRangeFloor = 0.70f;
        const float k_MaxRangeFloor = 6.00f;
        const float k_FeatureMinDistance = 0.50f;
        const float k_DepthBeatsPlaneMargin = 0.12f;
        const float k_HorizontalMinNormalY = 0.55f;
        const float k_FlatNormalSnapY = 0.78f;
        const float k_FloorMinDepthBelowHead = 0.85f;
        const float k_FloorMaxDepthBelowHead = 2.20f;
        const float k_FloorToleranceAboveKnown = 0.30f;
        const float k_FallbackFloorDepth = 1.45f;
        const float k_FallbackMinRayDownY = -0.15f;
        const float k_FallbackMinDistance = 0.65f;
        const float k_FallbackMaxDistance = 5.50f;
        const float k_ReticleOffset = 0.004f;
        const float k_ReticleAnyScale = 0.16f;
        const float k_ReticleFloorScale = 0.52f;
        const float k_ReticlePosRate = 22f;
        const float k_ReticleRotRate = 18f;
        const float k_ReticleFadeRate = 10f;
        const float k_ReticleSnapDistance = 0.60f;
        const float k_StatusSeconds = 5f;
        const int k_DepthFloorWindow = 5;

        const TrackableType k_RaycastMask =
            TrackableType.PlaneWithinPolygon |
            TrackableType.Depth |
            TrackableType.PlaneEstimated |
            TrackableType.FeaturePoint;

        static readonly int k_ColorId = Shader.PropertyToID("_Color");
        static readonly int k_FillColorId = Shader.PropertyToID("_FillColor");
        static readonly int k_AlphaId = Shader.PropertyToID("_Alpha");
        static readonly int k_DashedId = Shader.PropertyToID("_Dashed");
        static readonly int k_DirectionalId = Shader.PropertyToID("_Directional");
        static readonly int k_PulseId = Shader.PropertyToID("_Pulse");

        static readonly Color k_SolidRingColor = new(0.86f, 0.96f, 1.00f, 0.95f);
        static readonly Color k_SolidFillColor = new(0.45f, 0.86f, 1.00f, 0.16f);
        static readonly Color k_EstimatedRingColor = new(0.45f, 0.86f, 1.00f, 0.82f);
        static readonly Color k_EstimatedFillColor = new(0.45f, 0.86f, 1.00f, 0.10f);

        static readonly List<ARRaycastHit> s_ArHits = new();
        static readonly List<RaycastCandidate> s_Candidates = new();
        static readonly List<Vector2> s_PolygonBuffer = new();

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] RoomBoundary m_Boundary;
        [SerializeField] AnchorPlacer m_Placer;
        [SerializeField] ARRaycastManager m_Raycasts;
        [SerializeField] ARPlaneManager m_Planes;
        [SerializeField] AROcclusionManager m_Occlusion;
        [SerializeField] Material m_ReticleMaterial;

        SpatialSurfaceClickTarget m_ClickTarget;
        GameObject m_ReticleObject;
        MeshRenderer m_ReticleRenderer;
        Material m_RuntimeReticleMaterial;
        MaterialPropertyBlock m_ReticleBlock;

        bool m_Active;
        bool m_WonGaze;
        bool m_OwnsAndroidDepth;
        SpatialSurfaceHit m_LastHit;
        float m_ReticleAlpha;
        bool m_HasReticlePose;
        Vector3 m_ReticlePos;
        Quaternion m_ReticleRot = Quaternion.identity;
        float m_NextStatusAt;

        readonly float[] m_DepthFloorSamples = new float[k_DepthFloorWindow];
        readonly float[] m_DepthFloorScratch = new float[k_DepthFloorWindow];
        int m_DepthFloorCount;
        int m_DepthFloorCursor;

        public static ISpatialSurfaceProvider Active { get; private set; }
        public static SpatialSurfaceLocator Instance { get; private set; }

        public bool IsPlacementActive => m_Active;
        public bool ExternalPlacementRequested { get; set; }

        void Awake()
        {
            Instance = this;
            SessionLog.KeepTag("[Spatial]");
            if (m_Fusion == null)
                m_Fusion = FindAnyObjectByType<HeadPoseFusion>();
            if (m_Boundary == null)
                m_Boundary = GetComponent<RoomBoundary>();
            if (m_Placer == null)
                m_Placer = GetComponent<AnchorPlacer>();
            if (m_Raycasts == null)
                m_Raycasts = GetComponent<ARRaycastManager>();
            if (m_Planes == null)
                m_Planes = GetComponent<ARPlaneManager>();
            if (m_Occlusion == null)
                m_Occlusion = GetComponentInChildren<AROcclusionManager>(true);
        }

        void OnEnable()
        {
            if (Active == null || ReferenceEquals(Active, this))
                Active = this;
        }

        void OnDisable()
        {
            if (ReferenceEquals(Active, this))
                Active = null;
            ExternalPlacementRequested = false;
            SetPlacementEnabled(false);
            if (m_ReticleRenderer != null)
                m_ReticleRenderer.enabled = false;
            m_ReticleAlpha = 0f;
            m_HasReticlePose = false;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (ReferenceEquals(Active, this))
                Active = null;
            if (m_OwnsAndroidDepth && m_Occlusion != null)
            {
                m_OwnsAndroidDepth = false;
                ArDepthCoordinator.SetRequested(m_Occlusion, "SpatialSurfaceLocator", false);
            }
            if (m_RuntimeReticleMaterial != null)
                Destroy(m_RuntimeReticleMaterial);
            if (m_ReticleObject != null)
                Destroy(m_ReticleObject);
            if (m_ClickTarget != null)
                Destroy(m_ClickTarget.gameObject);
        }

        void Update()
        {
            var wantActive = m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof &&
                             ((m_Placer != null && m_Placer.ObjectPrefab != null) || ExternalPlacementRequested);
            SetPlacementEnabled(wantActive);
            m_WonGaze = false;
        }

        void LateUpdate()
        {
            UpdateReticle();
            LogStatus();
        }

        void SetPlacementEnabled(bool active)
        {
            if (m_Active != active)
            {
                m_Active = active;
                m_LastHit = default;
                m_DepthFloorCount = 0;
                m_DepthFloorCursor = 0;
                if (active)
                    m_NextStatusAt = Time.unscaledTime + k_StatusSeconds;
                Debug.Log(active
                    ? $"[Spatial] Hybrid placement active (filter={(m_Placer != null ? m_Placer.SurfaceFilter : PlacementSurfaceFilter.HorizontalAny)})"
                    : "[Spatial] Hybrid placement inactive");
            }

            if (m_Boundary != null && m_Boundary.ContinuousPlacementActive != active)
                m_Boundary.ContinuousPlacementActive = active;

            if (m_Raycasts != null && m_Raycasts.enabled != active)
                m_Raycasts.enabled = active;

            if (Application.platform == RuntimePlatform.Android && m_Occlusion != null)
            {
                if (active && !m_OwnsAndroidDepth)
                {
                    m_OwnsAndroidDepth = true;
                    ArDepthCoordinator.SetRequested(m_Occlusion, "SpatialSurfaceLocator", true, preferTemporalSmoothing: true);
                }
                else if (!active && m_OwnsAndroidDepth)
                {
                    m_OwnsAndroidDepth = false;
                    ArDepthCoordinator.SetRequested(m_Occlusion, "SpatialSurfaceLocator", false);
                }
            }
        }

        public bool TryRaycast(Ray ray, out GameObject target, out Vector3 point, out Vector3 normal, out float distance)
        {
            target = null;
            point = default;
            normal = Vector3.up;
            distance = 0f;

            if (!m_Active || m_Fusion == null || m_Placer == null)
                return false;

            EnsureClickTarget();
            s_Candidates.Clear();

            var headPos = m_Fusion.transform.position;
            var filter = m_Placer.SurfaceFilter;
            var knownFloorY = m_Boundary != null ? m_Boundary.FloorWorldHeight : null;

            GatherNativeRaycastCandidates(ray, headPos);
            GatherTrackedPlaneCandidates(ray, filter);

            var best = SelectBestHit(s_Candidates, headPos, filter, knownFloorY);
            if (!best.Valid && m_Fusion.IsArTracking)
                TryEstimateFloorHit(ray, headPos, knownFloorY, out best);

            if (!best.Valid)
            {
                m_LastHit = default;
                return false;
            }

            m_LastHit = best;
            target = m_ClickTarget.gameObject;
            point = best.Point;
            normal = best.Normal;
            distance = best.Distance;
            return true;
        }

        public void NotifyHitResult(bool won)
        {
            m_WonGaze = won;
            if (!won)
                m_LastHit = default;
        }

        public bool TrySampleSurface(Ray ray, out SpatialSurfaceHit hit)
        {
            hit = default;
            if (m_Fusion == null)
                return false;

            var headPos = m_Fusion.transform.position;
            var filter = PlacementSurfaceFilter.HorizontalAny;
            var knownFloorY = m_Boundary != null ? m_Boundary.FloorWorldHeight : null;

            s_Candidates.Clear();
            GatherNativeRaycastCandidates(ray, headPos);
            GatherTrackedPlaneCandidates(ray, filter);

            hit = SelectBestHit(s_Candidates, headPos, filter, knownFloorY);
            if (!hit.Valid && m_Fusion.IsArTracking)
                TryEstimateFloorHit(ray, headPos, knownFloorY, out hit);

            if (hit.Valid)
            {
                m_LastHit = hit;
                m_WonGaze = true;
            }
            return hit.Valid;
        }

        internal void PlaceAtCurrentHit()
        {
            if (!m_Active || !m_LastHit.Valid || m_Placer == null)
                return;
            m_Placer.Place(m_LastHit.Point, m_LastHit.Normal, m_LastHit.Plane, m_LastHit.Quality);
        }

        void GatherNativeRaycastCandidates(Ray ray, Vector3 headPos)
        {
            if (m_Raycasts == null || !m_Raycasts.enabled || m_Raycasts.subsystem == null || !m_Fusion.IsArTracking)
                return;

            s_ArHits.Clear();
            bool hasHits;
            try
            {
                hasHits = m_Raycasts.Raycast(ray, s_ArHits, k_RaycastMask);
            }
            catch (Exception)
            {
                return;
            }

            if (!hasHits)
                return;

            for (var i = 0; i < s_ArHits.Count; i++)
            {
                var hit = s_ArHits[i];
                var plane = hit.trackable as ARPlane;
                var normal = plane != null ? plane.normal : hit.pose.up;
                if (normal.sqrMagnitude < 1e-6f)
                    normal = Vector3.up;
                else
                    normal.Normalize();

                if ((hit.hitType & TrackableType.Depth) != 0)
                    ObserveDepthFloorSample(headPos, hit.pose.position, hit.sessionRelativePose.position.y, normal);

                s_Candidates.Add(new RaycastCandidate
                {
                    Point = hit.pose.position,
                    Normal = normal,
                    Distance = hit.distance,
                    HitType = hit.hitType,
                    Plane = plane,
                });
            }
        }

        void GatherTrackedPlaneCandidates(Ray ray, PlacementSurfaceFilter filter)
        {
            if (m_Planes == null || m_Planes.trackables.count == 0)
                return;

            foreach (var plane in m_Planes.trackables)
            {
                if (plane == null || !plane.gameObject.activeInHierarchy ||
                    plane.subsumedBy != null || plane.trackingState == TrackingState.None)
                    continue;

                var planeNormal = plane.normal;
                if (planeNormal.sqrMagnitude < 1e-6f)
                    planeNormal = plane.transform.up;
                else
                    planeNormal.Normalize();

                if (planeNormal.y < (filter == PlacementSurfaceFilter.FloorOnly ? k_FlatNormalSnapY : k_HorizontalMinNormalY))
                    continue;

                var geomPlane = new Plane(planeNormal, plane.transform.position);
                if (!geomPlane.Raycast(ray, out var dist) || dist <= 0.1f)
                    continue;

                var worldPt = ray.GetPoint(dist);
                var localPt = plane.transform.InverseTransformPoint(worldPt);
                var planePt = new Vector2(localPt.x, localPt.z);

                var boundary = plane.boundary;
                var inside = false;
                if (boundary.IsCreated && boundary.Length >= 3)
                {
                    s_PolygonBuffer.Clear();
                    for (var i = 0; i < boundary.Length; i++)
                        s_PolygonBuffer.Add(boundary[i]);
                    inside = PointInPolygon(planePt, s_PolygonBuffer);
                }
                else
                {
                    var center = plane.centerInPlaneSpace;
                    var ext = plane.extents;
                    inside = Mathf.Abs(planePt.x - center.x) <= ext.x && Mathf.Abs(planePt.y - center.y) <= ext.y;
                }

                if (!inside)
                    continue;

                s_Candidates.Add(new RaycastCandidate
                {
                    Point = worldPt,
                    Normal = planeNormal,
                    Distance = dist,
                    HitType = TrackableType.PlaneWithinPolygon,
                    Plane = plane,
                });
            }
        }

        void ObserveDepthFloorSample(Vector3 headPos, Vector3 hitWorld, float sessionY, Vector3 normal)
        {
            if (m_Boundary == null || normal.y < 0.85f)
                return;
            var depthBelow = headPos.y - hitWorld.y;
            if (depthBelow < 0.90f || depthBelow > k_FloorMaxDepthBelowHead)
                return;

            m_DepthFloorSamples[m_DepthFloorCursor] = sessionY;
            m_DepthFloorCursor = (m_DepthFloorCursor + 1) % k_DepthFloorWindow;
            if (m_DepthFloorCount < k_DepthFloorWindow)
                m_DepthFloorCount++;

            if (m_DepthFloorCount < 3)
                return;

            Array.Copy(m_DepthFloorSamples, m_DepthFloorScratch, m_DepthFloorCount);
            Array.Sort(m_DepthFloorScratch, 0, m_DepthFloorCount);
            var median = m_DepthFloorCount % 2 == 1
                ? m_DepthFloorScratch[m_DepthFloorCount / 2]
                : 0.5f * (m_DepthFloorScratch[m_DepthFloorCount / 2 - 1] + m_DepthFloorScratch[m_DepthFloorCount / 2]);
            m_Boundary.ObserveDepthFloor(median);
        }

        void EnsureClickTarget()
        {
            if (m_ClickTarget != null)
                return;
            var go = new GameObject("Spatial Placement Target");
            go.transform.SetParent(transform, false);
            m_ClickTarget = go.AddComponent<SpatialSurfaceClickTarget>();
            m_ClickTarget.Initialize(this);
        }

        void EnsureReticle()
        {
            if (m_ReticleObject != null)
                return;

            var mat = m_ReticleMaterial;
            if (mat == null)
            {
                var shader = Shader.Find("CardboardXR/PlacementReticle");
                if (shader == null)
                    return;
                m_RuntimeReticleMaterial = new Material(shader) { name = "Placement Reticle Runtime" };
                mat = m_RuntimeReticleMaterial;
            }

            m_ReticleObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            m_ReticleObject.name = "Spatial Placement Reticle";
            Destroy(m_ReticleObject.GetComponent<Collider>());
            m_ReticleRenderer = m_ReticleObject.GetComponent<MeshRenderer>();
            m_ReticleRenderer.sharedMaterial = mat;
            m_ReticleRenderer.shadowCastingMode = ShadowCastingMode.Off;
            m_ReticleRenderer.receiveShadows = false;
            m_ReticleRenderer.enabled = false;
            m_ReticleBlock = new MaterialPropertyBlock();
        }

        void UpdateReticle()
        {
            EnsureReticle();
            if (m_ReticleRenderer == null)
                return;

            var show = m_Active && m_WonGaze && m_LastHit.Valid;
            var dt = Time.unscaledDeltaTime;
            var prevAlpha = m_ReticleAlpha;
            m_ReticleAlpha = Mathf.MoveTowards(m_ReticleAlpha, show ? 1f : 0f, dt * k_ReticleFadeRate);

            if (m_ReticleAlpha <= 0.005f)
            {
                if (m_ReticleRenderer.enabled)
                    m_ReticleRenderer.enabled = false;
                m_HasReticlePose = false;
                return;
            }

            if (!m_ReticleRenderer.enabled)
                m_ReticleRenderer.enabled = true;

            if (show && m_Fusion != null)
            {
                var targetPos = m_LastHit.Point + m_LastHit.Normal * k_ReticleOffset;
                var facing = Vector3.ProjectOnPlane(m_Fusion.transform.position - m_LastHit.Point, m_LastHit.Normal);
                if (facing.sqrMagnitude < 1e-6f)
                    facing = Vector3.ProjectOnPlane(-m_Fusion.transform.forward, m_LastHit.Normal);
                if (facing.sqrMagnitude < 1e-6f)
                    facing = Vector3.ProjectOnPlane(Vector3.forward, m_LastHit.Normal);
                if (facing.sqrMagnitude < 1e-6f)
                    facing = Vector3.ProjectOnPlane(Vector3.right, m_LastHit.Normal);
                // Unity's Quad front face points along local -Z, with +Y up.
                var targetRot = Quaternion.LookRotation(-m_LastHit.Normal, facing.normalized);

                var snap = !m_HasReticlePose || prevAlpha <= 0.01f ||
                           Vector3.Distance(m_ReticlePos, targetPos) > k_ReticleSnapDistance;
                if (snap)
                {
                    m_ReticlePos = targetPos;
                    m_ReticleRot = targetRot;
                    m_HasReticlePose = true;
                }
                else
                {
                    m_ReticlePos = Vector3.Lerp(m_ReticlePos, targetPos, 1f - Mathf.Exp(-dt * k_ReticlePosRate));
                    m_ReticleRot = Quaternion.Slerp(m_ReticleRot, targetRot, 1f - Mathf.Exp(-dt * k_ReticleRotRate));
                }

                var isFloorMode = m_Placer != null && m_Placer.SurfaceFilter == PlacementSurfaceFilter.FloorOnly;
                var scale = isFloorMode ? k_ReticleFloorScale : k_ReticleAnyScale;
                m_ReticleObject.transform.SetPositionAndRotation(m_ReticlePos, m_ReticleRot);
                m_ReticleObject.transform.localScale = new Vector3(scale, scale, 1f);

                var isSolid = m_LastHit.Quality is SpatialHitQuality.PlanePolygon or SpatialHitQuality.Depth;
                m_ReticleBlock.SetColor(k_ColorId, isSolid ? k_SolidRingColor : k_EstimatedRingColor);
                m_ReticleBlock.SetColor(k_FillColorId, isSolid ? k_SolidFillColor : k_EstimatedFillColor);
                m_ReticleBlock.SetFloat(k_DashedId, isSolid ? 0f : 1f);
                m_ReticleBlock.SetFloat(k_DirectionalId, isFloorMode ? 1f : 0f);
            }

            m_ReticleBlock.SetFloat(k_AlphaId, Mathf.SmoothStep(0f, 1f, m_ReticleAlpha));
            m_ReticleBlock.SetFloat(k_PulseId, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3.5f));
            m_ReticleRenderer.SetPropertyBlock(m_ReticleBlock);
        }

        void LogStatus()
        {
            if (!m_Active || Time.unscaledTime < m_NextStatusAt)
                return;
            m_NextStatusAt = Time.unscaledTime + k_StatusSeconds;

            var depthState = m_Occlusion == null
                ? "none"
                : $"{m_Occlusion.currentEnvironmentDepthMode} (enabled={m_Occlusion.enabled})";
            var planesCount = m_Planes != null ? m_Planes.trackables.count : 0;
            var hitText = m_LastHit.Valid ? $"{m_LastHit.Quality} @ {m_LastHit.Distance:F2} m" : "none";
            Debug.Log($"[Spatial] Active: hit={hitText}, depth={depthState}, planes={planesCount}");
        }

        // Selects the highest-priority valid surface candidate for the requested filter. Pure, for testing.
        public static SpatialSurfaceHit SelectBestHit(IReadOnlyList<RaycastCandidate> candidates,
            Vector3 headPosition, PlacementSurfaceFilter filter, float? knownFloorWorldY)
        {
            if (candidates == null || candidates.Count == 0)
                return default;

            var minRange = filter == PlacementSurfaceFilter.FloorOnly ? k_MinRangeFloor : k_MinRangeAny;
            var maxRange = filter == PlacementSurfaceFilter.FloorOnly ? k_MaxRangeFloor : k_MaxRangeAny;

            var hasPlane = false;
            var bestPlane = default(SpatialSurfaceHit);
            var hasDepth = false;
            var bestDepth = default(SpatialSurfaceHit);
            var hasEstimated = false;
            var bestEstimated = default(SpatialSurfaceHit);
            var hasFeature = false;
            var bestFeature = default(SpatialSurfaceHit);

            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.Distance < minRange || c.Distance > maxRange)
                    continue;

                var normal = c.Normal.sqrMagnitude > 1e-6f ? c.Normal.normalized : Vector3.up;
                if (filter == PlacementSurfaceFilter.FloorOnly)
                {
                    if (normal.y < k_FlatNormalSnapY)
                        continue;
                    if (headPosition.y - c.Point.y < k_FloorMinDepthBelowHead)
                        continue;
                    if (knownFloorWorldY.HasValue && c.Point.y > knownFloorWorldY.Value + k_FloorToleranceAboveKnown)
                        continue;
                    normal = Vector3.up;
                }
                else
                {
                    if (normal.y < k_HorizontalMinNormalY)
                        continue;
                    if (normal.y >= k_FlatNormalSnapY)
                        normal = Vector3.up;
                }

                if ((c.HitType & TrackableType.PlaneWithinPolygon) != 0)
                {
                    if (!hasPlane || c.Distance < bestPlane.Distance)
                    {
                        hasPlane = true;
                        bestPlane = MakeHit(c.Point, normal, c.Distance, SpatialHitQuality.PlanePolygon, c.Plane);
                    }
                }
                else if ((c.HitType & TrackableType.Depth) != 0)
                {
                    if (!hasDepth || c.Distance < bestDepth.Distance)
                    {
                        hasDepth = true;
                        bestDepth = MakeHit(c.Point, normal, c.Distance, SpatialHitQuality.Depth, c.Plane);
                    }
                }
                else if ((c.HitType & TrackableType.PlaneEstimated) != 0)
                {
                    if (!hasEstimated || c.Distance < bestEstimated.Distance)
                    {
                        hasEstimated = true;
                        bestEstimated = MakeHit(c.Point, normal, c.Distance, SpatialHitQuality.FeatureOrEstimated, c.Plane);
                    }
                }
                else if ((c.HitType & TrackableType.FeaturePoint) != 0 && c.Distance >= k_FeatureMinDistance)
                {
                    if (!hasFeature || c.Distance < bestFeature.Distance)
                    {
                        hasFeature = true;
                        bestFeature = MakeHit(c.Point, normal, c.Distance, SpatialHitQuality.FeatureOrEstimated, c.Plane);
                    }
                }
            }

            if (hasPlane && hasDepth)
                return bestDepth.Distance < bestPlane.Distance - k_DepthBeatsPlaneMargin ? bestDepth : bestPlane;
            if (hasPlane)
                return bestPlane;
            if (hasDepth)
                return bestDepth;
            if (hasEstimated)
                return bestEstimated;
            if (hasFeature)
                return bestFeature;
            return default;
        }

        // Instant calibrated floor intersection fallback while AR is tracking but no plane/depth hit exists yet.
        // Pure, for testing.
        public static bool TryEstimateFloorHit(Ray ray, Vector3 headPosition, float? knownFloorWorldY,
            out SpatialSurfaceHit hit)
        {
            hit = default;
            if (ray.direction.y > k_FallbackMinRayDownY)
                return false;

            var dir = ray.direction.normalized;
            if (Mathf.Abs(dir.y) < 1e-4f)
                return false;

            var floorY = knownFloorWorldY ?? (headPosition.y - k_FallbackFloorDepth);
            var t = (floorY - ray.origin.y) / dir.y;
            if (t < k_FallbackMinDistance || t > k_FallbackMaxDistance)
                return false;

            hit = MakeHit(ray.origin + dir * t, Vector3.up, t, SpatialHitQuality.EstimatedFloor, null);
            return true;
        }

        // 2D even-odd ray-crossing point-in-polygon test in plane local coordinates. Pure, for testing.
        public static bool PointInPolygon(Vector2 point, IReadOnlyList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return false;

            var inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                var intersects = (pi.y > point.y) != (pj.y > point.y) &&
                                 point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y) + pi.x;
                if (intersects)
                    inside = !inside;
            }
            return inside;
        }

        static SpatialSurfaceHit MakeHit(Vector3 point, Vector3 normal, float distance,
            SpatialHitQuality quality, ARPlane plane) => new()
        {
            Valid = true,
            Point = point,
            Normal = normal,
            Distance = distance,
            Quality = quality,
            Plane = plane,
        };
    }
}
