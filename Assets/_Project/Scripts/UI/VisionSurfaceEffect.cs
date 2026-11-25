using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    public enum VisionSurfaceStyle
    {
        Frosted = 0,
        Recessed = 1,
        Glossy = 2,
        Pure = 3,
        OutlineRing = 4,
        CornerArc = 5,
    }

    // Packs analytical rect size (in dp), normalized UVs, corner radius, and visionOS glass style
    // parameters into TEXCOORD1/TEXCOORD2 on any uGUI Graphic so VisionGlass and RoundedScreen
    // shaders render razor-sharp anti-aliased corners and specular rims with zero 9-slice seams.
    [ExecuteAlways]
    [RequireComponent(typeof(Graphic))]
    [DisallowMultipleComponent]
    public class VisionSurfaceEffect : BaseMeshEffect
    {
        [SerializeField] float m_Radius = 18f;
        [SerializeField] float m_RimStrength = 0.85f;
        [SerializeField] float m_SheenStrength = 0.85f;
        [SerializeField] VisionSurfaceStyle m_Style = VisionSurfaceStyle.Frosted;

        public float Radius
        {
            get => m_Radius;
            set
            {
                if (Mathf.Approximately(m_Radius, value))
                    return;
                m_Radius = value;
                if (graphic != null)
                    graphic.SetVerticesDirty();
            }
        }

        public float RimStrength
        {
            get => m_RimStrength;
            set
            {
                if (Mathf.Approximately(m_RimStrength, value))
                    return;
                m_RimStrength = value;
                if (graphic != null)
                    graphic.SetVerticesDirty();
            }
        }

        public float SheenStrength
        {
            get => m_SheenStrength;
            set
            {
                if (Mathf.Approximately(m_SheenStrength, value))
                    return;
                m_SheenStrength = value;
                if (graphic != null)
                    graphic.SetVerticesDirty();
            }
        }

        public VisionSurfaceStyle Style
        {
            get => m_Style;
            set
            {
                if (m_Style == value)
                    return;
                m_Style = value;
                if (graphic != null)
                    graphic.SetVerticesDirty();
            }
        }

        public void Configure(float radius, VisionSurfaceStyle style, float rimStrength, float sheenStrength)
        {
            m_Radius = radius;
            m_Style = style;
            m_RimStrength = rimStrength;
            m_SheenStrength = sheenStrength;
            EnsureCanvasChannels();
            if (graphic != null)
                graphic.SetVerticesDirty();
        }

        public static float ClampRadius(float radius, Vector2 size) =>
            size.x <= 0f || size.y <= 0f
                ? Mathf.Max(0f, radius)
                : Mathf.Clamp(radius, 0f, Mathf.Min(size.x, size.y) * 0.5f);

        public static Vector4 PackParams(float radius, VisionSurfaceStyle style, float rimStrength, float sheenStrength) =>
            new(radius, Mathf.Max(0f, rimStrength), Mathf.Max(0f, sheenStrength), (int)style);

        protected override void OnEnable()
        {
            base.OnEnable();
            EnsureCanvasChannels();
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            EnsureCanvasChannels();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            if (graphic != null)
                graphic.SetVerticesDirty();
        }

        void EnsureCanvasChannels()
        {
            if (graphic == null)
                return;
            var canvas = graphic.canvas;
            if (canvas == null)
                return;
            const AdditionalCanvasShaderChannels needed =
                AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
            if ((canvas.additionalShaderChannels & needed) != needed)
                canvas.additionalShaderChannels |= needed;
            var rootCanvas = canvas.rootCanvas;
            if (rootCanvas != null && (rootCanvas.additionalShaderChannels & needed) != needed)
                rootCanvas.additionalShaderChannels |= needed;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh == null || vh.currentVertCount == 0 || graphic == null)
                return;

            var rt = graphic.rectTransform;
            var rect = rt.rect;
            var width = rect.width;
            var height = rect.height;
            var xMin = rect.xMin;
            var yMin = rect.yMin;
            if (width <= 0.01f || height <= 0.01f)
            {
                var sd = rt.sizeDelta;
                width = sd.x;
                height = sd.y;
                xMin = -width * rt.pivot.x;
                yMin = -height * rt.pivot.y;
            }
            if (width <= 0.01f || height <= 0.01f)
                return;

            var clampedRadius = ClampRadius(m_Radius, new Vector2(width, height));
            var packed = PackParams(clampedRadius, m_Style, m_RimStrength, m_SheenStrength);
            var invW = 1f / width;
            var invH = 1f / height;

            var vert = default(UIVertex);
            var count = vh.currentVertCount;
            for (var i = 0; i < count; i++)
            {
                vh.PopulateUIVertex(ref vert, i);
                var nx = (vert.position.x - xMin) * invW;
                var ny = (vert.position.y - yMin) * invH;
                vert.uv1 = new Vector4(nx, ny, width, height);
                vert.uv2 = packed;
                vh.SetUIVertex(vert, i);
            }
        }
    }
}
