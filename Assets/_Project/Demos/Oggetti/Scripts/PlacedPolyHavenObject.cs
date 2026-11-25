using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Demos.Oggetti
{
    // A placed Poly Haven 3D object anchored cleanly onto a real room surface:
    //   - Persists anchored to its surface via ARCore ARAnchor + XR Origin session fallback across the session
    //   - Rotates strictly around the vertical up axis (Blender Z-axis / Unity Y-axis, zero tilt)
    //   - Reveals its floating controls (Move / Rotate / Scale / Delete) ONLY when looked at
    public class PlacedPolyHavenObject : MonoBehaviour
    {
        const float k_GraceSeconds = 0.55f;
        const float k_FadeInSpeed = 9f;
        const float k_FadeOutSpeed = 5.5f;
        const float k_ControlsMetersPerDp = 0.00125f;

        LoadedGltfModel m_Model;
        Transform m_VisualRoot;
        BoxCollider m_Collider;

        ARAnchor m_Anchor;
        int m_AnchorRequestVersion;
        Vector3 m_SessionPos;
        float m_SessionYawDeg;
        bool m_HasSessionPose;

        float m_BaseScale = 1f;
        float m_Scale = 1f;
        float m_YawDeg;
        bool m_IsMoving;
        bool m_IsManipulating;

        RectTransform m_Controls;
        CanvasGroup m_ControlsGroup;
        M3Button m_MoveBtn;
        M3Button m_RotateBtn;
        M3Button m_ScaleBtn;
        float m_LastGazeAt = float.NegativeInfinity;
        float m_ControlsAlpha;

        OggettiRuntimeHost m_Host;

        public string AssetId { get; private set; }
        public string AssetName { get; private set; }
        public float CurrentScale => m_Scale;
        public float EffectiveScale => m_BaseScale * m_Scale;
        public float CurrentYawDeg => m_YawDeg;
        public bool IsMoving => m_IsMoving;
        public float BoundsHeightMeters => m_Model != null ? Mathf.Max(0.18f, m_Model.LocalBounds.size.y * EffectiveScale) : 0.4f;

        public void Initialize(OggettiRuntimeHost host, string assetId, string assetName, LoadedGltfModel model)
        {
            m_Host = host;
            AssetId = assetId ?? "";
            AssetName = string.IsNullOrWhiteSpace(assetName) ? AssetId : assetName;
            m_Model = model;

            m_VisualRoot = model.Root.transform;
            m_VisualRoot.SetParent(transform, false);
            m_VisualRoot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);

            m_Collider = gameObject.AddComponent<BoxCollider>();
            m_BaseScale = OggettiLayout.DefaultNormalizedScale(model.LocalBounds);
            m_Scale = 1f;
            SetScale(1f);
            BuildControls();
        }

        void BuildControls()
        {
            m_Controls = M3.Canvas($"Object Controls - {AssetId}", transform, new Vector2(236f, 54f), 1.4f,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            m_Controls.gameObject.AddComponent<GazeCanvas>();
            FloatingWindowHandle.Exclude(m_Controls);
            m_Controls.localScale = Vector3.one * k_ControlsMetersPerDp;

            m_ControlsGroup = m_Controls.gameObject.AddComponent<CanvasGroup>();
            SetControlsGroup(0f, false);

            m_MoveBtn = M3.IconButton("Move", m_Controls, "open_with", M3Button.Variant.Tonal,
                new Vector2(-81f, 0f), () => m_Host?.BeginMove(this), 40f);
            m_RotateBtn = M3.IconButton("Rotate", m_Controls, "refresh", M3Button.Variant.Tonal,
                new Vector2(-27f, 0f), () => m_Host?.BeginRotate(this), 40f);
            m_ScaleBtn = M3.IconButton("Scale", m_Controls, "aspect_ratio", M3Button.Variant.Tonal,
                new Vector2(27f, 0f), () => m_Host?.BeginScale(this), 40f);
            M3.IconButton("Delete", m_Controls, "delete", M3Button.Variant.Tonal,
                new Vector2(81f, 0f), () => m_Host?.RemoveObject(this), 40f);

            PositionControls();
        }

        public void SetInteractionFlags(bool isMoving, bool isRotating, bool isScaling)
        {
            m_IsMoving = isMoving;
            m_IsManipulating = isMoving || isRotating || isScaling;
            if (m_Collider != null)
                m_Collider.enabled = !isMoving;
            if (m_MoveBtn != null)
                m_MoveBtn.Selected = isMoving;
            if (m_RotateBtn != null)
                m_RotateBtn.Selected = isRotating;
            if (m_ScaleBtn != null)
                m_ScaleBtn.Selected = isScaling;
        }

        public void SetPreviewPose(Vector3 worldPos, float yawDeg)
        {
            m_YawDeg = OggettiLayout.NormalizeYaw(yawDeg);
            transform.SetPositionAndRotation(worldPos, OggettiLayout.VerticalAxisRotation(m_YawDeg));
            UpdateSessionPoseFromWorld(worldPos, m_YawDeg);
        }

        public void SetScale(float scale)
        {
            m_Scale = OggettiLayout.ClampScale(scale);
            var effective = EffectiveScale;
            if (m_VisualRoot != null)
                m_VisualRoot.localScale = Vector3.one * effective;
            if (m_Collider != null && m_Model != null)
            {
                var b = m_Model.LocalBounds;
                m_Collider.center = b.center * effective;
                m_Collider.size = new Vector3(
                    Mathf.Max(0.14f, b.size.x * effective),
                    Mathf.Max(0.14f, b.size.y * effective),
                    Mathf.Max(0.14f, b.size.z * effective));
            }
            PositionControls();
        }

        public void SetYaw(float yawDeg)
        {
            m_YawDeg = OggettiLayout.NormalizeYaw(yawDeg);
            transform.rotation = OggettiLayout.VerticalAxisRotation(m_YawDeg);
            UpdateSessionPoseFromWorld(transform.position, m_YawDeg);
        }

        public void AnchorAt(Vector3 worldPos, float yawDeg)
        {
            m_YawDeg = OggettiLayout.NormalizeYaw(yawDeg);
            transform.SetPositionAndRotation(worldPos, OggettiLayout.VerticalAxisRotation(m_YawDeg));
            UpdateSessionPoseFromWorld(worldPos, m_YawDeg);
            m_AnchorRequestVersion++;
            CreateAnchorAsync(worldPos, m_YawDeg, m_AnchorRequestVersion);
        }

        void UpdateSessionPoseFromWorld(Vector3 worldPos, float worldYawDeg)
        {
            var placer = AnchorPlacer.Instance;
            if (placer == null || placer.Origin == null)
                return;
            var origin = placer.Origin;
            m_SessionPos = origin.InverseTransformPoint(worldPos);
            m_SessionYawDeg = Mathf.DeltaAngle(origin.eulerAngles.y, worldYawDeg);
            m_HasSessionPose = true;
        }

        async void CreateAnchorAsync(Vector3 worldPos, float yawDeg, int version)
        {
            var placer = AnchorPlacer.Instance;
            if (placer == null || placer.Fusion == null || !placer.Fusion.IsArTracking ||
                placer.Fusion.CurrentMode != HeadPoseFusion.Mode.SixDof ||
                placer.AnchorManager == null || !placer.AnchorManager.enabled || placer.AnchorManager.subsystem == null)
            {
                return;
            }

            var pose = new Pose(worldPos, OggettiLayout.VerticalAxisRotation(yawDeg));
            var result = await placer.AnchorManager.TryAddAnchorAsync(pose);
            if (this == null || version != m_AnchorRequestVersion)
            {
                if (result.status.IsSuccess() && result.value != null && placer.AnchorManager != null)
                    placer.AnchorManager.TryRemoveAnchor(result.value);
                return;
            }
            if (!result.status.IsSuccess() || result.value == null)
                return;

            ReleaseAnchor();
            m_Anchor = result.value;
            UpdateSessionPoseFromWorld(m_Anchor.transform.position, m_YawDeg);
        }

        void ReleaseAnchor()
        {
            if (m_Anchor == null)
                return;
            var old = m_Anchor;
            m_Anchor = null;
            var placer = AnchorPlacer.Instance;
            if (placer != null && placer.AnchorManager != null && placer.AnchorManager.enabled)
                placer.AnchorManager.TryRemoveAnchor(old);
        }

        void LateUpdate()
        {
            if (!m_IsMoving)
            {
                var placer = AnchorPlacer.Instance;
                var origin = placer != null ? placer.Origin : null;
                var sixDof = placer != null && placer.Fusion != null &&
                             placer.Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;

                if (sixDof && m_Anchor != null && m_Anchor.trackingState == TrackingState.Tracking)
                {
                    var p = m_Anchor.transform.position;
                    transform.SetPositionAndRotation(p, OggettiLayout.VerticalAxisRotation(m_YawDeg));
                    UpdateSessionPoseFromWorld(p, m_YawDeg);
                }
                else if (m_HasSessionPose && origin != null)
                {
                    var p = origin.TransformPoint(m_SessionPos);
                    var yaw = origin.eulerAngles.y + m_SessionYawDeg;
                    m_YawDeg = OggettiLayout.NormalizeYaw(yaw);
                    transform.SetPositionAndRotation(p, OggettiLayout.VerticalAxisRotation(m_YawDeg));
                }
            }

            UpdateControlsVisibility();
        }

        void PositionControls()
        {
            if (m_Controls == null)
                return;
            m_Controls.localPosition = new Vector3(0f, BoundsHeightMeters + 0.14f, 0f);
        }

        void UpdateControlsVisibility()
        {
            if (m_Controls == null)
                return;

            PositionControls();
            var head = m_Host != null ? m_Host.HeadTransform : null;
            if (head != null)
                m_Controls.rotation = M3Theme.FacingRotation(m_Controls.position, head.position, 10f);

            var gaze = m_Host != null ? m_Host.ActiveGaze : null;
            var target = gaze != null ? gaze.Target : null;
            var isGazed = target != null && (target.transform == transform || target.transform.IsChildOf(transform));

            var now = Time.unscaledTime;
            var dt = Time.unscaledDeltaTime;
            if (isGazed || m_IsManipulating)
                m_LastGazeAt = now;

            var wantVisible = !m_IsMoving && (m_IsManipulating || isGazed || now - m_LastGazeAt <= k_GraceSeconds);
            var speed = wantVisible ? k_FadeInSpeed : k_FadeOutSpeed;
            m_ControlsAlpha = Mathf.MoveTowards(m_ControlsAlpha, wantVisible ? 1f : 0f, dt * speed);
            SetControlsGroup(m_ControlsAlpha, m_ControlsAlpha > 0.15f);
        }

        void SetControlsGroup(float alpha, bool interactive)
        {
            if (m_ControlsGroup == null)
                return;
            m_ControlsGroup.alpha = alpha;
            m_ControlsGroup.blocksRaycasts = interactive;
            m_ControlsGroup.interactable = interactive;
        }

        void OnDestroy()
        {
            m_AnchorRequestVersion++;
            ReleaseAnchor();
            if (m_Model != null)
            {
                m_Model.DestroyResources();
                m_Model = null;
            }
        }
    }
}
