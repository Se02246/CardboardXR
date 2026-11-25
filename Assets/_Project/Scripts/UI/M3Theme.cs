using UnityEngine;

namespace CardboardXR.UI
{
    // Material 3's design tokens as the app uses them, following Material Design for XR
    // (developer.android.com/design/ui/xr): the baseline dark color scheme, the type scale, shapes,
    // and how big a dp is in the world.
    //   Android XR sizes UI in dp, at 0.868 dmm each; a dmm is a millimeter seen from a meter away, so
    //   1 dp at distance d is 0.868 d mm. A phone in a Cardboard viewer has about half a headset's
    //   pixels per degree, so everything is CardboardScale times bigger than that: 14 dp text, XR's
    //   smallest, would otherwise be about 9 pixels tall per eye. Proportions are untouched.
    // World-space canvases here use one canvas unit per dp (see M3Canvas), so sizes in code are dp.
    public static class M3Theme
    {
        public const float k_DpToDmm = 0.868f;
        public const float k_DefaultCardboardScale = 1.6f;

        // Set at startup from the "uiScale" launch option (percent); see UiScaleOption.
        public static float CardboardScale { get; set; } = k_DefaultCardboardScale;

        // Where Android XR puts a panel: 1.75 m away, its middle 5 degrees below eye level.
        public const float k_PanelDistance = 1.75f;
        public const float k_PanelPitch = -5f;
        // Nearer than that, for the app's own overlay (the settings panel), so it's always in front of
        // a scene's panels rather than cutting through them. Sizes are angular, so it looks the same.
        public const float k_OverlayDistance = 1.25f;

        public static float DpToMeters(float dp, float distance) => dp * k_DpToDmm * 1e-3f * distance * CardboardScale;

        // Apple Vision Pro (visionOS) translucent smoked-glass palette (analytical SDF shader, zero 9-slice seams).
        public static readonly Color Primary = Hex(0xF2F7FF, 0.94f);
        public static readonly Color OnPrimary = Hex(0x0A101D);
        public static readonly Color PrimaryContainer = Hex(0x1D3B63, 0.62f);
        public static readonly Color OnPrimaryContainer = Hex(0xE8F4FF);
        public static readonly Color SecondaryContainer = Hex(0x30394D, 0.54f);
        public static readonly Color OnSecondaryContainer = Hex(0xFFFFFF);
        public static readonly Color Surface = Hex(0x0A0D14);
        public static readonly Color SurfaceContainer = Hex(0x1E2330, 0.58f);
        public static readonly Color SurfaceContainerHigh = Hex(0x282F40, 0.70f);
        public static readonly Color SurfaceContainerHighest = Hex(0x363F54, 0.62f);
        public static readonly Color OnSurface = Hex(0xFFFFFF);
        public static readonly Color OnSurfaceVariant = Hex(0xC6D0E0);
        public static readonly Color Outline = Hex(0xFFFFFF, 0.34f);
        public static readonly Color OutlineVariant = Hex(0xFFFFFF, 0.16f);
        public static readonly Color Error = Hex(0xFF6961);
        public static readonly Color InverseSurface = Hex(0xF5F8FC, 0.96f);
        public static readonly Color InverseOnSurface = Hex(0x0C111C);

        // Additional visionOS glass semantic tokens.
        public static readonly Color GlassRecessed = Hex(0x0F131D, 0.44f);
        public static readonly Color GlassAccent = Hex(0x5AC8FA, 0.95f);
        public static readonly Color StarGold = Hex(0xFFD60A, 0.96f);
        public static readonly Color GlassRestingBar = Hex(0xFFFFFF, 0.58f);

        // State layers: luminous visionOS specular hover lift over translucent glass.
        public const float k_HoverOpacity = 0.22f;
        public const float k_PressedOpacity = 0.32f;
        // Disabled: content at 38%, container at 12% of on-surface.
        public const float k_DisabledContent = 0.38f;
        public const float k_DisabledContainer = 0.12f;

        // Corner radii, in dp. Full rounds a shape's shorter side into a circle or capsule.
        public const float k_ShapeFull = 1000f;
        public const float k_ShapePanel = 42f;
        public const float k_ShapeDialog = 30f;
        public const float k_ShapeLarge = 24f;
        public const float k_ShapeCard = 20f;
        public const float k_ShapeSmall = 16f;

        // Spatial elevation, in dp toward the viewer.
        public const float k_ElevationOrbiter = 16f;
        public const float k_ElevationPopup = 32f;
        public const float k_ElevationDialog = 56f;

        // Targets: 56 dp, 8 dp apart.
        public const float k_Target = 56f;
        public const float k_TargetGap = 8f;

        public enum Type
        {
            HeadlineSmall,
            TitleLarge,
            TitleMedium,
            BodyLarge,
            BodyMedium,
            LabelLarge,
        }

        // Size in dp, and whether it's set in the medium weight. Nothing under 14 dp, XR's minimum.
        public static (float size, bool medium) Style(Type type) => type switch
        {
            Type.HeadlineSmall => (24f, false),
            Type.TitleLarge => (22f, false),
            Type.TitleMedium => (16f, true),
            Type.BodyLarge => (16f, false),
            Type.BodyMedium => (14f, false),
            _ => (14f, true),
        };

        // WCAG contrast ratio between two colors, for checking pairs of roles.
        public static float Contrast(Color a, Color b)
        {
            var la = Luminance(a);
            var lb = Luminance(b);
            return (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
        }

        static float Luminance(Color c)
        {
            static float Channel(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        public static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

        // Apple visionOS-style anchored window orientation: strictly level (0 deg roll, upright relative to
        // gravity), facing the viewer horizontally with a gentle ergonomic tilt toward the eyes.
        public static Quaternion FacingRotation(Vector3 panelPosition, Vector3 headPosition, float maxTiltDeg = 14f)
        {
            var offset = panelPosition - headPosition;
            var dist = offset.magnitude;
            if (dist < 1e-4f)
                return Quaternion.identity;
            var yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            var elevDeg = Mathf.Asin(Mathf.Clamp(offset.y / dist, -0.95f, 0.95f)) * Mathf.Rad2Deg;
            var pitch = -Mathf.Clamp(elevDeg * 0.45f, -maxTiltDeg, maxTiltDeg);
            return Quaternion.Euler(pitch, yaw, 0f);
        }

        // Full spherical facing for floating follow-me windows: tangent to a sphere around the viewer's
        // eyes (0 deg roll) so windows placed high look down at you and windows placed low look up at you.
        public static Quaternion SphericalFacingRotation(Vector3 panelPosition, Vector3 headPosition)
        {
            var offset = panelPosition - headPosition;
            var dist = offset.magnitude;
            if (dist < 1e-4f)
                return Quaternion.identity;
            var yaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            var elevDeg = Mathf.Asin(Mathf.Clamp(offset.y / dist, -0.96f, 0.96f)) * Mathf.Rad2Deg;
            return Quaternion.Euler(-elevDeg, yaw, 0f);
        }

        static Color Hex(int rgb, float alpha = 1f) =>
            new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    }
}
