using System;
using Unity.Collections;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Fills the models' NHWC float inputs (RGB, 0 to 1) from an RGBA32 camera image whose first row
    // is the top of the image, with bilinear filtering. Pixels outside the image are black.
    public static class ImageSampler
    {
        const float k_Inv255 = 1f / 255f;

        // The whole image, fitted into the square detector input.
        public static void SampleLetterbox(NativeArray<byte> rgba, int width, int height, Letterbox letterbox, float[] output)
        {
            var span = rgba.AsReadOnlySpan();
            var size = letterbox.inputSize;
            var invScale = 1f / letterbox.scale;
            var startX = (0.5f - letterbox.offset.x) * invScale - 0.5f;
            var startY = (0.5f - letterbox.offset.y) * invScale - 0.5f;
            var maxX = width - 1;
            var maxY = height - 1;
            var rowBytes = width * 4;
            var i = 0;

            for (var y = 0; y < size; y++)
            {
                var fy = startY + y * invScale;
                var y0 = fy >= 0f ? (int)fy : (int)Mathf.Floor(fy);
                var ty = fy - y0;
                var ity = 1f - ty;
                var yInside = y0 >= 0 && y0 < maxY;
                var y0Row = y0 * rowBytes;
                var y1Row = y0Row + rowBytes;

                for (var x = 0; x < size; x++)
                {
                    var fx = startX + x * invScale;
                    var x0 = fx >= 0f ? (int)fx : (int)Mathf.Floor(fx);
                    var tx = fx - x0;
                    var itx = 1f - tx;

                    if (yInside && x0 >= 0 && x0 < maxX)
                    {
                        var w00 = itx * ity;
                        var w10 = tx * ity;
                        var w01 = itx * ty;
                        var w11 = tx * ty;
                        var p00 = y0Row + x0 * 4;
                        var p10 = p00 + 4;
                        var p01 = y1Row + x0 * 4;
                        var p11 = p01 + 4;
                        output[i++] = (span[p00] * w00 + span[p10] * w10 + span[p01] * w01 + span[p11] * w11) * k_Inv255;
                        output[i++] = (span[p00 + 1] * w00 + span[p10 + 1] * w10 + span[p01 + 1] * w01 + span[p11 + 1] * w11) * k_Inv255;
                        output[i++] = (span[p00 + 2] * w00 + span[p10 + 2] * w10 + span[p01 + 2] * w01 + span[p11 + 2] * w11) * k_Inv255;
                    }
                    else
                    {
                        SampleBorder(span, width, height, x0, y0, tx, ty, output, ref i);
                    }
                }
            }
        }

        // The region, upright, into a size x size input.
        public static void SampleRegion(NativeArray<byte> rgba, int width, int height, HandRegion region, int size, float[] output)
        {
            var span = rgba.AsReadOnlySpan();
            var first = region.CropToImage(new Vector2(0.5f / size, 0.5f / size));
            var stepX = region.CropToImage(new Vector2(1.5f / size, 0.5f / size)) - first;
            var stepY = region.CropToImage(new Vector2(0.5f / size, 1.5f / size)) - first;
            var firstX = first.x - 0.5f;
            var firstY = first.y - 0.5f;
            var stepXx = stepX.x;
            var stepXy = stepX.y;
            var stepYx = stepY.x;
            var stepYy = stepY.y;
            var maxX = width - 1;
            var maxY = height - 1;
            var rowBytes = width * 4;
            var i = 0;

            for (var y = 0; y < size; y++)
            {
                var fx = firstX + stepYx * y;
                var fy = firstY + stepYy * y;
                for (var x = 0; x < size; x++)
                {
                    var x0 = fx >= 0f ? (int)fx : (int)Mathf.Floor(fx);
                    var y0 = fy >= 0f ? (int)fy : (int)Mathf.Floor(fy);
                    var tx = fx - x0;
                    var ty = fy - y0;

                    if (x0 >= 0 && x0 < maxX && y0 >= 0 && y0 < maxY)
                    {
                        var itx = 1f - tx;
                        var ity = 1f - ty;
                        var w00 = itx * ity;
                        var w10 = tx * ity;
                        var w01 = itx * ty;
                        var w11 = tx * ty;
                        var p00 = y0 * rowBytes + x0 * 4;
                        var p10 = p00 + 4;
                        var p01 = p00 + rowBytes;
                        var p11 = p01 + 4;
                        output[i++] = (span[p00] * w00 + span[p10] * w10 + span[p01] * w01 + span[p11] * w11) * k_Inv255;
                        output[i++] = (span[p00 + 1] * w00 + span[p10 + 1] * w10 + span[p01 + 1] * w01 + span[p11 + 1] * w11) * k_Inv255;
                        output[i++] = (span[p00 + 2] * w00 + span[p10 + 2] * w10 + span[p01 + 2] * w01 + span[p11 + 2] * w11) * k_Inv255;
                    }
                    else
                    {
                        SampleBorder(span, width, height, x0, y0, tx, ty, output, ref i);
                    }

                    fx += stepXx;
                    fy += stepXy;
                }
            }
        }

        static void SampleBorder(ReadOnlySpan<byte> span, int width, int height, int x0, int y0,
            float tx, float ty, float[] output, ref int i)
        {
            var r = 0f;
            var g = 0f;
            var b = 0f;
            var itx = 1f - tx;
            var ity = 1f - ty;
            Accum(span, width, height, x0, y0, itx * ity, ref r, ref g, ref b);
            Accum(span, width, height, x0 + 1, y0, tx * ity, ref r, ref g, ref b);
            Accum(span, width, height, x0, y0 + 1, itx * ty, ref r, ref g, ref b);
            Accum(span, width, height, x0 + 1, y0 + 1, tx * ty, ref r, ref g, ref b);
            output[i++] = r * k_Inv255;
            output[i++] = g * k_Inv255;
            output[i++] = b * k_Inv255;
        }

        static void Accum(ReadOnlySpan<byte> span, int width, int height, int x, int y, float weight,
            ref float r, ref float g, ref float b)
        {
            if (weight <= 0f || (uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            var p = (y * width + x) * 4;
            r += span[p] * weight;
            g += span[p + 1] * weight;
            b += span[p + 2] * weight;
        }
    }
}
