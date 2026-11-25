using UnityEngine;

namespace CardboardXR.Demos.Quadri
{
    // Pure layout math for Quadri gallery thumbnails, pagination, and aspect-preserving photo windows.
    public static class QuadriLayout
    {
        public const int Columns = 4;
        public const int Rows = 3;
        public const int PerPage = Columns * Rows;
        public const int MaxPlacedFrames = 12;
        public const float MaxFrameWidthDp = 600f;
        public const float MaxFrameHeightDp = 580f;

        public static Vector2 ComputeWindowSize(int imageWidth, int imageHeight)
        {
            var aspect = imageWidth > 0 && imageHeight > 0
                ? Mathf.Clamp((float)imageWidth / imageHeight, 0.42f, 2.4f)
                : 4f / 3f;

            var width = MaxFrameWidthDp;
            var height = width / aspect;
            if (height > MaxFrameHeightDp)
            {
                height = MaxFrameHeightDp;
                width = height * aspect;
            }
            return new Vector2(Mathf.Round(width), Mathf.Round(height));
        }

        public static Rect ComputeCenterCropUv(int texWidth, int texHeight, float targetAspect = 1f)
        {
            if (texWidth <= 0 || texHeight <= 0 || targetAspect <= 0f)
                return new Rect(0f, 0f, 1f, 1f);
            var texAspect = (float)texWidth / texHeight;
            if (texAspect > targetAspect)
            {
                var w = targetAspect / texAspect;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            var h = texAspect / targetAspect;
            return new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }

        public static int PageCount(int totalItems) =>
            Mathf.Max(1, (Mathf.Max(0, totalItems) + PerPage - 1) / PerPage);
    }
}
