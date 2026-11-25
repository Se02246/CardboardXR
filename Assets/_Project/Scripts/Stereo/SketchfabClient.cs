using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CardboardXR.Stereo
{
    public struct SketchfabCategory
    {
        public string Slug;
        public string DisplayNameIt;
        public bool IsQuickFilter;
    }

    public struct SketchfabModelEntry
    {
        public string Uid;
        public string Name;
        public string AuthorName;
        public string ThumbnailUrl;
        public long LikeCount;
        public long FaceCount;
        public long DownloadSizeBytes;
        public bool HasGlb;
        public bool HasGltf;
        public string LicenseLabel;
    }

    public struct SketchfabSearchPage
    {
        public List<SketchfabModelEntry> Results;
        public string NextCursor;
        public string PreviousCursor;
    }

    public struct SketchfabDownloadLink
    {
        public string Url;
        public long SizeBytes;
        public bool IsGlb;

        public bool IsValid => !string.IsNullOrEmpty(Url);
    }

    public struct SketchfabUserProfile
    {
        public bool Valid;
        public string Username;
        public string DisplayName;
    }

    // Pure-C# client helpers for the official Sketchfab Data API v3 (18-category search catalog,
    // cursor pagination, GLB/glTF archive link parsing, /v3/me verification, and token sanitization).
    public static class SketchfabClient
    {
        public const string ApiBaseUrl = "https://api.sketchfab.com/v3";
        public const string TokenSettingsUrl = "https://sketchfab.com/settings/password";
        public const string AttributionText = "Powered by Sketchfab";
        public const int MaxFaceCount = 150000;
        public const long MaxDownloadBytes = 45L * 1024L * 1024L;

        public static readonly (string SortKey, string LabelIt)[] SortOptions =
        {
            ("-likeCount", "Popolari"),
            ("-viewCount", "Più visti"),
            ("-publishedAt", "Recenti"),
        };

        static readonly SketchfabCategory[] k_Categories =
        {
            new() { Slug = "", DisplayNameIt = "Tutti", IsQuickFilter = true },
            new() { Slug = "furniture-home", DisplayNameIt = "Arredo", IsQuickFilter = true },
            new() { Slug = "nature-plants", DisplayNameIt = "Natura", IsQuickFilter = true },
            new() { Slug = "electronics-gadgets", DisplayNameIt = "Elettronica", IsQuickFilter = true },
            new() { Slug = "cars-vehicles", DisplayNameIt = "Veicoli", IsQuickFilter = true },
            new() { Slug = "animals-pets", DisplayNameIt = "Animali", IsQuickFilter = true },
            new() { Slug = "art-abstract", DisplayNameIt = "Arte", IsQuickFilter = true },
            new() { Slug = "architecture", DisplayNameIt = "Architettura", IsQuickFilter = false },
            new() { Slug = "characters-creatures", DisplayNameIt = "Personaggi", IsQuickFilter = false },
            new() { Slug = "cultural-heritage-history", DisplayNameIt = "Storia e Arte", IsQuickFilter = false },
            new() { Slug = "food-drink", DisplayNameIt = "Cibo e Bevande", IsQuickFilter = false },
            new() { Slug = "science-technology", DisplayNameIt = "Scienza e Tech", IsQuickFilter = false },
            new() { Slug = "fashion-style", DisplayNameIt = "Moda", IsQuickFilter = false },
            new() { Slug = "music", DisplayNameIt = "Musica", IsQuickFilter = false },
            new() { Slug = "places-travel", DisplayNameIt = "Luoghi", IsQuickFilter = false },
            new() { Slug = "sports-fitness", DisplayNameIt = "Sport", IsQuickFilter = false },
            new() { Slug = "people", DisplayNameIt = "Persone", IsQuickFilter = false },
            new() { Slug = "weapons-military", DisplayNameIt = "Militaria", IsQuickFilter = false },
            new() { Slug = "news-politics", DisplayNameIt = "Attualità", IsQuickFilter = false },
        };

        public static IReadOnlyList<SketchfabCategory> Categories => k_Categories;

        public static string BuildSearchUrl(string categorySlug, string query, string sortBy, string cursor, int count = 12)
        {
            var clampedCount = Mathf.Clamp(count, 1, 24);
            var sb = new System.Text.StringBuilder(220);
            sb.Append(ApiBaseUrl)
                .Append("/search?type=models&downloadable=true&archives_flavours=false&max_face_count=")
                .Append(MaxFaceCount)
                .Append("&count=")
                .Append(clampedCount);

            if (!string.IsNullOrWhiteSpace(categorySlug))
                sb.Append("&categories=").Append(Uri.EscapeDataString(categorySlug.Trim()));

            if (!string.IsNullOrWhiteSpace(query))
                sb.Append("&q=").Append(Uri.EscapeDataString(query.Trim()));

            if (!string.IsNullOrWhiteSpace(sortBy) && string.IsNullOrWhiteSpace(query))
                sb.Append("&sort_by=").Append(Uri.EscapeDataString(sortBy.Trim()));

            if (!string.IsNullOrWhiteSpace(cursor))
                sb.Append("&cursor=").Append(Uri.EscapeDataString(cursor.Trim()));

            return sb.ToString();
        }

        public static string BuildDownloadEndpoint(string uid) =>
            string.IsNullOrEmpty(uid) ? "" : $"{ApiBaseUrl}/models/{Uri.EscapeDataString(uid)}/download";

        public static string BuildMeEndpoint() => ApiBaseUrl + "/me";

        public static SketchfabSearchPage ParseSearchResponse(string json)
        {
            var page = new SketchfabSearchPage
            {
                Results = new List<SketchfabModelEntry>(),
            };

            if (PolyHavenJson.Parse(json) is not Dictionary<string, object> root)
                return page;

            var cursors = PolyHavenJson.GetDict(root, "cursors");
            if (cursors != null)
            {
                page.NextCursor = PolyHavenJson.GetString(cursors, "next", "");
                page.PreviousCursor = PolyHavenJson.GetString(cursors, "previous", "");
            }

            var results = PolyHavenJson.GetList(root, "results");
            if (results == null)
                return page;

            for (var i = 0; i < results.Count; i++)
            {
                if (results[i] is not Dictionary<string, object> item)
                    continue;
                var uid = PolyHavenJson.GetString(item, "uid", "");
                if (string.IsNullOrWhiteSpace(uid))
                    continue;

                var name = PolyHavenJson.GetString(item, "name", uid);
                var userDict = PolyHavenJson.GetDict(item, "user");
                var author = userDict != null
                    ? PolyHavenJson.GetString(userDict, "displayName", PolyHavenJson.GetString(userDict, "username", ""))
                    : "";
                var thumbUrl = PickBestThumbnailUrl(PolyHavenJson.GetDict(item, "thumbnails"));

                var archives = PolyHavenJson.GetDict(item, "archives");
                var glbDict = PolyHavenJson.GetDict(archives, "glb");
                var gltfDict = PolyHavenJson.GetDict(archives, "gltf");
                var hasGlb = glbDict != null;
                var hasGltf = gltfDict != null;
                var glbSize = glbDict != null ? PolyHavenJson.GetLong(glbDict, "size", 0) : 0;
                var gltfSize = gltfDict != null ? PolyHavenJson.GetLong(gltfDict, "size", 0) : 0;
                var dlSize = glbSize > 0 ? glbSize : gltfSize;

                var licenseDict = PolyHavenJson.GetDict(item, "license");
                var licenseLabel = licenseDict != null ? PolyHavenJson.GetString(licenseDict, "label", "") : "";

                page.Results.Add(new SketchfabModelEntry
                {
                    Uid = uid,
                    Name = string.IsNullOrWhiteSpace(name) ? uid : name.Trim(),
                    AuthorName = author ?? "",
                    ThumbnailUrl = thumbUrl,
                    LikeCount = PolyHavenJson.GetLong(item, "likeCount", 0),
                    FaceCount = PolyHavenJson.GetLong(item, "faceCount", 0),
                    DownloadSizeBytes = dlSize,
                    HasGlb = hasGlb,
                    HasGltf = hasGltf,
                    LicenseLabel = licenseLabel,
                });
            }

            return page;
        }

        static string PickBestThumbnailUrl(Dictionary<string, object> thumbnailsDict)
        {
            var images = PolyHavenJson.GetList(thumbnailsDict, "images");
            if (images == null || images.Count == 0)
                return "";

            var bestUrl = "";
            var bestScore = int.MaxValue;
            for (var i = 0; i < images.Count; i++)
            {
                if (images[i] is not Dictionary<string, object> img)
                    continue;
                var url = PolyHavenJson.GetString(img, "url", "");
                if (string.IsNullOrEmpty(url))
                    continue;
                var width = (int)PolyHavenJson.GetLong(img, "width", 0);
                // Prefer widths around 480 px within [256..1024].
                var penalty = width >= 256 && width <= 1024
                    ? Mathf.Abs(width - 480)
                    : 10000 + Mathf.Abs(width - 480);
                if (penalty < bestScore)
                {
                    bestScore = penalty;
                    bestUrl = url;
                }
            }
            return bestUrl;
        }

        public static SketchfabDownloadLink ParseDownloadResponse(string json)
        {
            if (PolyHavenJson.Parse(json) is not Dictionary<string, object> root)
                return default;

            var glb = PolyHavenJson.GetDict(root, "glb");
            var glbUrl = PolyHavenJson.GetString(glb, "url", "");
            if (!string.IsNullOrEmpty(glbUrl))
            {
                return new SketchfabDownloadLink
                {
                    Url = glbUrl,
                    SizeBytes = PolyHavenJson.GetLong(glb, "size", 0),
                    IsGlb = true,
                };
            }

            var gltf = PolyHavenJson.GetDict(root, "gltf");
            var gltfUrl = PolyHavenJson.GetString(gltf, "url", "");
            if (!string.IsNullOrEmpty(gltfUrl))
            {
                return new SketchfabDownloadLink
                {
                    Url = gltfUrl,
                    SizeBytes = PolyHavenJson.GetLong(gltf, "size", 0),
                    IsGlb = false,
                };
            }

            return default;
        }

        public static SketchfabUserProfile ParseUserProfile(string json)
        {
            if (PolyHavenJson.Parse(json) is not Dictionary<string, object> root)
                return default;
            var username = PolyHavenJson.GetString(root, "username", "");
            var displayName = PolyHavenJson.GetString(root, "displayName", "");
            if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(displayName))
                return default;
            return new SketchfabUserProfile
            {
                Valid = true,
                Username = string.IsNullOrWhiteSpace(username) ? displayName : username,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            };
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "";
            if (bytes >= 1024L * 1024L)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / (1024.0 * 1024.0));
            return string.Format(CultureInfo.InvariantCulture, "{0} KB", Math.Max(1L, bytes / 1024L));
        }

        public static string SanitizeToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(6).Trim();
            else if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(7).Trim();
            return trimmed;
        }
    }
}
