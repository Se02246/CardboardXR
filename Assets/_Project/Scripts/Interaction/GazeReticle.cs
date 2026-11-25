using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Interaction
{
    // Cardboard's gaze reticle: a dot that grows into a ring over something clickable.
    // Smoothly eases its depth within the natural UI range (never jumping 20 m -> 1 m in a frame),
    // uses short interactive hysteresis so button edges never flicker, and updates its own material
    // instance directly for clean Unity 6 URP rendering without MaterialPropertyBlock artifacts.
    [RequireComponent(typeof(MeshRenderer))]
    public class GazeReticle : MonoBehaviour
    {
        static readonly int k_Inner = Shader.PropertyToID("_Inner");
        static readonly int k_Outer = Shader.PropertyToID("_Outer");
        static readonly int k_Outline = Shader.PropertyToID("_Outline");
        static readonly int k_Color = Shader.PropertyToID("_Color");

        const float k_MaxReticleDistance = 2.2f;
        const float k_DistanceSmoothRate = 18f;
        const float k_InteractiveGraceSeconds = 0.08f;

        [Tooltip("Diameter of the dot, in degrees.")]
        [SerializeField] float m_DotDegrees = 0.5f;
        [Tooltip("How much the inner and outer diameters grow over something clickable, in degrees.")]
        [SerializeField] float m_GrowthDegrees = 1.5f;
        [SerializeField] float m_GrowthSpeed = 10f;
        [Tooltip("Width of the dark edge that keeps the reticle visible on light surfaces, in degrees.")]
        [SerializeField] float m_OutlineDegrees = 0.1f;
        [Tooltip("Closer than this, the eyes can't comfortably converge on the reticle.")]
        [SerializeField] float m_MinDistance = 0.22f;
        [SerializeField] Color m_Color = new(1f, 1f, 1f, 0.9f);
        [Tooltip("Color of the dot when hovering over something interactive/clickable.")]
        [SerializeField] Color m_InteractiveColor = new(0f, 0.92f, 1f, 0.98f);
        [Tooltip("While the camera sees a hand, so a pinch will click.")]
        [SerializeField] Color m_HandColor = new(0.45f, 0.85f, 1f, 0.95f);
        [Tooltip("While moving a grabbed window or object with the Pixel Watch.")]
        [SerializeField] Color m_GrabColor = new(1f, 0.72f, 0.22f, 0.98f);

        MeshRenderer m_Renderer;
        Material m_Material;
        float m_TargetDistance = 1.85f;
        float m_Distance = 1.85f;
        bool m_Interactive;
        float m_LastInteractiveAt = float.NegativeInfinity;
        Vector3 m_LocalDirection = Vector3.forward;
        // 0 is the dot, 1 the full ring.
        float m_Growth;

        // Whether a hand is in the camera's view, ready to pinch: the reticle turns light blue, since
        // otherwise nothing says whether a pinch will register.
        public bool HandSeen { get; set; }

        // Whether the user is currently moving a grabbed window/object with the watch gyroscope.
        public bool Grabbing { get; set; }

        public void SetTarget(float distance, bool interactive, Vector3? localDirection = null)
        {
            m_TargetDistance = Mathf.Clamp(distance - 0.002f, m_MinDistance, k_MaxReticleDistance);
            m_Interactive = interactive;
            if (interactive)
                m_LastInteractiveAt = Time.unscaledTime;
            m_LocalDirection = localDirection.HasValue && localDirection.Value.sqrMagnitude > 1e-6f
                ? localDirection.Value.normalized
                : Vector3.forward;
        }

        void Awake()
        {
            gameObject.layer = StereoRig.FullscreenOverlayLayer;
            m_Renderer = GetComponent<MeshRenderer>();
            m_Renderer.sortingOrder = UiRenderQueue.k_ReticleSortingOrder;
            m_Material = Application.isPlaying ? m_Renderer.material : m_Renderer.sharedMaterial;
            if (m_Material != null && Application.isPlaying)
                m_Material.renderQueue = (int)RenderQueue.Overlay;
        }

        void OnDestroy()
        {
            if (Application.isPlaying && m_Material != null)
                Destroy(m_Material);
        }

        public static Color ResolveDotColor(bool grabbing, float interactiveBlend, Color idleColor, Color interactiveColor, Color grabColor)
        {
            if (grabbing)
                return grabColor;
            return Color.Lerp(idleColor, interactiveColor, Mathf.Clamp01(interactiveBlend));
        }

        void LateUpdate()
        {
            var dt = Time.unscaledDeltaTime;
            var wantInteractive = m_Interactive || (Time.unscaledTime - m_LastInteractiveAt <= k_InteractiveGraceSeconds);
            m_Growth = Mathf.Lerp(m_Growth, wantInteractive ? 1f : 0f, 1f - Mathf.Exp(-dt * m_GrowthSpeed));
            m_Distance = Mathf.Lerp(m_Distance, m_TargetDistance, 1f - Mathf.Exp(-dt * k_DistanceSmoothRate));

            // Always a clean single dot (never expands into a ring that clutters video/content).
            var outer = RadiusForDiameter(m_DotDegrees);
            var outline = Mathf.Tan(m_OutlineDegrees * Mathf.Deg2Rad);
            var quadRadius = outer + outline;

            transform.localPosition = m_LocalDirection * m_Distance;
            transform.localRotation = Quaternion.FromToRotation(Vector3.forward, m_LocalDirection);
            var size = 2f * quadRadius * m_Distance;
            transform.localScale = new Vector3(size, size, 1f);

            if (m_Material != null)
            {
                m_Material.SetFloat(k_Inner, 0f);
                m_Material.SetFloat(k_Outer, outer / quadRadius);
                m_Material.SetFloat(k_Outline, outline / quadRadius);
                m_Material.SetColor(k_Color, ResolveDotColor(Grabbing, m_Growth, m_Color, m_InteractiveColor, m_GrabColor));
            }
        }

        static float RadiusForDiameter(float degrees) => Mathf.Tan(degrees * 0.5f * Mathf.Deg2Rad);
    }
}
