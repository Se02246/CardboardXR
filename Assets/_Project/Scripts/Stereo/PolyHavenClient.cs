using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Stereo
{
    public struct PolyHavenCategoryInfo
    {
        public string Slug;
        public string ApiName;
        public string DisplayNameIt;
        public string Icon;
        public Color Tint;
        public int Count;
        public string RepresentativeAssetId;
        public string RepresentativeThumbUrl;
    }

    public struct PolyHavenAssetEntry
    {
        public string Id;
        public string Name;
        public string TopCategory;
        public string FullCategory;
        public string[] Tags;
        public long DownloadCount;
        public string ThumbnailUrl;
    }

    public struct PolyHavenDownloadChoice
    {
        public string Url;
        public bool IsHdr;
        public long SizeBytes;
        public string ResolutionLabel;
    }

    // Pure-C# client helpers for Poly Haven's public HDRI API (catalog parsing, 12-category taxonomy,
    // multi-token search, JPEG dimension inspection, and download resolution/format selection).
    public static class PolyHavenClient
    {
        public const string UserAgent = "CardboardXR/1.0";
        public const string AttributionText = "Powered by Poly Haven";
        public const string ApiBaseUrl = "https://api.polyhaven.com";
        public const string AssetsEndpoint = ApiBaseUrl + "/assets?type=hdris";
        public const string FilesEndpointPrefix = ApiBaseUrl + "/files/";
        public const long MaxDirectTonemappedBytes = 12L * 1024L * 1024L;
        public const long MaxDirect4kHdrBytes = 22L * 1024L * 1024L;

        static readonly PolyHavenCategoryInfo[] k_Categories =
        {
            new()
            {
                Slug = "abandoned-ruins",
                ApiName = "Abandoned & Ruins",
                DisplayNameIt = "Rovine e abbandonati",
                Icon = "door_front",
                Tint = new Color(0.38f, 0.34f, 0.31f, 0.94f),
            },
            new()
            {
                Slug = "coast-water",
                ApiName = "Coast & Water",
                DisplayNameIt = "Costa e acque",
                Icon = "360",
                Tint = new Color(0.14f, 0.44f, 0.68f, 0.94f),
            },
            new()
            {
                Slug = "desert-arid",
                ApiName = "Desert & Arid",
                DisplayNameIt = "Deserto e arido",
                Icon = "landscape",
                Tint = new Color(0.68f, 0.45f, 0.22f, 0.94f),
            },
            new()
            {
                Slug = "fields-countryside",
                ApiName = "Fields & Countryside",
                DisplayNameIt = "Campi e campagna",
                Icon = "landscape",
                Tint = new Color(0.32f, 0.52f, 0.24f, 0.94f),
            },
            new()
            {
                Slug = "forest-woodland",
                ApiName = "Forest & Woodland",
                DisplayNameIt = "Foresta e bosco",
                Icon = "palette",
                Tint = new Color(0.16f, 0.40f, 0.25f, 0.94f),
            },
            new()
            {
                Slug = "industrial",
                ApiName = "Industrial",
                DisplayNameIt = "Industriale",
                Icon = "bolt",
                Tint = new Color(0.35f, 0.38f, 0.42f, 0.94f),
            },
            new()
            {
                Slug = "interiors",
                ApiName = "Interiors",
                DisplayNameIt = "Interni",
                Icon = "home",
                Tint = new Color(0.49f, 0.34f, 0.26f, 0.94f),
            },
            new()
            {
                Slug = "mountains-hills",
                ApiName = "Mountains & Hills",
                DisplayNameIt = "Montagne e colline",
                Icon = "landscape",
                Tint = new Color(0.24f, 0.42f, 0.58f, 0.94f),
            },
            new()
            {
                Slug = "parks-gardens",
                ApiName = "Parks & Gardens",
                DisplayNameIt = "Parchi e giardini",
                Icon = "auto_awesome",
                Tint = new Color(0.26f, 0.48f, 0.32f, 0.94f),
            },
            new()
            {
                Slug = "pure-skies",
                ApiName = "Pure Skies",
                DisplayNameIt = "Cieli puri",
                Icon = "360",
                Tint = new Color(0.25f, 0.50f, 0.78f, 0.94f),
            },
            new()
            {
                Slug = "streets-town",
                ApiName = "Streets & Town",
                DisplayNameIt = "Strade e città",
                Icon = "my_location",
                Tint = new Color(0.32f, 0.36f, 0.52f, 0.94f),
            },
            new()
            {
                Slug = "studio",
                ApiName = "Studio",
                DisplayNameIt = "Studio",
                Icon = "movie",
                Tint = new Color(0.30f, 0.30f, 0.36f, 0.94f),
            },
        };

        public static IReadOnlyList<PolyHavenCategoryInfo> Categories => k_Categories;

        public static string BuildThumbnailJpgUrl(string assetId) =>
            string.IsNullOrEmpty(assetId)
                ? ""
                : $"https://cdn.polyhaven.com/asset_img/primary/{Uri.EscapeDataString(assetId)}.png?width=512&format=jpg";

        public static string BuildFilesUrl(string assetId) =>
            string.IsNullOrEmpty(assetId) ? "" : FilesEndpointPrefix + Uri.EscapeDataString(assetId);

        public static string ExtractTopCategory(string fullCategory)
        {
            if (string.IsNullOrWhiteSpace(fullCategory))
                return "";
            var slash = fullCategory.IndexOf('/');
            return (slash >= 0 ? fullCategory.Substring(0, slash) : fullCategory).Trim();
        }

        public static string GetItalianCategoryName(string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName))
                return "";
            var top = ExtractTopCategory(apiName);
            for (var i = 0; i < k_Categories.Length; i++)
            {
                if (string.Equals(k_Categories[i].ApiName, top, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k_Categories[i].Slug, top, StringComparison.OrdinalIgnoreCase))
                    return k_Categories[i].DisplayNameIt;
            }
            return top;
        }

        public static List<PolyHavenAssetEntry> ParseAssetsCatalog(string json)
        {
            var result = new List<PolyHavenAssetEntry>();
            if (!(PolyHavenJson.Parse(json) is Dictionary<string, object> root))
                return result;

            foreach (var kvp in root)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || !(kvp.Value is Dictionary<string, object> item))
                    continue;

                var id = kvp.Key.Trim();
                var name = PolyHavenJson.GetString(item, "name", id);
                var fullCat = PolyHavenJson.GetString(item, "category", "");
                var topCat = ExtractTopCategory(fullCat);
                if (string.IsNullOrEmpty(topCat))
                {
                    var cats = PolyHavenJson.GetStringList(item, "categories");
                    for (var i = 0; i < cats.Length; i++)
                    {
                        if (string.Equals(cats[i], "studio", StringComparison.OrdinalIgnoreCase))
                        {
                            topCat = "Studio";
                            fullCat = "Studio";
                            break;
                        }
                        if (string.Equals(cats[i], "pure skies", StringComparison.OrdinalIgnoreCase))
                        {
                            topCat = "Pure Skies";
                            fullCat = "Pure Skies";
                            break;
                        }
                    }
                }

                result.Add(new PolyHavenAssetEntry
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                    TopCategory = topCat,
                    FullCategory = fullCat,
                    Tags = PolyHavenJson.GetStringList(item, "tags"),
                    DownloadCount = PolyHavenJson.GetLong(item, "download_count", 0),
                    ThumbnailUrl = BuildThumbnailJpgUrl(id),
                });
            }

            result.Sort((a, b) =>
            {
                var cmp = b.DownloadCount.CompareTo(a.DownloadCount);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public static List<PolyHavenCategoryInfo> BuildCategorySummaries(IReadOnlyList<PolyHavenAssetEntry> catalog)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var reps = new Dictionary<string, PolyHavenAssetEntry>(StringComparer.OrdinalIgnoreCase);

            if (catalog != null)
            {
                for (var i = 0; i < catalog.Count; i++)
                {
                    var entry = catalog[i];
                    if (string.IsNullOrEmpty(entry.TopCategory))
                        continue;
                    counts.TryGetValue(entry.TopCategory, out var c);
                    counts[entry.TopCategory] = c + 1;

                    if (!reps.TryGetValue(entry.TopCategory, out var currentBest) ||
                        entry.DownloadCount > currentBest.DownloadCount)
                    {
                        reps[entry.TopCategory] = entry;
                    }
                }
            }

            var summaries = new List<PolyHavenCategoryInfo>(k_Categories.Length);
            for (var i = 0; i < k_Categories.Length; i++)
            {
                var info = k_Categories[i];
                if (counts.TryGetValue(info.ApiName, out var count))
                    info.Count = count;
                if (reps.TryGetValue(info.ApiName, out var rep))
                {
                    info.RepresentativeAssetId = rep.Id;
                    info.RepresentativeThumbUrl = rep.ThumbnailUrl;
                }
                summaries.Add(info);
            }
            return summaries;
        }

        public static List<PolyHavenAssetEntry> FilterByCategory(IReadOnlyList<PolyHavenAssetEntry> catalog,
            string apiCategoryName)
        {
            var result = new List<PolyHavenAssetEntry>();
            if (catalog == null)
                return result;
            if (string.IsNullOrWhiteSpace(apiCategoryName))
            {
                result.AddRange(catalog);
                return result;
            }
            for (var i = 0; i < catalog.Count; i++)
            {
                if (string.Equals(catalog[i].TopCategory, apiCategoryName, StringComparison.OrdinalIgnoreCase))
                    result.Add(catalog[i]);
            }
            return result;
        }

        public static List<PolyHavenAssetEntry> SearchCatalog(IReadOnlyList<PolyHavenAssetEntry> catalog,
            string query, string optionalApiCategory = null)
        {
            var scoped = FilterByCategory(catalog, optionalApiCategory);
            if (string.IsNullOrWhiteSpace(query))
                return scoped;

            var tokens = query.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return scoped;

            var scored = new List<(int score, PolyHavenAssetEntry entry)>();
            var trimmed = query.Trim();

            for (var i = 0; i < scoped.Count; i++)
            {
                var entry = scoped[i];
                var italianCat = GetItalianCategoryName(entry.TopCategory);
                var allMatched = true;
                var score = 0;

                for (var t = 0; t < tokens.Length; t++)
                {
                    var tok = tokens[t];
                    var inName = entry.Name != null && entry.Name.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0;
                    var inId = entry.Id != null && entry.Id.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0;
                    var inCat = (entry.FullCategory != null &&
                                 entry.FullCategory.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (italianCat.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0);
                    var inTags = false;
                    if (entry.Tags != null)
                    {
                        for (var g = 0; g < entry.Tags.Length; g++)
                        {
                            if (entry.Tags[g].IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                inTags = true;
                                break;
                            }
                        }
                    }

                    if (!inName && !inId && !inCat && !inTags)
                    {
                        allMatched = false;
                        break;
                    }

                    if (inName)
                        score += 10;
                    else if (inId)
                        score += 6;
                    else if (inTags)
                        score += 3;
                    else
                        score += 1;
                }

                if (!allMatched)
                    continue;

                if (entry.Name != null && entry.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                    score += 30;
                scored.Add((score, entry));
            }

            scored.Sort((a, b) =>
            {
                var s = b.score.CompareTo(a.score);
                if (s != 0)
                    return s;
                return b.entry.DownloadCount.CompareTo(a.entry.DownloadCount);
            });

            var result = new List<PolyHavenAssetEntry>(scored.Count);
            for (var i = 0; i < scored.Count; i++)
                result.Add(scored[i].entry);
            return result;
        }

        public static bool TrySelectBestDownload(string filesJson, out PolyHavenDownloadChoice choice)
        {
            choice = default;
            if (!(PolyHavenJson.Parse(filesJson) is Dictionary<string, object> root))
                return false;

            var tonemapped = PolyHavenJson.GetDict(root, "tonemapped");
            var tmUrl = PolyHavenJson.GetString(tonemapped, "url", "");
            var tmSize = PolyHavenJson.GetLong(tonemapped, "size", 0);
            if (!string.IsNullOrEmpty(tmUrl) && tmSize > 0 && tmSize <= MaxDirectTonemappedBytes)
            {
                choice = new PolyHavenDownloadChoice
                {
                    Url = tmUrl,
                    IsHdr = false,
                    SizeBytes = tmSize,
                    ResolutionLabel = "Tonemapped JPG",
                };
                return true;
            }

            var hdri = PolyHavenJson.GetDict(root, "hdri");
            if (TryGetHdrVariant(hdri, "4k", out var hdr4k) && hdr4k.SizeBytes > 0 && hdr4k.SizeBytes <= MaxDirect4kHdrBytes)
            {
                choice = hdr4k;
                return true;
            }
            if (TryGetHdrVariant(hdri, "2k", out var hdr2k))
            {
                choice = hdr2k;
                return true;
            }
            if (TryGetHdrVariant(hdri, "1k", out var hdr1k))
            {
                choice = hdr1k;
                return true;
            }
            if (!string.IsNullOrEmpty(tmUrl))
            {
                choice = new PolyHavenDownloadChoice
                {
                    Url = tmUrl,
                    IsHdr = false,
                    SizeBytes = tmSize,
                    ResolutionLabel = "Tonemapped JPG",
                };
                return true;
            }
            return false;
        }

        static bool TryGetHdrVariant(Dictionary<string, object> hdri, string res, out PolyHavenDownloadChoice choice)
        {
            choice = default;
            var resDict = PolyHavenJson.GetDict(hdri, res);
            var hdrDict = PolyHavenJson.GetDict(resDict, "hdr");
            var url = PolyHavenJson.GetString(hdrDict, "url", "");
            if (string.IsNullOrEmpty(url))
                return false;
            choice = new PolyHavenDownloadChoice
            {
                Url = url,
                IsHdr = true,
                SizeBytes = PolyHavenJson.GetLong(hdrDict, "size", 0),
                ResolutionLabel = res.ToUpperInvariant() + " HDR",
            };
            return true;
        }

        public static bool TryReadJpegDimensions(byte[] jpegBytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (jpegBytes == null || jpegBytes.Length < 4 || jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8)
                return false;

            var i = 2;
            while (i + 3 < jpegBytes.Length)
            {
                if (jpegBytes[i] != 0xFF)
                {
                    i++;
                    continue;
                }
                while (i < jpegBytes.Length && jpegBytes[i] == 0xFF)
                    i++;
                if (i >= jpegBytes.Length)
                    break;

                var marker = jpegBytes[i++];
                if (marker == 0xD9 || marker == 0xDA)
                    break;
                if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                    continue;
                if (i + 1 >= jpegBytes.Length)
                    break;

                var segmentLength = (jpegBytes[i] << 8) | jpegBytes[i + 1];
                if (segmentLength < 2 || i + segmentLength > jpegBytes.Length)
                    break;

                if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2 || marker == 0xC3)
                {
                    if (segmentLength >= 7)
                    {
                        height = (jpegBytes[i + 3] << 8) | jpegBytes[i + 4];
                        width = (jpegBytes[i + 5] << 8) | jpegBytes[i + 6];
                        return width > 0 && height > 0;
                    }
                    return false;
                }

                i += segmentLength;
            }
            return false;
        }
    }
}
