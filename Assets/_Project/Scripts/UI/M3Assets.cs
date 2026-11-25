using TMPro;
using UnityEngine;

namespace CardboardXR.UI
{
    // The fonts the Material UI is set in, as TextMesh Pro font assets: Roboto in two weights and
    // Material Icons (Round). Kept in Resources so UI built at runtime in any scene finds them; the
    // editor's MaterialAssets step makes it.
    public class M3Assets : ScriptableObject
    {
        public const string k_ResourcePath = "M3Assets";
        public const string k_VisionGlassShader = "CardboardXR/VisionGlass";
        public const string k_RoundedScreenShader = "CardboardXR/RoundedScreen";

        [SerializeField] TMP_FontAsset m_Regular;
        [SerializeField] TMP_FontAsset m_Medium;
        [SerializeField] TMP_FontAsset m_Icons;
        [SerializeField] Sprite m_Round;
        [SerializeField] Material m_GlassMaterial;
        [SerializeField] Material m_RoundedScreenMaterial;

        static M3Assets s_Instance;
        static Material s_FallbackGlass;
        static Material s_FallbackRoundedScreen;

        public static M3Assets Instance => s_Instance != null ? s_Instance : s_Instance = Resources.Load<M3Assets>(k_ResourcePath);

        public TMP_FontAsset Regular => m_Regular;
        public TMP_FontAsset Medium => m_Medium;
        public TMP_FontAsset Icons => m_Icons;
        // M3Shapes' rounded sprite, saved so UI built in the editor keeps it.
        public Sprite Round => m_Round;
        public Material GlassMaterial => m_GlassMaterial;
        public Material RoundedScreenMaterial => m_RoundedScreenMaterial;

        public void Set(TMP_FontAsset regular, TMP_FontAsset medium, TMP_FontAsset icons, Sprite round)
        {
            m_Regular = regular;
            m_Medium = medium;
            m_Icons = icons;
            m_Round = round;
        }

        public void SetMaterials(Material glass, Material roundedScreen)
        {
            m_GlassMaterial = glass;
            m_RoundedScreenMaterial = roundedScreen;
        }

        public static Material GetGlassMaterial()
        {
            var instance = Instance;
            if (instance != null && instance.m_GlassMaterial != null)
                return instance.m_GlassMaterial;
            if (s_FallbackGlass != null)
                return s_FallbackGlass;
            var shader = Shader.Find(k_VisionGlassShader);
            if (shader == null)
                return null;
            s_FallbackGlass = new Material(shader)
            {
                name = "Vision Glass (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            return s_FallbackGlass;
        }

        public static Material GetRoundedScreenMaterial()
        {
            var instance = Instance;
            if (instance != null && instance.m_RoundedScreenMaterial != null)
                return instance.m_RoundedScreenMaterial;
            if (s_FallbackRoundedScreen != null)
                return s_FallbackRoundedScreen;
            var shader = Shader.Find(k_RoundedScreenShader);
            if (shader == null)
                return null;
            s_FallbackRoundedScreen = new Material(shader)
            {
                name = "Rounded Screen (Runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            return s_FallbackRoundedScreen;
        }
    }
}
