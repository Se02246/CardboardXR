using System.Text.RegularExpressions;
using CardboardXR.Demos.Video;
using UnityEngine;

namespace CardboardXR.Demos.Browser
{
    // Pure layout, coordinate, and VR-video classification rules for Web Cinema browser windows.
    public static class BrowserRules
    {
        static readonly Regex s_VrPrimaryToken =
            new(@"(?:^|[^a-z0-9])(360°?|vr180|180°|equirectangular)(?:$|[^a-z0-9])", RegexOptions.Compiled);
        static readonly Regex s_3dToken =
            new(@"(?:^|[^a-z0-9])3d(?:$|[^a-z0-9])", RegexOptions.Compiled);
        static readonly Regex s_3dPairToken =
            new(@"(?:^|[^a-z0-9])(vr|sbs|180|360)(?:$|[^a-z0-9])", RegexOptions.Compiled);

        public readonly struct Shortcut
        {
            public readonly string Label;
            public readonly string Url;

            public Shortcut(string label, string url)
            {
                Label = label;
                Url = url;
            }
        }

        public static readonly Shortcut[] Shortcuts =
        {
            new("YouTube", "https://www.youtube.com"),
            new("Netflix", "https://www.netflix.com/browse"),
            new("Prime Video", "https://www.primevideo.com"),
            new("Disney+", "https://www.disneyplus.com"),
            new("Twitch", "https://www.twitch.tv"),
            new("Plex", "https://app.plex.tv/desktop"),
            new("Cerca", "https://www.google.com"),
        };

        // Converts a local point on a centered RectTransform into normalized web viewport coordinates
        // (u: 0 left to 1 right, v: 0 top to 1 bottom).
        public static Vector2 RectToNormalized(Rect rect, Vector2 localPoint)
        {
            if (rect.width <= 0f || rect.height <= 0f)
                return new Vector2(0.5f, 0.5f);
            var u = Mathf.Clamp01((localPoint.x - rect.xMin) / rect.width);
            var v = Mathf.Clamp01(1f - (localPoint.y - rect.yMin) / rect.height);
            return new Vector2(u, v);
        }

        // Horizontal yaw angle in degrees for window index (0..count-1) arranged in an arc around the user.
        public static float WindowSlotYaw(int index, int count, float stepDegrees = 38f)
        {
            if (count <= 1)
                return 0f;
            var center = (count - 1) * 0.5f;
            return (index - center) * stepDegrees;
        }

        // Detects whether a playing webpage title or URL advertises a 360/VR180 video.
        public static bool IsVr360TitleOrUrl(string title, string url)
        {
            var combined = ((title ?? "") + " " + (url ?? "")).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(combined))
                return false;
            if (s_VrPrimaryToken.IsMatch(combined))
                return true;
            return s_3dToken.IsMatch(combined) && s_3dPairToken.IsMatch(combined);
        }

        // Infers equirectangular 360 vs VR180 & 2D/SBS/TB stereo layout from the webpage title and URL.
        public static VideoFormat InferVrVideoFormat(string title, string url)
        {
            var normalized = ((title ?? "") + " " + (url ?? ""))
                .Replace(':', ' ')
                .Replace('/', ' ')
                .Replace('\\', ' ')
                .Replace('.', ' ')
                .Replace('?', ' ')
                .Replace('&', ' ')
                .Replace('=', ' ')
                .Replace('#', ' ')
                .Replace('°', ' ');
            return VideoFormat.FromFileName(normalized);
        }
    }
}
