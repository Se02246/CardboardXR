using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Stereo
{
    public struct PolyHavenModelCategory
    {
        public string Slug;
        public string DisplayNameIt;
        public int Count;
    }

    public struct PolyHavenModelEntry
    {
        public string Id;
        public string Name;
        public string[] Categories;
        public string[] Tags;
        public long DownloadCount;
        public string ThumbnailUrl;
    }

    public struct PolyHavenModelFileItem
    {
        public string RelativePath;
        public string Url;
        public long SizeBytes;
    }

    public struct PolyHavenModelDownloadPlan
    {
        public string AssetId;
        public string ResolutionLabel;
        public PolyHavenModelFileItem GltfFile;
        public List<PolyHavenModelFileItem> IncludedFiles;

        public bool IsValid => !string.IsNullOrEmpty(GltfFile.Url);
    }

    // Pure-C# client helpers for Poly Haven's public 3D Models API (/assets?t=models and /files/{id}).
    public static class PolyHavenModelClient
    {
        public const string ModelsEndpoint = PolyHavenClient.ApiBaseUrl + "/assets?t=models";
        public const string FilesEndpointPrefix = PolyHavenClient.ApiBaseUrl + "/files/";
        public const string AttributionText = PolyHavenClient.AttributionText;

        static readonly PolyHavenModelCategory[] k_Categories =
        {
            new() { Slug = "", DisplayNameIt = "Tutti" },
            new() { Slug = "furniture", DisplayNameIt = "Arredo" },
            new() { Slug = "decorative", DisplayNameIt = "Decorazioni" },
            new() { Slug = "plants", DisplayNameIt = "Piante" },
            new() { Slug = "lighting", DisplayNameIt = "Luci" },
            new() { Slug = "props", DisplayNameIt = "Oggetti" },
            new() { Slug = "electronics", DisplayNameIt = "Elettronica" },
            new() { Slug = "nature", DisplayNameIt = "Natura" },
            new() { Slug = "industrial", DisplayNameIt = "Industriale" },
        };

        public static IReadOnlyList<PolyHavenModelCategory> Categories => k_Categories;

        public static string BuildModelThumbUrl(string assetId) =>
            string.IsNullOrEmpty(assetId)
                ? ""
                : $"https://cdn.polyhaven.com/asset_img/thumbs/{assetId}.png?width=256&height=256&format=jpg";

        public static List<PolyHavenModelEntry> ParseModelsCatalog(string json)
        {
            var result = new List<PolyHavenModelEntry>();
            if (PolyHavenJson.Parse(json) is not Dictionary<string, object> root)
                return result;

            foreach (var kv in root)
            {
                var id = kv.Key;
                if (string.IsNullOrWhiteSpace(id) || kv.Value is not Dictionary<string, object> dict)
                    continue;
                var name = PolyHavenJson.GetString(dict, "name", id).Replace('_', ' ');
                var cats = PolyHavenJson.GetStringList(dict, "categories");
                var tags = PolyHavenJson.GetStringList(dict, "tags");
                var dlCount = PolyHavenJson.GetLong(dict, "download_count", 0);
                result.Add(new PolyHavenModelEntry
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name,
                    Categories = cats,
                    Tags = tags,
                    DownloadCount = dlCount,
                    ThumbnailUrl = BuildModelThumbUrl(id),
                });
            }

            result.Sort((a, b) =>
            {
                var cmp = b.DownloadCount.CompareTo(a.DownloadCount);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public static List<PolyHavenModelEntry> FilterModels(IReadOnlyList<PolyHavenModelEntry> catalog,
            string categorySlug, string query)
        {
            var result = new List<PolyHavenModelEntry>();
            if (catalog == null || catalog.Count == 0)
                return result;

            var hasCat = !string.IsNullOrWhiteSpace(categorySlug);
            var trimmedCat = hasCat ? categorySlug.Trim() : "";
            var tokens = string.IsNullOrWhiteSpace(query)
                ? Array.Empty<string>()
                : query.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < catalog.Count; i++)
            {
                var entry = catalog[i];
                if (hasCat && !MatchesCategory(entry, trimmedCat))
                    continue;
                if (tokens.Length > 0 && !MatchesTokens(entry, tokens))
                    continue;
                result.Add(entry);
            }
            return result;
        }

        static bool MatchesCategory(PolyHavenModelEntry entry, string slug)
        {
            if (entry.Categories == null)
                return false;
            for (var i = 0; i < entry.Categories.Length; i++)
            {
                if (string.Equals(entry.Categories[i], slug, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static bool MatchesTokens(PolyHavenModelEntry entry, string[] tokens)
        {
            for (var t = 0; t < tokens.Length; t++)
            {
                var tok = tokens[t];
                var matched = ContainsIgnoreCase(entry.Name, tok) || ContainsIgnoreCase(entry.Id, tok);
                if (!matched && entry.Categories != null)
                {
                    for (var c = 0; c < entry.Categories.Length; c++)
                    {
                        if (ContainsIgnoreCase(entry.Categories[c], tok))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched && entry.Tags != null)
                {
                    for (var g = 0; g < entry.Tags.Length; g++)
                    {
                        if (ContainsIgnoreCase(entry.Tags[g], tok))
                        {
                            matched = true;
                            break;
                        }
                    }
                }
                if (!matched)
                    return false;
            }
            return true;
        }

        static bool ContainsIgnoreCase(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        public static PolyHavenModelDownloadPlan ParseModelDownloadPlan(string assetId, string filesJson)
        {
            var plan = new PolyHavenModelDownloadPlan
            {
                AssetId = assetId ?? "",
                IncludedFiles = new List<PolyHavenModelFileItem>(),
            };
            var root = PolyHavenJson.Parse(filesJson) as Dictionary<string, object>;
            var gltfSection = PolyHavenJson.GetDict(root, "gltf");
            if (gltfSection == null)
                return plan;

            string chosenRes = null;
            Dictionary<string, object> chosenResDict = null;
            var preferred = new[] { "1k", "2k", "4k" };
            for (var i = 0; i < preferred.Length; i++)
            {
                var d = PolyHavenJson.GetDict(gltfSection, preferred[i]);
                if (d != null && PolyHavenJson.GetDict(d, "gltf") != null)
                {
                    chosenRes = preferred[i];
                    chosenResDict = d;
                    break;
                }
            }
            if (chosenResDict == null)
            {
                foreach (var kv in gltfSection)
                {
                    if (kv.Value is Dictionary<string, object> d && PolyHavenJson.GetDict(d, "gltf") != null)
                    {
                        chosenRes = kv.Key;
                        chosenResDict = d;
                        break;
                    }
                }
            }
            if (chosenResDict == null)
                return plan;

            var mainGltf = PolyHavenJson.GetDict(chosenResDict, "gltf");
            var gltfUrl = PolyHavenJson.GetString(mainGltf, "url");
            if (string.IsNullOrEmpty(gltfUrl))
                return plan;

            plan.ResolutionLabel = chosenRes ?? "1k";
            var gltfName = ExtractFileName(gltfUrl, $"{assetId}.gltf");
            plan.GltfFile = new PolyHavenModelFileItem
            {
                RelativePath = gltfName,
                Url = gltfUrl,
                SizeBytes = PolyHavenJson.GetLong(mainGltf, "size", 0),
            };

            var includes = PolyHavenJson.GetDict(mainGltf, "include");
            if (includes != null)
            {
                foreach (var kv in includes)
                {
                    if (kv.Value is not Dictionary<string, object> incDict)
                        continue;
                    var url = PolyHavenJson.GetString(incDict, "url");
                    if (string.IsNullOrEmpty(url))
                        continue;
                    plan.IncludedFiles.Add(new PolyHavenModelFileItem
                    {
                        RelativePath = kv.Key.Replace('\\', '/'),
                        Url = url,
                        SizeBytes = PolyHavenJson.GetLong(incDict, "size", 0),
                    });
                }
            }

            return plan;
        }

        static string ExtractFileName(string url, string fallback)
        {
            if (string.IsNullOrEmpty(url))
                return fallback;
            var q = url.IndexOf('?');
            var clean = q >= 0 ? url.Substring(0, q) : url;
            var slash = clean.LastIndexOf('/');
            return slash >= 0 && slash + 1 < clean.Length ? clean.Substring(slash + 1) : fallback;
        }
    }
}
