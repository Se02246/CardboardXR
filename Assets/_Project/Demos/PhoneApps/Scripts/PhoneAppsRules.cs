using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Demos.PhoneApps
{
    // Pure layout, orientation, coordinate-mapping, and sorting rules for Shizuku phone app windows.
    public static class PhoneAppsRules
    {
        public const int MaxWindows = 3;

        public static bool ShouldPromptReplaceWindow(int openCount, bool targetAlreadyOpen) =>
            !targetAlreadyOpen && openCount >= MaxWindows;
        public const int AppsPerPage = 12;
        public const int MaxBufferBytes = 896 * 864 * 4;
        public const float FullscreenExitShowPitchDeg = 46f;
        public const float FullscreenExitHidePitchDeg = 34f;
        public const float FullscreenExitButtonPitchDeg = 58f;
        public const float FullscreenExitButtonDistance = 1.15f;
        public const float FullscreenCaptureInterval = 1f / 30f;

        public static readonly Vector2 VrFullscreenTapPoint = new(0.25f, 0.50f);

        public static readonly Vector2 PortraitPanelSize = new(424f, 796f);
        public static readonly Vector2 PortraitScreenSize = new(400f, 772f);
        public static readonly Vector2 LandscapePanelSize = new(868f, 502f);
        public static readonly Vector2 LandscapeScreenSize = new(844f, 478f);

        static readonly Dictionary<string, int> s_FeaturedRanks = new()
        {
            ["com.android.chrome"] = 0,
            ["com.zhiliaoapp.musically"] = 1,
            ["com.ss.android.ugc.trill"] = 2,
            ["com.whatsapp"] = 3,
            ["com.instagram.android"] = 4,
            ["com.google.android.youtube"] = 5,
            ["com.spotify.music"] = 6,
            ["org.telegram.messenger"] = 7,
            ["com.twitter.android"] = 8,
            ["com.reddit.frontpage"] = 9,
            ["com.google.android.gm"] = 10,
            ["com.google.android.apps.maps"] = 11,
            ["com.discord"] = 12,
            ["com.google.android.apps.photos"] = 13,
            ["com.android.settings"] = 14,
        };

        public static Vector2 PanelSize(bool landscape) => landscape ? LandscapePanelSize : PortraitPanelSize;

        public static Vector2 ScreenSize(bool landscape) => landscape ? LandscapeScreenSize : PortraitScreenSize;

        public static float TopBarWidth(bool landscape) => landscape ? 748f : 420f;

        public static bool ShouldShowFullscreenExit(bool currentlyShown, float pitchDownDeg) =>
            currentlyShown
                ? pitchDownDeg >= FullscreenExitHidePitchDeg
                : pitchDownDeg >= FullscreenExitShowPitchDeg;

        public static float TopBarY(bool landscape) => PanelSize(landscape).y * 0.5f + 38f;

        public static Vector2Int DisplaySize(bool landscape) =>
            landscape ? new Vector2Int(896, 504) : new Vector2Int(432, 864);

        // Converts a local point on a centered RectTransform into normalized Android display
        // coordinates (u: 0 left to 1 right, v: 0 top to 1 bottom).
        public static Vector2 RectToNormalized(Rect rect, Vector2 localPoint)
        {
            if (rect.width <= 0f || rect.height <= 0f)
                return new Vector2(0.5f, 0.5f);
            var u = Mathf.Clamp01((localPoint.x - rect.xMin) / rect.width);
            var v = Mathf.Clamp01(1f - (localPoint.y - rect.yMin) / rect.height);
            return new Vector2(u, v);
        }

        // Places up to 3 windows clearly around the centered launcher without occlusion.
        public static float WindowSlotYaw(int index) => index switch
        {
            0 => 34f,
            1 => -34f,
            2 => 58f,
            _ => 0f,
        };

        public static int PageCount(int totalApps, int perPage = AppsPerPage)
        {
            if (perPage <= 0 || totalApps <= 0)
                return 1;
            return (totalApps + perPage - 1) / perPage;
        }

        public static int ClampPage(int page, int totalApps, int perPage = AppsPerPage)
        {
            var max = PageCount(totalApps, perPage) - 1;
            return Mathf.Clamp(page, 0, max);
        }

        public static int FeaturedPriority(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return int.MaxValue;
            return s_FeaturedRanks.TryGetValue(packageName, out var rank) ? rank : int.MaxValue;
        }

        public static string Initials(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return "AP";
            var parts = label.Trim().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (char.ToUpperInvariant(parts[0][0]).ToString() + char.ToUpperInvariant(parts[1][0]));
            var word = parts[0];
            return word.Length >= 2
                ? word.Substring(0, 2).ToUpperInvariant()
                : word.ToUpperInvariant();
        }

        public static float CaptureIntervalForWindowCount(int windowCount) => windowCount switch
        {
            <= 1 => 1f / 24f,
            2 => 1f / 18f,
            _ => 1f / 14f,
        };
    }
}
