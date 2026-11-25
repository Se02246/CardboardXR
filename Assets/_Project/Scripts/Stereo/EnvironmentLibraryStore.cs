using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CardboardXR.Stereo
{
    public struct SavedEnvironment : IEquatable<SavedEnvironment>
    {
        public string Id;
        public string Title;
        public string Category;
        public string ThumbUrl;
        public bool IsProceduralCinema;

        public bool Equals(SavedEnvironment other) => string.Equals(Id, other.Id, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SavedEnvironment other && Equals(other);
        public override int GetHashCode() => Id != null ? Id.GetHashCode() : 0;
    }

    // Persists the user's active set of 360-degree environments (curated Poly Haven starters,
    // user-downloaded Poly Haven HDRIs, plus the offline procedural Dark Cinema fallback) and
    // resolves their local disk cache paths under Application.persistentDataPath.
    public static class EnvironmentLibraryStore
    {
        public const string ProceduralCinemaId = "__procedural_cinema__";
        const string k_PrefsKey = "CardboardXR.PolyHavenEnvironments";

        static readonly SavedEnvironment[] k_DefaultStarters =
        {
            new()
            {
                Id = "spiaggia_di_mondello",
                Title = "Spiaggia di Mondello",
                Category = "Coast & Water",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("spiaggia_di_mondello"),
            },
            new()
            {
                Id = "venice_sunset",
                Title = "Tramonto a Venezia",
                Category = "Coast & Water",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("venice_sunset"),
            },
            new()
            {
                Id = "alps_field",
                Title = "Alpi",
                Category = "Mountains & Hills",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("alps_field"),
            },
            new()
            {
                Id = "rogland_clear_night",
                Title = "Notte Stellata",
                Category = "Desert & Arid",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("rogland_clear_night"),
            },
            new()
            {
                Id = "rainforest_trail",
                Title = "Foresta Pluviale",
                Category = "Forest & Woodland",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("rainforest_trail"),
            },
            new()
            {
                Id = "autumn_park",
                Title = "Parco d'Autunno",
                Category = "Parks & Gardens",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("autumn_park"),
            },
            new()
            {
                Id = "fireplace",
                Title = "Salotto Camino",
                Category = "Interiors",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("fireplace"),
            },
            new()
            {
                Id = "kloppenheim_06_puresky",
                Title = "Cielo Dorato",
                Category = "Pure Skies",
                ThumbUrl = PolyHavenClient.BuildThumbnailJpgUrl("kloppenheim_06_puresky"),
            },
        };

        static readonly SavedEnvironment k_CinemaEntry = new()
        {
            Id = ProceduralCinemaId,
            Title = "Sala Cinema",
            Category = "Studio",
            ThumbUrl = "",
            IsProceduralCinema = true,
        };

        static readonly List<SavedEnvironment> s_Entries = new();
        static bool s_Loaded;

        public static event Action Changed;

        public static IReadOnlyList<SavedEnvironment> Entries
        {
            get
            {
                EnsureLoaded();
                return s_Entries;
            }
        }

        public static int Count
        {
            get
            {
                EnsureLoaded();
                return s_Entries.Count;
            }
        }

        public static SavedEnvironment Get(int index)
        {
            EnsureLoaded();
            if (s_Entries.Count == 0)
                return k_CinemaEntry;
            return s_Entries[Mathf.Clamp(index, 0, s_Entries.Count - 1)];
        }

        public static int IndexOfId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return -1;
            EnsureLoaded();
            for (var i = 0; i < s_Entries.Count; i++)
            {
                if (string.Equals(s_Entries[i].Id, id, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        public static bool Contains(string id) => IndexOfId(id) >= 0;

        public static bool AddOrUpdate(string id, string title, string category, string thumbUrl = null)
        {
            if (string.IsNullOrWhiteSpace(id) || id == ProceduralCinemaId)
                return false;
            EnsureLoaded();

            var cleanId = SanitizeField(id);
            var cleanTitle = SanitizeField(string.IsNullOrWhiteSpace(title) ? cleanId : title);
            var cleanCat = SanitizeField(category ?? "");
            var cleanThumb = SanitizeField(string.IsNullOrWhiteSpace(thumbUrl)
                ? PolyHavenClient.BuildThumbnailJpgUrl(cleanId)
                : thumbUrl);

            var entry = new SavedEnvironment
            {
                Id = cleanId,
                Title = cleanTitle,
                Category = cleanCat,
                ThumbUrl = cleanThumb,
                IsProceduralCinema = false,
            };

            var existingIdx = IndexOfId(cleanId);
            if (existingIdx >= 0)
            {
                s_Entries[existingIdx] = entry;
            }
            else
            {
                var cinemaIdx = IndexOfId(ProceduralCinemaId);
                if (cinemaIdx >= 0)
                    s_Entries.Insert(cinemaIdx, entry);
                else
                    s_Entries.Add(entry);
            }

            Save();
            Changed?.Invoke();
            return true;
        }

        public static bool Remove(string id, bool deleteCachedFiles = true)
        {
            if (string.IsNullOrWhiteSpace(id) || id == ProceduralCinemaId)
                return false;
            EnsureLoaded();

            var idx = IndexOfId(id);
            if (idx < 0)
                return false;
            s_Entries.RemoveAt(idx);
            Save();

            if (deleteCachedFiles)
            {
                TryDeleteFile(GetPanoramaCachePath(id));
                TryDeleteFile(GetThumbnailCachePath(id));
            }

            Changed?.Invoke();
            return true;
        }

        public static void ResetToDefaults()
        {
            s_Entries.Clear();
            s_Entries.AddRange(k_DefaultStarters);
            s_Entries.Add(k_CinemaEntry);
            s_Loaded = true;
            Save();
            Changed?.Invoke();
        }

        public static string GetPanoramaCachePath(string assetId) =>
            Path.Combine(Application.persistentDataPath, "polyhaven_envs", SanitizeFilename(assetId) + ".jpg");

        public static string GetThumbnailCachePath(string assetId) =>
            Path.Combine(Application.persistentDataPath, "polyhaven_covers", SanitizeFilename(assetId) + ".jpg");

        public static string GetCatalogCachePath() =>
            Path.Combine(Application.persistentDataPath, "polyhaven_catalog_cache.json");

        public static bool IsPanoramaCached(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId) || assetId == ProceduralCinemaId)
                return assetId == ProceduralCinemaId;
            try
            {
                var path = GetPanoramaCachePath(assetId);
                return File.Exists(path) && new FileInfo(path).Length > 1024;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string Serialize(IReadOnlyList<SavedEnvironment> items)
        {
            if (items == null || items.Count == 0)
                return "";
            var sb = new StringBuilder();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (string.IsNullOrWhiteSpace(item.Id) || item.IsProceduralCinema || item.Id == ProceduralCinemaId)
                    continue;
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(SanitizeField(item.Id))
                    .Append('\t').Append(SanitizeField(string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title))
                    .Append('\t').Append(SanitizeField(item.Category ?? ""))
                    .Append('\t').Append(SanitizeField(item.ThumbUrl ?? ""));
            }
            return sb.ToString();
        }

        public static List<SavedEnvironment> Deserialize(string raw)
        {
            var result = new List<SavedEnvironment>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]))
                    continue;
                var id = parts[0].Trim();
                if (id == ProceduralCinemaId || !seen.Add(id))
                    continue;
                var title = string.IsNullOrWhiteSpace(parts[1]) ? id : parts[1].Trim();
                var cat = parts.Length > 2 ? parts[2].Trim() : "";
                var thumb = parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3])
                    ? parts[3].Trim()
                    : PolyHavenClient.BuildThumbnailJpgUrl(id);
                result.Add(new SavedEnvironment
                {
                    Id = id,
                    Title = title,
                    Category = cat,
                    ThumbUrl = thumb,
                    IsProceduralCinema = false,
                });
            }
            return result;
        }

        static void EnsureLoaded()
        {
            if (s_Loaded)
                return;
            s_Loaded = true;
            s_Entries.Clear();
            if (PlayerPrefs.HasKey(k_PrefsKey))
            {
                var parsed = Deserialize(PlayerPrefs.GetString(k_PrefsKey, ""));
                s_Entries.AddRange(parsed);
            }
            else
            {
                s_Entries.AddRange(k_DefaultStarters);
            }
            s_Entries.Add(k_CinemaEntry);
        }

        static void Save()
        {
            PlayerPrefs.SetString(k_PrefsKey, Serialize(s_Entries));
            PlayerPrefs.Save();
        }

        static string SanitizeField(string value) =>
            (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        static string SanitizeFilename(string assetId)
        {
            if (string.IsNullOrEmpty(assetId))
                return "env";
            var sb = new StringBuilder(assetId.Length);
            for (var i = 0; i < assetId.Length; i++)
            {
                var c = assetId[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString();
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception) { }
        }
    }
}
