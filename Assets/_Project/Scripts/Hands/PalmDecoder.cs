using UnityEngine;

namespace CardboardXR.Hands
{
    // A palm the detector found, in the detector input's normalized coordinates: 0 to 1 across and
    // down the square input.
    public struct Palm
    {
        public float score;
        public Vector2 center;
        public Vector2 size;
        public Vector2 wrist;
        public Vector2 middleKnuckle;
    }

    // Decodes MediaPipe's BlazePalm detector output. Each of 2016 SSD anchors over the 192-pixel
    // input has a score logit and 18 values: the box center and size, then seven keypoints, as
    // offsets in input pixels.
    public static class PalmDecoder
    {
        public const int InputSize = 192;
        public const int AnchorCount = 2016;
        public const int ValuesPerAnchor = 18;
        // Keypoint 0 is the wrist and keypoint 2 the middle finger's knuckle.
        const int k_WristOffset = 4;
        const int k_MiddleKnuckleOffset = 8;

        // MediaPipe's anchor layout for this model: a 24x24 grid (stride 8) with 2 anchors per cell,
        // then a 12x12 grid (stride 16) with 6, each anchor at its cell's center. Anchors all have
        // unit size, so offsets are only scaled by the input size.
        public static Vector2[] CreateAnchors()
        {
            var anchors = new Vector2[AnchorCount];
            var i = 0;
            foreach (var (grid, perCell) in new[] { (24, 2), (12, 6) })
            {
                for (var y = 0; y < grid; y++)
                {
                    for (var x = 0; x < grid; x++)
                    {
                        var center = new Vector2((x + 0.5f) / grid, (y + 0.5f) / grid);
                        for (var a = 0; a < perCell; a++)
                            anchors[i++] = center;
                    }
                }
            }
            return anchors;
        }

        // The highest-scoring palm, if its score reaches minScore. boxes holds AnchorCount *
        // ValuesPerAnchor values and scores AnchorCount logits.
        public static bool TryDecodeBest(float[] boxes, float[] scores, Vector2[] anchors, float minScore, out Palm palm)
        {
            palm = default;
            var best = 0;
            for (var i = 1; i < AnchorCount; i++)
            {
                if (scores[i] > scores[best])
                    best = i;
            }
            var score = Sigmoid(scores[best]);
            if (score < minScore)
                return false;

            palm = DecodeAnchor(boxes, anchors, best, score);
            return true;
        }

        // Prefers the user's closest real palm over small distant hands or background clutter.
        public static bool TryDecodeNearestUserPalm(float[] boxes, float[] scores, Vector2[] anchors,
            float minScore, float minPalmSizeNorm, out Palm palm)
        {
            palm = default;
            var best = -1;
            var bestPriority = float.NegativeInfinity;
            var bestScore = 0f;

            for (var i = 0; i < AnchorCount; i++)
            {
                var score = Sigmoid(scores[i]);
                if (score < minScore)
                    continue;

                var o = i * ValuesPerAnchor;
                var w = Mathf.Abs(boxes[o + 2]) / InputSize;
                var h = Mathf.Abs(boxes[o + 3]) / InputSize;
                var boxSize = Mathf.Max(w, h);
                if (boxSize < minPalmSizeNorm || boxSize > 0.85f)
                    continue;

                var anchor = anchors[i];
                var center = anchor + new Vector2(boxes[o], boxes[o + 1]) / InputSize;
                var wrist = anchor + new Vector2(boxes[o + k_WristOffset], boxes[o + k_WristOffset + 1]) / InputSize;
                var middleKnuckle = anchor + new Vector2(boxes[o + k_MiddleKnuckleOffset], boxes[o + k_MiddleKnuckleOffset + 1]) / InputSize;
                // Other people's arms hanging upside down high in front of the viewer.
                if (wrist.y < middleKnuckle.y - 0.02f && center.y < 0.58f)
                    continue;

                var priority = boxSize * (0.35f + score);
                if (priority > bestPriority)
                {
                    bestPriority = priority;
                    best = i;
                    bestScore = score;
                }
            }

            if (best < 0)
                return false;

            palm = DecodeAnchor(boxes, anchors, best, bestScore);
            return true;
        }

        static Palm DecodeAnchor(float[] boxes, Vector2[] anchors, int index, float score)
        {
            var anchor = anchors[index];
            var o = index * ValuesPerAnchor;
            return new Palm
            {
                score = score,
                center = anchor + new Vector2(boxes[o], boxes[o + 1]) / InputSize,
                size = new Vector2(boxes[o + 2], boxes[o + 3]) / InputSize,
                wrist = anchor + new Vector2(boxes[o + k_WristOffset], boxes[o + k_WristOffset + 1]) / InputSize,
                middleKnuckle = anchor + new Vector2(boxes[o + k_MiddleKnuckleOffset], boxes[o + k_MiddleKnuckleOffset + 1]) / InputSize,
            };
        }

        public static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
    }
}
