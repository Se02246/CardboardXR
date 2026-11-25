using UnityEngine;

namespace CardboardXR.Demos.Oggetti
{
    // Pure layout, scale, and strict vertical-axis rotation math for Poly Haven 3D objects.
    public static class OggettiLayout
    {
        public const int Columns = 4;
        public const int Rows = 3;
        public const int PerPage = Columns * Rows;
        public const int MaxPlacedObjects = 16;
        public const float MinScale = 0.20f;
        public const float MaxScale = 4.0f;
        public const float StandardHeightMeters = 0.65f;
        public const float MaxSpawnDimensionMeters = 0.90f;

        public static float ClampScale(float scale) => Mathf.Clamp(scale, MinScale, MaxScale);

        public static float NormalizeYaw(float yawDeg) => Mathf.Repeat(yawDeg, 360f);

        // Rotates strictly around the vertical up axis (zero pitch or roll tilt).
        public static Quaternion VerticalAxisRotation(float yawDeg) =>
            Quaternion.Euler(0f, NormalizeYaw(yawDeg), 0f);

        // Computes an unclamped base scale so any Sketchfab model (whether authored in mm, cm, or m)
        // spawns at StandardHeightMeters tall without exceeding MaxSpawnDimensionMeters on any axis.
        public static float DefaultNormalizedScale(Bounds localBounds)
        {
            var size = localBounds.size;
            var maxDim = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (maxDim <= 1e-4f)
                return 1f;
            var byHeight = size.y > 1e-4f ? StandardHeightMeters / size.y : float.PositiveInfinity;
            var byMaxDim = MaxSpawnDimensionMeters / maxDim;
            return Mathf.Min(byHeight, byMaxDim);
        }

        public static int PageCount(int totalItems) =>
            Mathf.Max(1, (Mathf.Max(0, totalItems) + PerPage - 1) / PerPage);
    }
}
