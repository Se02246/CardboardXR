using System;
using System.Globalization;
using System.Text;

namespace CardboardXR.Stereo
{
    // Pure C# Radiance RGBE (.hdr) decoder with adaptive RLE support, log-average auto-exposure,
    // and ACES filmic tonemapping to sRGB RGB24 (bottom-up for Unity Texture2D.LoadRawTextureData).
    // Thread-safe: uses no UnityEngine APIs so it can execute on a background thread.
    public static class RadianceHdrDecoder
    {
        public static bool TryDecodeToRgb24(byte[] hdrBytes, out int width, out int height,
            out byte[] rgb24BottomUp, out string error)
        {
            width = 0;
            height = 0;
            rgb24BottomUp = null;
            error = null;

            if (hdrBytes == null || hdrBytes.Length < 16)
            {
                error = "HDR buffer is empty or too short";
                return false;
            }

            var pos = 0;
            var magic = ReadAsciiLine(hdrBytes, ref pos);
            if (!magic.StartsWith("#?RADIANCE", StringComparison.Ordinal) &&
                !magic.StartsWith("#?RGBE", StringComparison.Ordinal))
            {
                error = "Invalid Radiance HDR header";
                return false;
            }

            // Read header lines until empty line.
            while (pos < hdrBytes.Length)
            {
                var line = ReadAsciiLine(hdrBytes, ref pos);
                if (string.IsNullOrEmpty(line))
                    break;
            }

            if (pos >= hdrBytes.Length)
            {
                error = "Unexpected EOF in HDR header";
                return false;
            }

            var resLine = ReadAsciiLine(hdrBytes, ref pos);
            if (!TryParseResolutionLine(resLine, out width, out height, out var topDown))
            {
                error = $"Unsupported HDR resolution line: {resLine}";
                return false;
            }

            if (width <= 0 || height <= 0 || (long)width * height > 4096L * 4096L)
            {
                error = $"HDR dimensions out of range: {width}x{height}";
                return false;
            }

            var pixelCount = width * height;
            var linearRgb = new float[pixelCount * 3];
            var scanline = new byte[width * 4];

            for (var y = 0; y < height; y++)
            {
                if (!ReadScanline(hdrBytes, ref pos, width, scanline))
                {
                    error = $"Truncated or corrupt HDR scanline {y}/{height}";
                    return false;
                }

                var outRow = topDown ? (height - 1 - y) : y;
                var rowOffset = outRow * width * 3;
                for (var x = 0; x < width; x++)
                {
                    var r = scanline[x * 4];
                    var g = scanline[x * 4 + 1];
                    var b = scanline[x * 4 + 2];
                    var e = scanline[x * 4 + 3];
                    var idx = rowOffset + x * 3;
                    if (e == 0)
                    {
                        linearRgb[idx] = 0f;
                        linearRgb[idx + 1] = 0f;
                        linearRgb[idx + 2] = 0f;
                    }
                    else
                    {
                        var scale = MathF.Pow(2f, e - 136);
                        linearRgb[idx] = r * scale;
                        linearRgb[idx + 1] = g * scale;
                        linearRgb[idx + 2] = b * scale;
                    }
                }
            }

            var exposure = ComputeAutoExposure(linearRgb, pixelCount);
            rgb24BottomUp = new byte[pixelCount * 3];
            for (var i = 0; i < pixelCount; i++)
            {
                var idx = i * 3;
                rgb24BottomUp[idx] = LinearToSrgbByte(AcesFilmic(linearRgb[idx] * exposure));
                rgb24BottomUp[idx + 1] = LinearToSrgbByte(AcesFilmic(linearRgb[idx + 1] * exposure));
                rgb24BottomUp[idx + 2] = LinearToSrgbByte(AcesFilmic(linearRgb[idx + 2] * exposure));
            }

            return true;
        }

        static float ComputeAutoExposure(float[] linearRgb, int pixelCount)
        {
            var stride = Math.Max(1, pixelCount / 16384);
            var sumLog = 0.0;
            var samples = 0;
            for (var i = 0; i < pixelCount; i += stride)
            {
                var idx = i * 3;
                var lum = 0.2126f * linearRgb[idx] + 0.7152f * linearRgb[idx + 1] + 0.0722f * linearRgb[idx + 2];
                // Cap bright sun hotspots in the exposure average so the sky doesn't crush shadows.
                var clampedLum = Math.Clamp((double)lum, 1e-4, 24.0);
                sumLog += Math.Log(clampedLum);
                samples++;
            }

            if (samples == 0)
                return 1f;
            var logAvg = Math.Exp(sumLog / samples);
            var exposure = (float)(0.24 / Math.Max(logAvg, 1e-3));
            return Math.Clamp(exposure, 0.15f, 4.5f);
        }

        static float AcesFilmic(float x)
        {
            if (x <= 0f)
                return 0f;
            const float a = 2.51f;
            const float b = 0.03f;
            const float c = 2.43f;
            const float d = 0.59f;
            const float e = 0.14f;
            var v = (x * (a * x + b)) / (x * (c * x + d) + e);
            return Math.Clamp(v, 0f, 1f);
        }

        static byte LinearToSrgbByte(float linear)
        {
            var srgb = linear <= 0.0031308f
                ? 12.92f * linear
                : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            var clamped = Math.Clamp(srgb * 255f + 0.5f, 0f, 255f);
            return (byte)clamped;
        }

        static bool ReadScanline(byte[] data, ref int pos, int width, byte[] outRgbe)
        {
            if (pos + 4 > data.Length)
                return false;

            var b0 = data[pos];
            var b1 = data[pos + 1];
            var b2 = data[pos + 2];
            var b3 = data[pos + 3];

            // New-style adaptive RLE scanline header: [2, 2, widthHigh, widthLow]
            if (b0 == 2 && b1 == 2 && (b2 & 0x80) == 0 && width >= 8 && width <= 32767)
            {
                var scanWidth = (b2 << 8) | b3;
                if (scanWidth == width)
                {
                    pos += 4;
                    for (var ch = 0; ch < 4; ch++)
                    {
                        var x = 0;
                        while (x < width)
                        {
                            if (pos >= data.Length)
                                return false;
                            var count = data[pos++];
                            if (count > 128)
                            {
                                var run = count - 128;
                                if (pos >= data.Length || x + run > width)
                                    return false;
                                var val = data[pos++];
                                for (var i = 0; i < run; i++)
                                    outRgbe[(x + i) * 4 + ch] = val;
                                x += run;
                            }
                            else
                            {
                                if (count == 0 || pos + count > data.Length || x + count > width)
                                    return false;
                                for (var i = 0; i < count; i++)
                                    outRgbe[(x + i) * 4 + ch] = data[pos++];
                                x += count;
                            }
                        }
                    }
                    return true;
                }
            }

            // Old-style uncompressed RGBE scanline
            var needed = width * 4;
            if (pos + needed > data.Length)
                return false;
            Buffer.BlockCopy(data, pos, outRgbe, 0, needed);
            pos += needed;
            return true;
        }

        static string ReadAsciiLine(byte[] data, ref int pos)
        {
            var start = pos;
            while (pos < data.Length && data[pos] != (byte)'\n')
                pos++;
            var len = pos - start;
            if (len > 0 && data[start + len - 1] == (byte)'\r')
                len--;
            if (pos < data.Length && data[pos] == (byte)'\n')
                pos++;
            return Encoding.ASCII.GetString(data, start, len);
        }

        static bool TryParseResolutionLine(string line, out int width, out int height, out bool topDown)
        {
            width = 0;
            height = 0;
            topDown = true;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                return false;

            if (parts[0] != "-Y" && parts[0] != "+Y")
                return false;
            if (parts[2] != "+X")
                return false;

            topDown = parts[0] == "-Y";
            return int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) &&
                   int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
        }
    }
}
