using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    public struct StarredPhoneApp : IEquatable<StarredPhoneApp>
    {
        public string PackageName;
        public string ActivityName;
        public string Label;
        public int ColorRgb;

        public string ItemId => AppLibrary.PhoneAppPrefix + PackageName;
        public Color Tint => new(((ColorRgb >> 16) & 0xFF) / 255f, ((ColorRgb >> 8) & 0xFF) / 255f, (ColorRgb & 0xFF) / 255f, 1f);

        public bool Equals(StarredPhoneApp other) => PackageName == other.PackageName;
        public override bool Equals(object obj) => obj is StarredPhoneApp other && Equals(other);
        public override int GetHashCode() => PackageName != null ? PackageName.GetHashCode() : 0;
    }

    // Stores real Android phone apps pinned via the star button so they appear in the general AppLibrary
    // and can launch directly in VR without opening the PhoneApps launcher list first.
    public static class PhoneAppShortcutStore
    {
        const string k_PrefsKey = "CardboardXR.StarredPhoneApps";

        static readonly List<StarredPhoneApp> s_Starred = new();
        static readonly Dictionary<string, Texture2D> s_OwnedTextures = new();
        static readonly Dictionary<string, byte[]> s_IconPngs = new();
        static bool s_Loaded;

        public static event Action Changed;
        public static Func<StarredPhoneApp, bool> DirectLaunchHandler { get; set; }
        public static Func<string, bool> IsPackageOpenHandler { get; set; }
        public static StarredPhoneApp? PendingDirectLaunch { get; set; }

        public static bool LaunchPackage(DemoManager demos, string packageName, string label = null)
        {
            if (string.IsNullOrWhiteSpace(packageName))
                return false;
            var pkg = packageName.Trim();
            EnsureLoaded();
            var app = new StarredPhoneApp
            {
                PackageName = pkg,
                ActivityName = "",
                Label = string.IsNullOrEmpty(label) ? pkg : label,
                ColorRgb = 0x4A6CF7,
            };
            for (var i = 0; i < s_Starred.Count; i++)
            {
                if (s_Starred[i].PackageName == pkg)
                {
                    app = s_Starred[i];
                    break;
                }
            }
            return LaunchStarredApp(demos, app);
        }

        public static bool LaunchStarredApp(DemoManager demos, StarredPhoneApp app)
        {
            if (string.IsNullOrWhiteSpace(app.PackageName))
                return false;
            if (DirectLaunchHandler != null && DirectLaunchHandler.Invoke(app))
                return true;
            if (demos == null || demos.Catalog == null)
                return false;
            DemoCatalog.Entry phoneAppsEntry = null;
            var entries = demos.Catalog.Demos;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i] != null && entries[i].Id == "phoneapps")
                {
                    phoneAppsEntry = entries[i];
                    break;
                }
            }
            if (phoneAppsEntry == null)
                return false;
            PendingDirectLaunch = app;
            demos.Enter(phoneAppsEntry);
            return true;
        }

        // Routes URL opens (such as external account/help links in VR apps) inside the in-VR Web Cinema
        // browser rather than calling Application.OpenURL, which would exit CardboardXR.
        public static class VrBrowserRouter
        {
            public static Func<string, bool> DirectOpenUrlHandler { get; set; }
            public static string PendingUrl { get; set; }

            public static bool OpenUrl(DemoManager demos, string url)
            {
                if (string.IsNullOrWhiteSpace(url))
                    return false;
                var trimmed = url.Trim();
                if (!trimmed.Contains("://"))
                    trimmed = "https://" + trimmed;
                if (DirectOpenUrlHandler != null && DirectOpenUrlHandler.Invoke(trimmed))
                    return true;
                if (demos == null || demos.Catalog == null)
                    return false;

                DemoCatalog.Entry browserEntry = null;
                var entries = demos.Catalog.Demos;
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] != null && entries[i].Id == "browser")
                    {
                        browserEntry = entries[i];
                        break;
                    }
                }
                if (browserEntry == null)
                    return false;
                PendingUrl = trimmed;
                demos.Enter(browserEntry);
                return true;
            }
        }

        public static IReadOnlyList<StarredPhoneApp> Starred
        {
            get
            {
                EnsureLoaded();
                return s_Starred;
            }
        }

        public static bool IsStarred(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return false;
            EnsureLoaded();
            for (var i = 0; i < s_Starred.Count; i++)
            {
                if (s_Starred[i].PackageName == packageName)
                    return true;
            }
            return false;
        }

        public static bool ToggleStar(string packageName, string activityName, string label, int colorRgb,
            Texture2D iconTex = null, byte[] iconPng = null)
        {
            if (string.IsNullOrEmpty(packageName))
                return false;
            EnsureLoaded();
            for (var i = 0; i < s_Starred.Count; i++)
            {
                if (s_Starred[i].PackageName != packageName)
                    continue;
                s_Starred.RemoveAt(i);
                RemoveCachedIcon(packageName);
                Save();
                Changed?.Invoke();
                return false;
            }

            s_Starred.Add(new StarredPhoneApp
            {
                PackageName = SanitizeField(packageName),
                ActivityName = SanitizeField(activityName ?? ""),
                Label = SanitizeField(string.IsNullOrWhiteSpace(label) ? packageName : label),
                ColorRgb = colorRgb,
            });
            CacheIcon(packageName, iconPng);
            Save();
            Changed?.Invoke();
            return true;
        }

        public static Texture2D GetIcon(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            if (s_OwnedTextures.TryGetValue(packageName, out var existing) && existing != null)
                return existing;

            if (!s_IconPngs.TryGetValue(packageName, out var bytes) || bytes == null || bytes.Length == 0)
            {
                bytes = TryReadIconFromDisk(packageName);
                if (bytes != null && bytes.Length > 0)
                    s_IconPngs[packageName] = bytes;
            }
            if (bytes == null || bytes.Length == 0)
                return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
            {
                s_OwnedTextures[packageName] = tex;
                return tex;
            }
            UnityEngine.Object.Destroy(tex);
            return null;
        }

        public static string Serialize(IReadOnlyList<StarredPhoneApp> items)
        {
            if (items == null || items.Count == 0)
                return "";
            var sb = new StringBuilder();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (string.IsNullOrEmpty(item.PackageName))
                    continue;
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(SanitizeField(item.PackageName))
                    .Append('\t').Append(SanitizeField(item.ActivityName ?? ""))
                    .Append('\t').Append(SanitizeField(string.IsNullOrWhiteSpace(item.Label) ? item.PackageName : item.Label))
                    .Append('\t').Append(item.ColorRgb.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static List<StarredPhoneApp> Deserialize(string raw)
        {
            var result = new List<StarredPhoneApp>();
            if (string.IsNullOrWhiteSpace(raw))
                return result;
            var seen = new HashSet<string>();
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[0]))
                    continue;
                var pkg = parts[0].Trim();
                if (!seen.Add(pkg))
                    continue;
                int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rgb);
                result.Add(new StarredPhoneApp
                {
                    PackageName = pkg,
                    ActivityName = parts[1].Trim(),
                    Label = string.IsNullOrWhiteSpace(parts[2]) ? pkg : parts[2].Trim(),
                    ColorRgb = rgb,
                });
            }
            return result;
        }

        static string SanitizeField(string value) =>
            (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        static void EnsureLoaded()
        {
            if (s_Loaded)
                return;
            s_Loaded = true;
            s_Starred.Clear();
            s_Starred.AddRange(Deserialize(PlayerPrefs.GetString(k_PrefsKey, "")));
        }

        static void Save()
        {
            PlayerPrefs.SetString(k_PrefsKey, Serialize(s_Starred));
            PlayerPrefs.Save();
        }

        static string IconPath(string packageName)
        {
            var safe = new StringBuilder(packageName.Length);
            foreach (var c in packageName)
                safe.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            return Path.Combine(Application.persistentDataPath, "phone_icons", safe + ".png");
        }

        static void CacheIcon(string packageName, byte[] iconPng)
        {
            if (iconPng == null || iconPng.Length == 0)
                return;
            s_IconPngs[packageName] = iconPng;
            if (s_OwnedTextures.TryGetValue(packageName, out var oldTex) && oldTex != null)
                UnityEngine.Object.Destroy(oldTex);
            s_OwnedTextures.Remove(packageName);
            try
            {
                var path = IconPath(packageName);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, iconPng);
            }
            catch (Exception) { }
        }

        static byte[] TryReadIconFromDisk(string packageName)
        {
            try
            {
                var path = IconPath(packageName);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch (Exception) { }
            return null;
        }

        static void RemoveCachedIcon(string packageName)
        {
            s_IconPngs.Remove(packageName);
            if (s_OwnedTextures.TryGetValue(packageName, out var tex) && tex != null)
                UnityEngine.Object.Destroy(tex);
            s_OwnedTextures.Remove(packageName);
            try
            {
                var path = IconPath(packageName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception) { }
        }
    }

    // The app library, like visionOS's or Quest's: a grid of app icons and names above the settings
    // panel, which its Apps button shows and hides. The lobby comes first, then each demo in the
    // catalog; pick one and the panel closes and that app opens, from wherever you are, so there's no
    // need to go back to the lobby between demos (DemoManager restores what one demo switched on
    // before applying the next's needs). The app running now is marked with a dot under its name.
    // Looking at an app shows its one-line tagline under the grid. Entering the lobby opens the
    // settings panel with the library showing (Open, from LobbyFlow), since the lobby is only a room
    // to start from. Part of the settings panel, so it follows the panel and
    // closes with it. Tiles are built at runtime from the catalog, so a new demo needs only a catalog
    // entry. A page holds k_Rows rows, so the library stays as short however many demos there are; with
    // more apps than that, arrows either side of the grid turn the page, dots under it say which page
    // it is, and it opens on the page with the app that's running. The "libraryRows" launch option
    // makes pages shorter, to try paging with few demos. Lives on an object that's always active (the
    // Demos object), and shows and hides m_Root. Logs [Demos].
    public class AppLibrary : MonoBehaviour
    {
        // In dp: each app's cell, its icon tile, and the gaps between cells and around the grid.
        public const float k_CellWidth = 128f;
        // Kept short, so it sits at eye level with the settings panel close below it.
        public const float k_CellHeight = 124f;
        const float k_IconSize = 76f;
        const float k_Gap = 8f;
        const float k_Padding = 16f;
        const float k_TaglineHeight = 28f;
        public const int k_Columns = 3;
        // Rows a page: two keep the library short enough to sit at eye level with the settings panel
        // close below it. A third row would push the panel 5 degrees further down, and each row after
        // that 5 more.
        public const int k_Rows = 2;
        // When there's more than a page: an arrow either side of the grid, and a row of page dots
        // between the grid and the tagline.
        const float k_ArrowSize = M3Theme.k_Target;
        const float k_DotsHeight = 14f;
        const float k_DotSize = 6f;
        const float k_ActiveDotWidth = 16f;
        const float k_DotSpacing = 12f;
        // Compact vertical smartphone form factor when docked on the user's open palm (2 cols x 3 rows).
        const int k_PalmColumns = 2;
        const int k_PalmRows = 3;
        const float k_PalmPagerRowHeight = 52f;
        internal const string ReorderPrompt = k_ReorderPrompt;
        const string k_ReorderPrompt = "Riordino attivo • Usa < / > o tocca due app per spostarle";
        const string k_OrderPrefsKey = "CardboardXR.AppLibraryOrder";
        public const string LobbyId = "lobby";
        public const string PhoneAppPrefix = "phoneapp:";
        // Picks this soon after the library appears are ignored: it opens where the user is looking,
        // and the pinch that opened it (or a flicker of it) landed on a tile 0.6 s later, reopening the
        // demo just left.
        const float k_PickGuardSeconds = 0.8f;

        struct LibraryItem
        {
            public string ItemId;
            public DemoCatalog.Entry Entry;
            public StarredPhoneApp? PhoneApp;
            public string Title;
            public string Tagline;
            public string Icon;
            public Color Tint;
            public Texture2D CustomIcon;
        }

        sealed class TileRecord
        {
            public LibraryItem Item;
            public GameObject Running;
            public GameObject ReorderControls;
            public M3Button MoveLeft;
            public M3Button MoveRight;
            public Image SelectionRing;
            public int Index;
        }

        sealed class EnvironmentBubbleRecord
        {
            public string AssetId;
            public int EnvIndex;
            public Image ActiveRing;
            public GameObject ActiveDot;
        }

        enum EnvPickerMode
        {
            MyEnvironments,
            PolyHavenCategories,
            PolyHavenBrowse,
        }

        public const float k_EnvBubbleSize = 82f;
        public const float k_EnvCellWidth = 120f;
        public const float k_EnvCellHeight = 114f;
        const float k_EnvColStep = 126f;
        const float k_EnvRowStep = 118f;
        const float k_EnvPickGuardSeconds = 0.35f;
        const int k_EnvBrowsePerPage = 12;
        const float k_EnvHeaderHeight = 56f;
        const float k_EnvFooterHeight = 38f;
        const float k_EnvMinPanelWidth = 560f;

        [SerializeField] DemoManager m_Demos;
        [SerializeField] SettingsSummoner m_Settings;
        [Tooltip("The settings panel's Apps button, shown selected while the library shows.")]
        [SerializeField] M3Button m_AppsButton;
        [Tooltip("The settings panel's Reorder toggle button.")]
        [SerializeField] M3Button m_ReorderButton;
        [Tooltip("The library's surface, under the settings panel, shown and hidden.")]
        [SerializeField] RectTransform m_Root;
        [Tooltip("Where the tiles go.")]
        [SerializeField] RectTransform m_Grid;
        [SerializeField] TMP_Text m_Tagline;
        [Header("The lobby, which isn't in the catalog")]
        [SerializeField] string m_LobbyTitle = "Lobby";
        [SerializeField] string m_LobbyTagline = "A quiet room to start from";
        [SerializeField] string m_LobbyIcon = "home";
        [SerializeField] Color m_LobbyTint = new(0.29f, 0.36f, 0.54f);

        static AppLibrary s_Instance;

        readonly List<TileRecord> m_Tiles = new();
        readonly List<GameObject> m_Cells = new();
        readonly List<Image> m_Dots = new();
        readonly List<string> m_OrderedIds = new();
        readonly List<EnvironmentBubbleRecord> m_EnvBubbles = new();
        readonly Dictionary<string, RawImage> m_EnvThumbImages = new(StringComparer.Ordinal);
        readonly Dictionary<string, TMP_Text> m_EnvProgressLabels = new(StringComparer.Ordinal);
        readonly Dictionary<string, GameObject> m_EnvProgressBadges = new(StringComparer.Ordinal);
        readonly Dictionary<string, GameObject> m_EnvDownloadedBadges = new(StringComparer.Ordinal);
        RectTransform m_EnvironmentsGrid;
        M3Button m_Previous;
        M3Button m_Next;
        M3Button m_EnvPrevious;
        M3Button m_EnvNext;
        Image m_TaglinePill;
        public const int k_HoneycombPerPage = 13;

        int m_Rows = k_Rows;
        int m_Columns = k_Columns;
        bool m_HasLibraryRowsOverride;
        bool m_PortraitMode;
        bool m_ReorderMode;
        string m_ReorderSelectedId;
        int m_Page;
        int m_Pages = 1;
        float m_ShownAt = float.NegativeInfinity;
        float m_EnvShownAt = float.NegativeInfinity;
        float m_LastRotaryTurn = float.NegativeInfinity;
        bool m_Built;
        bool m_EnvironmentsBuilt;
        bool m_EnvironmentsShown;

        EnvPickerMode m_EnvMode = EnvPickerMode.MyEnvironments;
        int m_EnvMyPage;
        int m_EnvBrowsePage;
        int m_EnvTotalPages = 1;
        string m_SelectedCategoryApiName;
        string m_SelectedCategoryTitleIt;
        string m_EnvSearchQuery = "";
        bool m_EnvManageMode;
        bool m_CatalogFetching;
        string m_EnvStatusMessage = "";
        IReadOnlyList<PolyHavenAssetEntry> m_PolyHavenCatalog;
        EnvironmentSearchKeyboard m_SearchKeyboard;

        public static bool IsReorderModeGlobal => s_Instance != null && s_Instance.m_ReorderMode;
        public bool IsReorderMode => m_ReorderMode;
        public static event Action<bool> ReorderModeChanged;
        public bool IsShown => !m_EnvironmentsShown && m_Root != null && M3Fade.IsShown(m_Root.gameObject);
        public bool IsEnvironmentsShown => m_EnvironmentsShown && m_Root != null && M3Fade.IsShown(m_Root.gameObject);
        // Which page shows, from 0, and how many there are.
        public int Page => m_Page;
        public int PageCount => m_Pages;

        public static int PerPage(int rows) => PerPage(rows, k_Columns);
        public static int PerPage(int rows, int columns) => Mathf.Max(1, columns) * Mathf.Max(1, rows);

        // How many pages this many apps take, at rows a page.
        public static int Pages(int apps, int rows = k_Rows) => Pages(apps, rows, k_Columns);
        public static int Pages(int apps, int rows, int columns) => Mathf.Max(1, (apps + PerPage(rows, columns) - 1) / PerPage(rows, columns));

        // The page an app is on, by its place in the library (the lobby is 0).
        public static int PageOf(int index, int rows = k_Rows) => PageOf(index, rows, k_Columns);
        public static int PageOf(int index, int rows, int columns) => Mathf.Max(0, index) / PerPage(rows, columns);

        // The library's size in dp for this many apps: the grid, as many rows as a page holds at most,
        // and the tagline under it; with more than a page, the arrows beside the grid and the page
        // dots under it too (or in a compact bottom pager bar in vertical smartphone palm mode).
        public static Vector2 Size(int apps, int rows = k_Rows) => Size(apps, rows, k_Columns, portrait: false);

        public static Vector2 Size(int apps, int rows, int columns, bool portrait)
        {
            var paged = Pages(apps, rows, columns) > 1;
            var shown = paged ? Mathf.Max(1, rows) : Mathf.Max(1, (apps + columns - 1) / columns);
            var width = columns * k_CellWidth + (columns - 1) * k_Gap + 2f * k_Padding;
            var height = shown * k_CellHeight + (shown - 1) * k_Gap + k_Gap + k_TaglineHeight + 2f * k_Padding;
            if (paged)
            {
                if (portrait)
                {
                    height += k_PalmPagerRowHeight;
                }
                else
                {
                    width += 2f * (k_ArrowSize + k_Gap);
                    height += k_DotsHeight;
                }
            }
            return new Vector2(width, height);
        }

        // Where an app's cell goes, its middle from the library's middle, in dp: the same place on its
        // own page as the app that many pages before it.
        public static Vector2 CellPosition(int index, int apps, int rows = k_Rows) =>
            CellPosition(index, apps, rows, k_Columns, portrait: false);

        public static Vector2 CellPosition(int index, int apps, int rows, int columns, bool portrait)
        {
            var size = Size(apps, rows, columns, portrait);
            var slot = index % PerPage(rows, columns);
            var column = slot % columns;
            var row = slot / columns;
            var sideArrows = !portrait && Pages(apps, rows, columns) > 1 ? k_ArrowSize + k_Gap : 0f;
            var left = -size.x / 2f + k_Padding + sideArrows;
            var x = left + k_CellWidth / 2f + column * (k_CellWidth + k_Gap);
            var y = size.y / 2f - k_Padding - k_CellHeight / 2f - row * (k_CellHeight + k_Gap);
            return new Vector2(x, y);
        }

        // The tagline's middle: at the bottom, under the grid (and the page dots).
        public static Vector2 TaglinePosition(Vector2 size) => new(0f, -size.y / 2f + k_Padding + k_TaglineHeight / 2f);

        public static int HoneycombPages(int apps) => Mathf.Max(1, (apps + k_HoneycombPerPage - 1) / k_HoneycombPerPage);

        public static int HoneycombPageOf(int index) => Mathf.Max(0, index) / k_HoneycombPerPage;

        public static Vector2 HoneycombSize(int apps)
        {
            var paged = HoneycombPages(apps) > 1;
            var pageCount = paged ? k_HoneycombPerPage : Mathf.Max(1, apps);
            var counts = EnvironmentRowCounts(pageCount);
            var maxCols = 1;
            for (var i = 0; i < counts.Length; i++)
                maxCols = Mathf.Max(maxCols, counts[i]);
            var rowCount = Mathf.Max(1, counts.Length);
            var width = maxCols * k_CellWidth + (maxCols - 1) * k_Gap + 2f * k_Padding;
            var height = rowCount * k_CellHeight + (rowCount - 1) * k_Gap + k_Gap + k_TaglineHeight + 2f * k_Padding;
            if (paged)
            {
                width += 2f * (k_ArrowSize + k_Gap);
                height += k_DotsHeight;
            }
            return new Vector2(width, height);
        }

        public static Vector2 HoneycombCellPosition(int index, int apps)
        {
            var size = HoneycombSize(apps);
            var page = HoneycombPageOf(index);
            var pageCount = Mathf.Clamp(apps - page * k_HoneycombPerPage, 1, k_HoneycombPerPage);
            var slot = Mathf.Max(0, index) % k_HoneycombPerPage;
            var counts = EnvironmentRowCounts(pageCount);
            var rowStart = 0;
            for (var r = 0; r < counts.Length; r++)
            {
                var rowCols = counts[r];
                if (slot < rowStart + rowCols)
                {
                    var col = slot - rowStart;
                    var x = (col - (rowCols - 1) * 0.5f) * (k_CellWidth + k_Gap);
                    var y = size.y * 0.5f - k_Padding - k_CellHeight * 0.5f - r * (k_CellHeight + k_Gap);
                    return new Vector2(x, y);
                }
                rowStart += rowCols;
            }
            return Vector2.zero;
        }

        bool UseHoneycombApps => !m_PortraitMode && !m_HasLibraryRowsOverride;
        int CurrentPerPage() => UseHoneycombApps ? k_HoneycombPerPage : PerPage(m_Rows, m_Columns);
        int CurrentPages(int apps) => UseHoneycombApps ? HoneycombPages(apps) : Pages(apps, m_Rows, m_Columns);
        int CurrentPageOf(int index) => UseHoneycombApps ? HoneycombPageOf(index) : PageOf(index, m_Rows, m_Columns);
        Vector2 CurrentPanelSize(int apps) => UseHoneycombApps ? HoneycombSize(apps) : Size(apps, m_Rows, m_Columns, m_PortraitMode);
        Vector2 CurrentCellPosition(int index, int apps) => UseHoneycombApps ? HoneycombCellPosition(index, apps) : CellPosition(index, apps, m_Rows, m_Columns, m_PortraitMode);

        public static int[] EnvironmentRowCounts(int totalCount)
        {
            if (totalCount <= 0)
                return Array.Empty<int>();
            if (totalCount == 13)
                return new[] { 4, 5, 4 };
            if (totalCount <= 4)
                return new[] { totalCount };
            if (totalCount <= 9)
            {
                var top = totalCount / 2;
                return new[] { top, totalCount - top };
            }
            if (totalCount <= 15)
            {
                var top = (totalCount - 1) / 3;
                var mid = Mathf.Min(5, totalCount - 2 * top);
                var bot = totalCount - top - mid;
                if (bot > 5)
                {
                    top++;
                    bot--;
                }
                return new[] { top, mid, bot };
            }
            var rowCount = (totalCount + 4) / 5;
            var rows = new int[rowCount];
            var remaining = totalCount;
            for (var r = 0; r < rowCount; r++)
            {
                var leftRows = rowCount - r;
                var take = (remaining + leftRows - 1) / leftRows;
                rows[r] = take;
                remaining -= take;
            }
            return rows;
        }

        public static Vector2 EnvironmentGridSize(int totalCount)
        {
            var counts = EnvironmentRowCounts(totalCount);
            if (counts.Length == 0)
                return new Vector2(k_EnvCellWidth + 2f * k_Padding, k_EnvCellHeight + 2f * k_Padding);
            var maxCols = 1;
            for (var i = 0; i < counts.Length; i++)
                maxCols = Mathf.Max(maxCols, counts[i]);
            var width = (maxCols - 1) * k_EnvColStep + k_EnvCellWidth + 2f * k_Padding;
            var height = (counts.Length - 1) * k_EnvRowStep + k_EnvCellHeight + 2f * k_Padding;
            return new Vector2(width, height);
        }

        public static Vector2 EnvironmentBubblePosition(int index, int totalCount)
        {
            var counts = EnvironmentRowCounts(totalCount);
            if (counts.Length == 0)
                return Vector2.zero;
            var clamped = Mathf.Clamp(index, 0, totalCount - 1);
            var rowStart = 0;
            for (var r = 0; r < counts.Length; r++)
            {
                var rowCols = counts[r];
                if (clamped < rowStart + rowCols)
                {
                    var col = clamped - rowStart;
                    var x = (col - (rowCols - 1) * 0.5f) * k_EnvColStep;
                    var y = ((counts.Length - 1) * 0.5f - r) * k_EnvRowStep;
                    return new Vector2(x, y);
                }
                rowStart += rowCols;
            }
            return Vector2.zero;
        }

        public static List<string> ApplySavedOrder(IReadOnlyList<string> defaultIds, string savedCsv)
        {
            var result = new List<string>();
            if (defaultIds == null || defaultIds.Count == 0)
                return result;

            var allowed = new HashSet<string>(defaultIds);
            var added = new HashSet<string>();
            if (!string.IsNullOrWhiteSpace(savedCsv))
            {
                var parts = savedCsv.Split(',');
                for (var i = 0; i < parts.Length; i++)
                {
                    var id = parts[i].Trim();
                    if (!string.IsNullOrEmpty(id) && allowed.Contains(id) && added.Add(id))
                        result.Add(id);
                }
            }
            for (var i = 0; i < defaultIds.Count; i++)
            {
                var id = defaultIds[i];
                if (!string.IsNullOrEmpty(id) && added.Add(id))
                    result.Add(id);
            }
            return result;
        }

        public static bool MoveItem(List<string> ids, int fromIndex, int delta)
        {
            if (ids == null || delta == 0 || fromIndex < 0 || fromIndex >= ids.Count)
                return false;
            var targetIndex = fromIndex + delta;
            if (targetIndex < 0 || targetIndex >= ids.Count)
                return false;
            var item = ids[fromIndex];
            ids.RemoveAt(fromIndex);
            ids.Insert(targetIndex, item);
            return true;
        }

        public static void ToggleReorderGlobal() => s_Instance?.ToggleReorderMode();

        public static void DismissGlobal()
        {
            if (s_Instance == null)
                return;
            if (s_Instance.IsEnvironmentsShown)
                s_Instance.ShowEnvironments(false);
            if (s_Instance.IsShown)
                s_Instance.Show(false);
            if (s_Instance.m_Settings != null && s_Instance.m_Settings.IsOpen)
                s_Instance.m_Settings.Close();
        }

        public void ToggleReorderMode()
        {
            var next = !m_ReorderMode;
            SetReorderMode(next);
            if (next && !IsShown)
                Open();
        }

        void SetReorderMode(bool active)
        {
            if (m_ReorderMode == active)
                return;
            m_ReorderMode = active;
            m_ReorderSelectedId = null;
            if (m_ReorderButton != null)
                m_ReorderButton.Selected = active;
            SettingsPanelHub.NotifyReorderMode(active);
            ReorderModeChanged?.Invoke(active);
            RefreshReorderUi();
            if (IsShown)
                UpdateTaglineVisual();
        }

        void Awake()
        {
            s_Instance = this;
            m_HasLibraryRowsOverride = LaunchOptions.GetInt("libraryRows", 0) > 0;
            m_Rows = Mathf.Clamp(LaunchOptions.GetInt("libraryRows", k_Rows), 1, 4);
            m_Columns = k_Columns;
            if (m_Settings != null)
            {
                m_Settings.OpenChanged += OnSettingsOpenChanged;
                m_Settings.PalmOpened += OnPalmOpened;
                m_Settings.PalmModeChanged += SetPortraitMode;
            }
            SettingsPanelHub.AppsRequested += Open;
            SettingsPanelHub.EnvironmentsRequested += OpenEnvironments;
            SettingsPanelHub.EnvironmentsToggleRequested += ToggleEnvironments;
            SettingsPanelHub.ReorderToggleRequested += ToggleReorderMode;
            PhoneAppShortcutStore.Changed += OnShortcutStoreChanged;
            EnvironmentManager.IsolationChanged += OnEnvironmentStateChanged;
            EnvironmentManager.EnvironmentChanged += OnEnvironmentNameChanged;
            EnvironmentLibraryStore.Changed += OnEnvironmentLibraryChanged;
            PolyHavenService.DownloadProgressChanged += OnPolyHavenDownloadProgress;
            PolyHavenService.DownloadFinished += OnPolyHavenDownloadFinished;
            SettingsSummoner.AboveScrollHandler = TryRotaryTurn;
            ApplyBackgroundStyle();
            Layout();
        }

        void OnPalmOpened()
        {
            ShowEnvironments(false);
            SetPortraitMode(true);
            Show(true);
        }

        // Switches dynamically between compact vertical smartphone layout (2x3 on open palm) and
        // full landscape VisionOS layout (3x2 when floating in space with zero opaque background).
        public void SetPortraitMode(bool portrait)
        {
            if (m_PortraitMode == portrait && m_Built)
            {
                ApplyBackgroundStyle();
                return;
            }
            m_PortraitMode = portrait;
            m_Columns = portrait ? k_PalmColumns : k_Columns;
            m_Rows = portrait ? k_PalmRows : Mathf.Clamp(LaunchOptions.GetInt("libraryRows", k_Rows), 1, 4);
            ApplyBackgroundStyle();
            Layout();
            if (m_Built)
                ReflowBuiltGrid();
        }

        void ApplyBackgroundStyle()
        {
            if (m_Root != null && m_Root.TryGetComponent<Image>(out var rootBg))
                rootBg.color = m_PortraitMode ? M3Theme.SurfaceContainer : Color.clear;
        }

        void EnsureTaglinePill(Vector2 panelSize)
        {
            if (m_Tagline == null || m_Root == null)
                return;
            if (m_TaglinePill == null)
            {
                m_TaglinePill = M3.Surface("Tagline Pill", m_Root, TaglinePosition(panelSize),
                    new Vector2(Mathf.Min(panelSize.x - 24f, 440f), 30f), M3Theme.SurfaceContainer, M3Theme.k_ShapeFull);
                m_TaglinePill.transform.SetSiblingIndex(m_Tagline.transform.GetSiblingIndex());
            }
            m_TaglinePill.rectTransform.anchoredPosition = TaglinePosition(panelSize);
            m_TaglinePill.rectTransform.sizeDelta = new Vector2(Mathf.Min(panelSize.x - 24f, 440f), 30f);
            UpdateTaglineVisual();
        }

        void UpdateTaglineVisual()
        {
            if (m_Tagline != null)
            {
                m_Tagline.text = m_ReorderMode ? k_ReorderPrompt : "";
                m_Tagline.gameObject.SetActive(m_ReorderMode);
            }
            if (m_TaglinePill != null)
                m_TaglinePill.gameObject.SetActive(m_ReorderMode && !m_PortraitMode);
        }

        // Sizes the surface and places the tagline for the catalog's apps at the current grid shape.
        void Layout()
        {
            if (m_EnvironmentsShown)
            {
                var envSize = CurrentEnvironmentPanelSize();
                if (m_Root != null)
                    m_Root.sizeDelta = envSize;
                if (m_EnvironmentsGrid != null)
                    m_EnvironmentsGrid.sizeDelta = envSize;
                return;
            }
            LayoutForCount(TotalAppCount());
        }

        int TotalEnvironmentBubbleCount => EnvironmentManager.EnvironmentCount + 2;

        void LayoutForCount(int apps)
        {
            if (m_Demos == null || m_Demos.Catalog == null || m_Root == null)
                return;
            var size = CurrentPanelSize(apps);
            m_Root.sizeDelta = size;
            if (m_Grid != null)
                m_Grid.sizeDelta = size;
            if (m_Tagline != null)
            {
                m_Tagline.rectTransform.anchoredPosition = TaglinePosition(size);
                m_Tagline.rectTransform.sizeDelta = new Vector2(size.x - 36f, k_TaglineHeight);
                EnsureTaglinePill(size);
            }
        }

        int TotalAppCount()
        {
            if (m_Demos == null || m_Demos.Catalog == null)
                return 1;
            return m_Demos.Catalog.Demos.Count + PhoneAppShortcutStore.Starred.Count + 1;
        }

        void ReflowBuiltGrid()
        {
            if (m_Demos == null || m_Demos.Catalog == null)
                return;
            var apps = Mathf.Max(1, m_Cells.Count);
            m_Pages = CurrentPages(apps);
            for (var i = 0; i < m_Cells.Count; i++)
            {
                if (m_Cells[i] != null)
                    ((RectTransform)m_Cells[i].transform).anchoredPosition = CurrentCellPosition(i, apps);
            }
            RebuildPaging(apps);
            UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
            ShowPage(CurrentPageOf(RunningIndex()));
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            if (m_Settings != null)
            {
                m_Settings.OpenChanged -= OnSettingsOpenChanged;
                m_Settings.PalmOpened -= OnPalmOpened;
                m_Settings.PalmModeChanged -= SetPortraitMode;
            }
            SettingsPanelHub.AppsRequested -= Open;
            SettingsPanelHub.EnvironmentsRequested -= OpenEnvironments;
            SettingsPanelHub.EnvironmentsToggleRequested -= ToggleEnvironments;
            SettingsPanelHub.ReorderToggleRequested -= ToggleReorderMode;
            PhoneAppShortcutStore.Changed -= OnShortcutStoreChanged;
            EnvironmentManager.IsolationChanged -= OnEnvironmentStateChanged;
            EnvironmentManager.EnvironmentChanged -= OnEnvironmentNameChanged;
            EnvironmentLibraryStore.Changed -= OnEnvironmentLibraryChanged;
            PolyHavenService.DownloadProgressChanged -= OnPolyHavenDownloadProgress;
            PolyHavenService.DownloadFinished -= OnPolyHavenDownloadFinished;
            if (SettingsSummoner.AboveScrollHandler == TryRotaryTurn)
                SettingsSummoner.AboveScrollHandler = null;
            SettingsPanelHub.NotifyReorderMode(false);
            SettingsPanelHub.NotifyEnvironmentsOpen(false);
            if (m_SearchKeyboard != null)
            {
                m_SearchKeyboard.Hide();
                Destroy(m_SearchKeyboard.gameObject);
                m_SearchKeyboard = null;
            }
            m_EnvThumbImages.Clear();
            m_EnvProgressLabels.Clear();
            m_EnvProgressBadges.Clear();
            m_EnvDownloadedBadges.Clear();
        }

        void OnShortcutStoreChanged()
        {
            if (m_Built)
                RebuildLibrary(preservePage: true);
        }

        bool TryRotaryTurn(GameObject target, float deltaMeters)
        {
            if (target == null || m_Root == null || !target.transform.IsChildOf(m_Root))
                return false;
            var dir = deltaMeters > 0.02f ? 1 : (deltaMeters < -0.02f ? -1 : 0);
            if (dir == 0)
                return false;

            if (IsEnvironmentsShown && m_EnvTotalPages > 1)
            {
                var nextEnv = CurrentEnvPage + dir;
                if (nextEnv < 0 || nextEnv >= m_EnvTotalPages)
                    return false;
                if (Time.unscaledTime - m_LastRotaryTurn < 0.28f)
                    return true;
                m_LastRotaryTurn = Time.unscaledTime;
                TurnEnvironments(dir);
                return true;
            }

            if (!IsShown || m_Pages <= 1)
                return false;
            var next = m_Page + dir;
            if (next < 0 || next >= m_Pages)
                return false;
            if (Time.unscaledTime - m_LastRotaryTurn < 0.28f)
                return true;
            m_LastRotaryTurn = Time.unscaledTime;
            Turn(dir);
            return true;
        }

        // The Apps button.
        public void Toggle() => Show(!IsShown);

        public void Show(bool show)
        {
            if (show)
            {
                var wasEnvShown = m_EnvironmentsShown;
                m_EnvironmentsShown = false;
                if (m_EnvironmentsGrid != null)
                    m_EnvironmentsGrid.gameObject.SetActive(false);
                m_SearchKeyboard?.Hide();
                SettingsPanelHub.NotifyEnvironmentsOpen(false);
                var hadShown = IsShown;
                ApplyBackgroundStyle();
                Build();
                if (m_Grid != null)
                    m_Grid.gameObject.SetActive(true);
                Layout();
                if (!hadShown)
                {
                    m_ShownAt = Time.unscaledTime;
                    ShowPage(CurrentPageOf(RunningIndex()));
                }
                M3Fade.SetShown(m_Root.gameObject, true);
                UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
                if (m_AppsButton != null)
                    m_AppsButton.Selected = true;
                UpdateTaglineVisual();
                MarkRunning();
                RefreshReorderUi();
                if (wasEnvShown || !hadShown)
                    m_Settings?.RefreshPlacement();
                return;
            }

            if (m_EnvironmentsShown)
            {
                if (m_Grid != null)
                    m_Grid.gameObject.SetActive(false);
            }
            else if (m_Root != null)
            {
                M3Fade.Hide(m_Root.gameObject, () =>
                {
                    if (!IsShown && m_Grid != null)
                        m_Grid.gameObject.SetActive(false);
                });
            }
            else if (m_Grid != null)
            {
                m_Grid.gameObject.SetActive(false);
            }
            if (m_AppsButton != null)
                m_AppsButton.Selected = false;
            if (m_ReorderMode)
                SetReorderMode(false);
        }

        // The Ambienti button: hides the apps grid and presents all environments in circular visionOS bubbles.
        public void OpenEnvironments()
        {
            if (m_Settings != null)
                m_Settings.Open();
            ShowEnvironments(true);
        }

        public void ToggleEnvironments() => ShowEnvironments(!IsEnvironmentsShown);

        public void ShowEnvironments(bool show)
        {
            if (m_Root == null)
                return;
            if (show)
            {
                if (m_ReorderMode)
                    SetReorderMode(false);
                if (m_Grid != null)
                    m_Grid.gameObject.SetActive(false);
                if (m_Tagline != null)
                    m_Tagline.gameObject.SetActive(false);
                if (m_TaglinePill != null)
                    m_TaglinePill.gameObject.SetActive(false);
                if (m_AppsButton != null)
                    m_AppsButton.Selected = false;
                if (m_Root.TryGetComponent<Image>(out var rootBg))
                    rootBg.color = Color.clear;

                var wasOpen = IsEnvironmentsShown;
                if (!wasOpen)
                {
                    m_EnvShownAt = Time.unscaledTime;
                    m_EnvManageMode = false;
                }
                if (m_Root != null && !m_Root.TryGetComponent<AppLibraryScrollTarget>(out _))
                    m_Root.gameObject.AddComponent<AppLibraryScrollTarget>().Initialize(this);

                m_EnvironmentsShown = true;
                RebuildEnvironmentsGrid();
                if (m_EnvironmentsGrid != null)
                    m_EnvironmentsGrid.gameObject.SetActive(true);
                M3Fade.SetShown(m_Root.gameObject, true);
                UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
                RefreshEnvironmentSelection();
                m_Settings?.RefreshPlacement();
                SettingsPanelHub.NotifyEnvironmentsOpen(true);
                return;
            }

            var wasShown = m_EnvironmentsShown;
            m_EnvironmentsShown = false;
            m_EnvManageMode = false;
            m_SearchKeyboard?.Hide();
            if (wasShown && (m_Grid == null || !m_Grid.gameObject.activeSelf) && m_Root != null)
            {
                M3Fade.Hide(m_Root.gameObject, () =>
                {
                    if (!m_EnvironmentsShown && m_EnvironmentsGrid != null)
                        m_EnvironmentsGrid.gameObject.SetActive(false);
                });
            }
            else if (m_EnvironmentsGrid != null)
            {
                m_EnvironmentsGrid.gameObject.SetActive(false);
            }
            SettingsPanelHub.NotifyEnvironmentsOpen(false);
        }

        void OnEnvironmentStateChanged(bool _) => RefreshEnvironmentSelection();

        void OnEnvironmentNameChanged(string _) => RefreshEnvironmentSelection();

        // The settings panel, opened in front of the user, with the library showing: how the lobby
        // greets the user (LobbyFlow).
        public void Open()
        {
            if (m_Settings != null)
                m_Settings.Open();
            Show(true);
        }

        // Closed with the panel, so it opens next time as the panel alone.
        void OnSettingsOpenChanged(bool open)
        {
            if (!open)
            {
                if (m_ReorderMode)
                    SetReorderMode(false);
                if (m_EnvironmentsShown)
                    ShowEnvironments(false);
                if (IsShown)
                    Show(false);
            }
        }

        void PickItem(LibraryItem item)
        {
            if (m_ReorderMode)
            {
                HandleReorderTileClick(item.ItemId);
                return;
            }
            if (item.PhoneApp.HasValue)
            {
                PickPhoneApp(item.PhoneApp.Value);
                return;
            }
            Pick(item.Entry);
        }

        void PickPhoneApp(StarredPhoneApp app)
        {
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of {app.Label} {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            Show(false);
            if (m_Settings != null)
                m_Settings.Close();

            Debug.Log($"[Demos] Picked starred phone app {app.Label} ({app.PackageName}) in the app library");
            PhoneAppShortcutStore.LaunchStarredApp(m_Demos, app);
        }

        DemoCatalog.Entry FindDemoById(string id)
        {
            if (m_Demos == null || m_Demos.Catalog == null)
                return null;
            var demos = m_Demos.Catalog.Demos;
            for (var i = 0; i < demos.Count; i++)
            {
                if (demos[i] != null && demos[i].Id == id)
                    return demos[i];
            }
            return null;
        }

        void Pick(DemoCatalog.Entry entry)
        {
            var title = entry != null ? entry.Title : m_LobbyTitle;
            if (Time.unscaledTime - m_ShownAt < k_PickGuardSeconds)
            {
                Debug.Log($"[Demos] Ignored a pick of {title} {Time.unscaledTime - m_ShownAt:F2} s after the library appeared");
                return;
            }
            Show(false);
            if (m_Settings != null)
                m_Settings.Close();
            if (entry == null)
            {
                if (m_Demos.IsRunning(null))
                {
                    Debug.Log($"[Demos] Picked {title} in the app library: already in lobby");
                    return;
                }
                Debug.Log($"[Demos] Picked {title} in the app library");
                m_Demos.ExitToLobby();
                return;
            }
            Debug.Log($"[Demos] Picked {title} in the app library");
            m_Demos.Enter(entry);
        }

        void MarkRunning()
        {
            foreach (var tile in m_Tiles)
            {
                var isRunning = tile.Item.PhoneApp.HasValue
                    ? (PhoneAppShortcutStore.IsPackageOpenHandler != null &&
                       PhoneAppShortcutStore.IsPackageOpenHandler(tile.Item.PhoneApp.Value.PackageName))
                    : (m_Demos != null && m_Demos.IsRunning(tile.Item.Entry));
                tile.Running.SetActive(isRunning);
            }
        }

        // The running app's place in the library: 0 for the lobby, or matching the reordered tile list.
        int RunningIndex()
        {
            var current = m_Demos != null ? m_Demos.Current : null;
            for (var i = 0; i < m_Tiles.Count; i++)
            {
                if (!m_Tiles[i].Item.PhoneApp.HasValue && m_Tiles[i].Item.Entry == current)
                    return i;
            }
            return 0;
        }

        // The lobby's tile, catalog demos, and starred phone apps in user-saved order;
        // rebuilds cleanly when apps are starred/unstarred or reordered.
        public void Build()
        {
            if (m_Built)
                return;
            RebuildLibrary(preservePage: false);
        }

        void RebuildLibrary(bool preservePage)
        {
            if (m_Demos == null || m_Demos.Catalog == null)
                return;
            var prevPage = m_Page;
            m_Built = true;
            for (var i = 0; i < m_Cells.Count; i++)
            {
                if (m_Cells[i] != null)
                    Destroy(m_Cells[i]);
            }
            m_Cells.Clear();
            m_Tiles.Clear();

            var byId = new Dictionary<string, LibraryItem>();
            var defaultIds = new List<string> { LobbyId };
            byId[LobbyId] = new LibraryItem
            {
                ItemId = LobbyId,
                Entry = null,
                Title = m_LobbyTitle,
                Tagline = m_LobbyTagline,
                Icon = m_LobbyIcon,
                Tint = m_LobbyTint,
            };

            var demos = m_Demos.Catalog.Demos;
            for (var i = 0; i < demos.Count; i++)
            {
                var d = demos[i];
                if (d == null || string.IsNullOrEmpty(d.Id) || byId.ContainsKey(d.Id))
                    continue;
                defaultIds.Add(d.Id);
                byId[d.Id] = new LibraryItem
                {
                    ItemId = d.Id,
                    Entry = d,
                    Title = d.Title,
                    Tagline = d.Tagline,
                    Icon = d.Icon,
                    Tint = d.Tint,
                };
            }

            var starred = PhoneAppShortcutStore.Starred;
            for (var i = 0; i < starred.Count; i++)
            {
                var app = starred[i];
                if (string.IsNullOrEmpty(app.PackageName) || byId.ContainsKey(app.ItemId))
                    continue;
                defaultIds.Add(app.ItemId);
                byId[app.ItemId] = new LibraryItem
                {
                    ItemId = app.ItemId,
                    PhoneApp = app,
                    Title = app.Label,
                    Tagline = $"Apri {app.Label} dal telefono",
                    Icon = "smartphone",
                    Tint = app.Tint,
                    CustomIcon = PhoneAppShortcutStore.GetIcon(app.PackageName),
                };
            }

            m_OrderedIds.Clear();
            m_OrderedIds.AddRange(ApplySavedOrder(defaultIds, PlayerPrefs.GetString(k_OrderPrefsKey, "")));
            var apps = m_OrderedIds.Count;
            LayoutForCount(apps);
            m_Pages = CurrentPages(apps);

            for (var i = 0; i < m_OrderedIds.Count; i++)
                Tile(i, apps, byId[m_OrderedIds[i]]);

            RebuildPaging(apps);
            if (m_Root != null && !m_Root.TryGetComponent<AppLibraryScrollTarget>(out _))
                m_Root.gameObject.AddComponent<AppLibraryScrollTarget>().Initialize(this);
            UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
            RefreshReorderUi();
            MarkRunning();
            ShowPage(preservePage ? Mathf.Clamp(prevPage, 0, m_Pages - 1) : 0);
        }

        void MoveItemAt(int fromIndex, int delta)
        {
            if (fromIndex < 0 || fromIndex >= m_OrderedIds.Count)
                return;
            var targetIndex = fromIndex + delta;
            if (!MoveItem(m_OrderedIds, fromIndex, delta))
                return;
            m_ReorderSelectedId = null;
            SaveOrder();
            RebuildLibrary(preservePage: true);
            ShowPage(CurrentPageOf(targetIndex));
        }

        void HandleReorderTileClick(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
                return;
            if (string.IsNullOrEmpty(m_ReorderSelectedId))
            {
                m_ReorderSelectedId = itemId;
                RefreshReorderUi();
                return;
            }
            if (m_ReorderSelectedId == itemId)
            {
                m_ReorderSelectedId = null;
                RefreshReorderUi();
                return;
            }
            var idxA = m_OrderedIds.IndexOf(m_ReorderSelectedId);
            var idxB = m_OrderedIds.IndexOf(itemId);
            m_ReorderSelectedId = null;
            if (idxA >= 0 && idxB >= 0)
            {
                (m_OrderedIds[idxA], m_OrderedIds[idxB]) = (m_OrderedIds[idxB], m_OrderedIds[idxA]);
                SaveOrder();
                RebuildLibrary(preservePage: true);
                ShowPage(CurrentPageOf(idxB));
                return;
            }
            RefreshReorderUi();
        }

        void SaveOrder()
        {
            PlayerPrefs.SetString(k_OrderPrefsKey, string.Join(",", m_OrderedIds));
            PlayerPrefs.Save();
        }

        void RefreshReorderUi()
        {
            for (var i = 0; i < m_Tiles.Count; i++)
            {
                var tile = m_Tiles[i];
                if (tile.ReorderControls != null)
                    tile.ReorderControls.SetActive(m_ReorderMode);
                if (tile.MoveLeft != null)
                    tile.MoveLeft.Interactable = m_ReorderMode && tile.Index > 0;
                if (tile.MoveRight != null)
                    tile.MoveRight.Interactable = m_ReorderMode && tile.Index < m_Tiles.Count - 1;
                if (tile.SelectionRing != null)
                    tile.SelectionRing.enabled = m_ReorderMode && !string.IsNullOrEmpty(m_ReorderSelectedId) &&
                                                 tile.Item.ItemId == m_ReorderSelectedId;
            }
        }

        void RebuildPaging(int apps)
        {
            if (m_Previous != null)
            {
                Destroy(m_Previous.gameObject);
                m_Previous = null;
            }
            if (m_Next != null)
            {
                Destroy(m_Next.gameObject);
                m_Next = null;
            }
            for (var i = 0; i < m_Dots.Count; i++)
            {
                if (m_Dots[i] != null)
                    Destroy(m_Dots[i].gameObject);
            }
            m_Dots.Clear();
            if (m_Pages <= 1)
                return;

            var size = CurrentPanelSize(apps);
            if (m_PortraitMode)
            {
                // Compact bottom pager row inside the vertical smartphone frame (<  • •  >).
                var pagerY = -size.y / 2f + k_Padding + k_TaglineHeight + k_PalmPagerRowHeight * 0.5f;
                var arrowX = size.x / 2f - k_Padding - 25f;
                m_Previous = M3.IconButton("Previous Apps", m_Grid, "chevron_left", M3Button.Variant.Tonal,
                    new Vector2(-arrowX, pagerY), () => Turn(-1), 46f);
                m_Next = M3.IconButton("Next Apps", m_Grid, "chevron_right", M3Button.Variant.Tonal,
                    new Vector2(arrowX, pagerY), () => Turn(1), 46f);
                for (var page = 0; page < m_Pages; page++)
                {
                    var dotX = (page - (m_Pages - 1) / 2f) * k_DotSpacing;
                    m_Dots.Add(M3.Surface($"Page {page + 1}", m_Grid, new Vector2(dotX, pagerY), Vector2.one * k_DotSize, M3Theme.Outline,
                        M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Pure));
                }
            }
            else
            {
                var rowCount = UseHoneycombApps
                    ? Mathf.Max(1, EnvironmentRowCounts(k_HoneycombPerPage).Length)
                    : m_Rows;
                var gridHeight = rowCount * k_CellHeight + (rowCount - 1) * k_Gap;
                var y = size.y / 2f - k_Padding - gridHeight / 2f;
                var x = size.x / 2f - k_Padding - k_ArrowSize / 2f;
                m_Previous = M3.IconButton("Previous Apps", m_Grid, "chevron_left", M3Button.Variant.Tonal, new Vector2(-x, y), () => Turn(-1));
                m_Next = M3.IconButton("Next Apps", m_Grid, "chevron_right", M3Button.Variant.Tonal, new Vector2(x, y), () => Turn(1));
                var dotsY = -size.y / 2f + k_Padding + k_TaglineHeight + k_DotsHeight / 2f;
                for (var page = 0; page < m_Pages; page++)
                {
                    var dotX = (page - (m_Pages - 1) / 2f) * k_DotSpacing;
                    m_Dots.Add(M3.Surface($"Page {page + 1}", m_Grid, new Vector2(dotX, dotsY), Vector2.one * k_DotSize, M3Theme.Outline,
                        M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Pure));
                }
            }
            UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
        }

        // An arrow: the page before or after, stopping at the first and last.
        internal void Turn(int step)
        {
            var page = Mathf.Clamp(m_Page + step, 0, m_Pages - 1);
            if (page == m_Page)
                return;
            ShowPage(page);
            Debug.Log($"[Demos] App library page {m_Page + 1} of {m_Pages}");
        }

        // Shows that page's tiles only, marks its dot, and dims an arrow that has no page to go to.
        void ShowPage(int page)
        {
            m_Page = Mathf.Clamp(page, 0, m_Pages - 1);
            var perPage = CurrentPerPage();
            for (var i = 0; i < m_Cells.Count; i++)
                m_Cells[i].SetActive(i / perPage == m_Page);
            for (var i = 0; i < m_Dots.Count; i++)
            {
                var active = i == m_Page;
                m_Dots[i].color = active ? M3Theme.Primary : M3Theme.Outline;
                m_Dots[i].rectTransform.sizeDelta = new Vector2(active ? k_ActiveDotWidth : k_DotSize, k_DotSize);
            }
            if (m_Previous != null)
                m_Previous.Interactable = m_Page > 0;
            if (m_Next != null)
                m_Next.Interactable = m_Page < m_Pages - 1;
            UpdateTaglineVisual();
        }

        // One app: a circular glossy visionOS medallion icon floating directly in space over its
        // shadowed white label, with optional reorder arrows while Reorder Mode is active.
        void Tile(int index, int apps, LibraryItem item)
        {
            var cell = M3.Group($"App {item.Title}", m_Grid, CurrentCellPosition(index, apps),
                new Vector2(k_CellWidth, k_CellHeight));
            m_Cells.Add(cell.gameObject);

            var hitTarget = M3.Surface("Hit Target", cell, Vector2.zero, cell.sizeDelta, Color.clear,
                M3Theme.k_ShapeLarge, raycastTarget: true, style: VisionSurfaceStyle.Pure);

            var selectionRing = M3.Surface("Selection Ring", cell, new Vector2(0f, 16f), new Vector2(88f, 88f),
                M3Theme.Primary, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.OutlineRing,
                rimStrength: 2.5f, sheenStrength: 0f);
            selectionRing.enabled = false;

            var state = M3.Surface("State Layer", cell, new Vector2(0f, 16f), new Vector2(92f, 92f), Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitTarget;
            var capturedItem = item;
            button.onClick.AddListener(() => PickItem(capturedItem));
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, Color.white);

            if (item.CustomIcon != null)
            {
                M3.Surface("Icon Backing", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize),
                    M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy,
                    rimStrength: 0.72f, sheenStrength: 0.85f);
                var rawImg = M3.RoundedScreen("App Icon", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize),
                    M3Theme.k_ShapeFull, raycastTarget: false, rimStrength: 0.82f, sheenStrength: 0.45f);
                rawImg.texture = item.CustomIcon;
            }
            else
            {
                var tile = M3.Surface("Icon Tile", cell, new Vector2(0f, 16f), new Vector2(k_IconSize, k_IconSize),
                    item.Tint, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Glossy, rimStrength: 0.95f, sheenStrength: 1.0f);
                M3.Icon("Icon", tile.rectTransform, item.Icon, 38f, Vector2.zero, Color.white);
            }

            var name = M3.TextWithShadow("Name", cell, item.Title, M3Theme.Type.LabelLarge, new Vector2(0f, -38f),
                new Vector2(k_CellWidth - 8f, 22f), Color.white);
            name.textWrappingMode = TextWrappingModes.NoWrap;
            var running = M3.Surface("Running", cell, new Vector2(0f, -54f), new Vector2(14f, 4f), M3Theme.Primary,
                M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Pure).gameObject;
            running.SetActive(false);

            var reorderControls = M3.Group("Reorder Controls", cell, Vector2.zero, cell.sizeDelta);
            var capturedIndex = index;
            var moveLeft = M3.IconButton("Move Left", reorderControls, "chevron_left", M3Button.Variant.Tonal,
                new Vector2(-k_CellWidth * 0.5f + 16f, k_CellHeight * 0.5f - 16f), () => MoveItemAt(capturedIndex, -1), 28f);
            var moveRight = M3.IconButton("Move Right", reorderControls, "chevron_right", M3Button.Variant.Tonal,
                new Vector2(k_CellWidth * 0.5f - 16f, k_CellHeight * 0.5f - 16f), () => MoveItemAt(capturedIndex, 1), 28f);
            reorderControls.gameObject.SetActive(m_ReorderMode);

            m_Tiles.Add(new TileRecord
            {
                Item = item,
                Running = running,
                ReorderControls = reorderControls.gameObject,
                MoveLeft = moveLeft,
                MoveRight = moveRight,
                SelectionRing = selectionRing,
                Index = index,
            });
        }

        int CurrentEnvPage => m_EnvMode == EnvPickerMode.MyEnvironments ? m_EnvMyPage : m_EnvBrowsePage;
        internal bool CanScrollEnvironments => m_EnvironmentsShown && m_EnvTotalPages > 1;

        internal void TurnEnvironments(int step)
        {
            if (m_EnvTotalPages <= 1)
                return;
            if (m_EnvMode == EnvPickerMode.MyEnvironments)
            {
                var next = Mathf.Clamp(m_EnvMyPage + step, 0, m_EnvTotalPages - 1);
                if (next == m_EnvMyPage)
                    return;
                m_EnvMyPage = next;
            }
            else if (m_EnvMode == EnvPickerMode.PolyHavenBrowse)
            {
                var next = Mathf.Clamp(m_EnvBrowsePage + step, 0, m_EnvTotalPages - 1);
                if (next == m_EnvBrowsePage)
                    return;
                m_EnvBrowsePage = next;
            }
            RebuildEnvironmentsGrid();
        }

        Vector2 CurrentEnvironmentPanelSize()
        {
            var (bubbleCount, paged) = GetCurrentEnvironmentPageBubbleCount();
            var gridSize = EnvironmentGridSize(Mathf.Max(1, bubbleCount));
            var arrowsWidth = paged ? 2f * (k_ArrowSize + k_Gap) : 0f;
            var width = Mathf.Max(k_EnvMinPanelWidth, gridSize.x + arrowsWidth);
            var height = gridSize.y + k_EnvHeaderHeight + k_EnvFooterHeight;
            return new Vector2(width, height);
        }

        (int count, bool paged) GetCurrentEnvironmentPageBubbleCount()
        {
            switch (m_EnvMode)
            {
                case EnvPickerMode.PolyHavenCategories:
                    return (PolyHavenClient.Categories.Count, false);
                case EnvPickerMode.PolyHavenBrowse:
                {
                    var items = GetBrowseResults();
                    var total = items.Count;
                    var pages = Mathf.Max(1, (total + k_EnvBrowsePerPage - 1) / k_EnvBrowsePerPage);
                    var page = Mathf.Clamp(m_EnvBrowsePage, 0, pages - 1);
                    var onPage = total == 0 ? 0 : Mathf.Min(k_EnvBrowsePerPage, total - page * k_EnvBrowsePerPage);
                    return (onPage, pages > 1);
                }
                default:
                {
                    var total = TotalEnvironmentBubbleCount;
                    if (total <= 15)
                        return (total, false);
                    var pages = Mathf.Max(1, (total + k_EnvBrowsePerPage - 1) / k_EnvBrowsePerPage);
                    var page = Mathf.Clamp(m_EnvMyPage, 0, pages - 1);
                    var onPage = Mathf.Min(k_EnvBrowsePerPage, total - page * k_EnvBrowsePerPage);
                    return (onPage, pages > 1);
                }
            }
        }

        List<PolyHavenAssetEntry> GetBrowseResults()
        {
            if (m_PolyHavenCatalog == null || m_PolyHavenCatalog.Count == 0)
                return new List<PolyHavenAssetEntry>();
            if (!string.IsNullOrWhiteSpace(m_EnvSearchQuery))
                return PolyHavenClient.SearchCatalog(m_PolyHavenCatalog, m_EnvSearchQuery, m_SelectedCategoryApiName);
            return PolyHavenClient.FilterByCategory(m_PolyHavenCatalog, m_SelectedCategoryApiName);
        }

        void BuildEnvironmentsGrid()
        {
            if (m_EnvironmentsBuilt)
                return;
            RebuildEnvironmentsGrid();
        }

        void RebuildEnvironmentsGrid()
        {
            if (m_Root == null)
                return;
            m_EnvironmentsBuilt = true;

            if (m_EnvironmentsGrid != null)
            {
                Destroy(m_EnvironmentsGrid.gameObject);
                m_EnvironmentsGrid = null;
            }
            m_EnvPrevious = null;
            m_EnvNext = null;
            m_EnvBubbles.Clear();
            m_EnvThumbImages.Clear();
            m_EnvProgressLabels.Clear();
            m_EnvProgressBadges.Clear();
            m_EnvDownloadedBadges.Clear();

            var (pageCountBubbles, _) = GetCurrentEnvironmentPageBubbleCount();
            var panelSize = CurrentEnvironmentPanelSize();
            m_Root.sizeDelta = panelSize;
            m_EnvironmentsGrid = M3.Group("Environments Grid", m_Root, Vector2.zero, panelSize);
            var gridCenterY = (k_EnvFooterHeight - k_EnvHeaderHeight) * 0.5f;

            BuildEnvironmentsHeader(panelSize);

            switch (m_EnvMode)
            {
                case EnvPickerMode.PolyHavenCategories:
                    m_EnvTotalPages = 1;
                    BuildCategoryBubbles(gridCenterY);
                    break;
                case EnvPickerMode.PolyHavenBrowse:
                    BuildPolyHavenBrowseBubbles(panelSize, gridCenterY);
                    break;
                default:
                    BuildMyEnvironmentBubbles(panelSize, gridCenterY, pageCountBubbles);
                    break;
            }

            BuildEnvironmentsFooter(panelSize);
            if (m_EnvironmentsShown)
            {
                UiRenderQueue.ApplyRecursive(m_Root, UiRenderQueue.k_PalmOverlayQueue);
                RefreshEnvironmentSelection();
                m_Settings?.RefreshPlacement();
            }
        }

        void BuildEnvironmentsHeader(Vector2 panelSize)
        {
            var headerY = panelSize.y * 0.5f - k_EnvHeaderHeight * 0.5f - 4f;
            var header = M3.Group("Env Header", m_EnvironmentsGrid, new Vector2(0f, headerY),
                new Vector2(panelSize.x - 24f, k_EnvHeaderHeight));

            if (m_EnvMode == EnvPickerMode.MyEnvironments)
            {
                var manageLabel = m_EnvManageMode ? "Fine" : "Gestisci";
                var manageIcon = m_EnvManageMode ? "check" : "edit";
                var manageWidth = Mathf.Max(152f, M3.ButtonWidth(manageLabel, manageIcon) + 8f);
                const float titleWidth = 196f;
                const float gap = 12f;
                var totalW = titleWidth + gap + manageWidth;
                var titleX = -totalW * 0.5f + titleWidth * 0.5f;
                var manageX = totalW * 0.5f - manageWidth * 0.5f;

                var titlePill = M3.Surface("Env Title Pill", header, new Vector2(titleX, 0f), new Vector2(titleWidth, 38f),
                    M3Theme.SurfaceContainer, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Frosted);
                var titleText = M3.Text("Env Title", titlePill.rectTransform, "Ambienti 360°", M3Theme.Type.TitleMedium,
                    Vector2.zero, new Vector2(titleWidth - 20f, 30f), M3Theme.OnSurface);
                titleText.textWrappingMode = TextWrappingModes.NoWrap;

                var manageBtn = M3.Button("Env Manage", header, manageLabel,
                    m_EnvManageMode ? M3Button.Variant.Filled : M3Button.Variant.Tonal,
                    new Vector2(manageX, 0f), ToggleEnvManageMode, manageIcon, width: manageWidth);
                if (manageBtn.Label != null)
                {
                    manageBtn.Label.textWrappingMode = TextWrappingModes.NoWrap;
                    manageBtn.Label.overflowMode = TextOverflowModes.Overflow;
                }
                manageBtn.Selected = m_EnvManageMode;
                return;
            }

            var backLabel = m_EnvMode == EnvPickerMode.PolyHavenBrowse ? "Categorie" : "Ambienti";
            var backWidth = Mathf.Max(148f, M3.ButtonWidth(backLabel, "chevron_left") + 8f);
            var leftX = -header.sizeDelta.x * 0.5f + backWidth * 0.5f + 4f;
            var backBtn = M3.Button("Env Back", header, backLabel, M3Button.Variant.Tonal, new Vector2(leftX, 0f),
                OnEnvBackClicked, "chevron_left", width: backWidth);
            if (backBtn.Label != null)
            {
                backBtn.Label.textWrappingMode = TextWrappingModes.NoWrap;
                backBtn.Label.overflowMode = TextOverflowModes.Overflow;
            }

            var searchWidth = Mathf.Clamp(header.sizeDelta.x - backWidth - 76f, 180f, 340f);
            var searchX = leftX + backWidth * 0.5f + 12f + searchWidth * 0.5f;
            var searchPill = M3.Surface("Search Pill", header, new Vector2(searchX, 0f),
                new Vector2(searchWidth, 42f), M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull,
                raycastTarget: true, style: VisionSurfaceStyle.Frosted);
            var searchBtn = searchPill.gameObject.AddComponent<Button>();
            searchBtn.transition = Selectable.Transition.None;
            searchBtn.targetGraphic = searchPill;
            searchBtn.onClick.AddListener(OpenSearchKeyboard);
            var state = M3.Surface("Search State", searchPill.rectTransform, Vector2.zero,
                new Vector2(searchWidth, 42f), Color.clear, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Pure);
            searchPill.gameObject.AddComponent<M3StateLayer>().Initialize(state, searchBtn, Color.white);

            M3.Icon("Search Icon", searchPill.rectTransform, "search", 20f,
                new Vector2(-searchWidth * 0.5f + 22f, 0f), M3Theme.OnSurfaceVariant);

            var hasQuery = !string.IsNullOrWhiteSpace(m_EnvSearchQuery);
            var placeholder = m_EnvMode == EnvPickerMode.PolyHavenBrowse && !string.IsNullOrEmpty(m_SelectedCategoryTitleIt)
                ? $"Cerca in {m_SelectedCategoryTitleIt}..."
                : "Cerca per nome...";
            var queryDisplay = hasQuery ? m_EnvSearchQuery : placeholder;
            var queryText = M3.Text("Search Text", searchPill.rectTransform, queryDisplay, M3Theme.Type.BodyMedium,
                new Vector2(8f, 0f), new Vector2(searchWidth - 62f, 34f),
                hasQuery ? M3Theme.OnSurface : M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            queryText.textWrappingMode = TextWrappingModes.NoWrap;

            if (hasQuery)
            {
                var clearX = searchX + searchWidth * 0.5f + 26f;
                M3.IconButton("Clear Search", header, "close", M3Button.Variant.Tonal,
                    new Vector2(clearX, 0f), ClearEnvSearch, 40f);
            }
        }

        void BuildEnvironmentsFooter(Vector2 panelSize)
        {
            var footerY = -panelSize.y * 0.5f + k_EnvFooterHeight * 0.5f + 4f;
            var parts = new List<string>(3) { PolyHavenClient.AttributionText };
            if (m_EnvTotalPages > 1)
                parts.Add($"Pagina {CurrentEnvPage + 1} / {m_EnvTotalPages}");
            if (!string.IsNullOrEmpty(m_EnvStatusMessage))
                parts.Add(m_EnvStatusMessage);
            else if (m_EnvTotalPages <= 1 && m_EnvMode == EnvPickerMode.PolyHavenBrowse &&
                     !string.IsNullOrEmpty(m_SelectedCategoryTitleIt))
                parts.Add(m_SelectedCategoryTitleIt);

            var footerText = string.Join("  •  ", parts);

            var pillWidth = Mathf.Clamp(panelSize.x - 40f, 260f, 520f);
            var pill = M3.Surface("PolyHaven Attribution Pill", m_EnvironmentsGrid, new Vector2(0f, footerY),
                new Vector2(pillWidth, 28f), M3Theme.SurfaceContainer, M3Theme.k_ShapeFull,
                style: VisionSurfaceStyle.Frosted);
            var txt = M3.Text("PolyHaven Attribution", pill.rectTransform, footerText, M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(pillWidth - 20f, 22f), M3Theme.OnSurfaceVariant);
            txt.textWrappingMode = TextWrappingModes.NoWrap;
        }

        void BuildMyEnvironmentBubbles(Vector2 panelSize, float gridCenterY, int expectedPageCount)
        {
            var total = TotalEnvironmentBubbleCount;
            if (total <= 15)
            {
                m_EnvTotalPages = 1;
                m_EnvMyPage = 0;
                BuildMyEnvironmentSlice(0, total, gridCenterY);
                return;
            }

            m_EnvTotalPages = Mathf.Max(1, (total + k_EnvBrowsePerPage - 1) / k_EnvBrowsePerPage);
            m_EnvMyPage = Mathf.Clamp(m_EnvMyPage, 0, m_EnvTotalPages - 1);
            var start = m_EnvMyPage * k_EnvBrowsePerPage;
            var countOnPage = Mathf.Min(k_EnvBrowsePerPage, total - start);
            BuildMyEnvironmentSlice(start, countOnPage, gridCenterY);
            BuildEnvPagingArrows(panelSize, gridCenterY, m_EnvMyPage, m_EnvTotalPages);
        }

        void BuildMyEnvironmentSlice(int startSlot, int countOnPage, float gridCenterY)
        {
            var savedCount = EnvironmentManager.EnvironmentCount;
            for (var slot = 0; slot < countOnPage; slot++)
            {
                var globalIndex = startSlot + slot;
                var pos = EnvironmentBubblePosition(slot, countOnPage) + new Vector2(0f, gridCenterY);
                if (globalIndex < savedCount)
                {
                    var envIndex = globalIndex;
                    var entry = EnvironmentLibraryStore.Get(envIndex);
                    BuildMyEnvBubble(pos, envIndex, entry);
                }
                else if (globalIndex == savedCount)
                {
                    BuildActionBubble(pos, envIndex: -1, title: "Stanza AR", icon: "view_in_ar",
                        tint: new Color(0.24f, 0.27f, 0.34f, 0.78f), style: VisionSurfaceStyle.Frosted,
                        onClick: () => PickEnvironmentBubble(-1));
                }
                else
                {
                    BuildActionBubble(pos, envIndex: -2, title: "Aggiungi", icon: "add",
                        tint: new Color(0.18f, 0.44f, 0.72f, 0.92f), style: VisionSurfaceStyle.Glossy,
                        onClick: OpenPolyHavenCategories);
                }
            }
        }

        void BuildMyEnvBubble(Vector2 pos, int envIndex, SavedEnvironment entry)
        {
            var cell = M3.Group($"Environment {entry.Title}", m_EnvironmentsGrid, pos,
                new Vector2(k_EnvCellWidth, k_EnvCellHeight));

            var hitTarget = M3.Surface("Hit Target", cell, Vector2.zero, cell.sizeDelta, Color.clear,
                M3Theme.k_ShapeLarge, raycastTarget: true, style: VisionSurfaceStyle.Pure);

            var activeRing = M3.Surface("Active Ring", cell, new Vector2(0f, 14f), new Vector2(92f, 92f),
                Color.white, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.OutlineRing,
                rimStrength: 3.0f, sheenStrength: 0f);
            activeRing.enabled = false;

            if (entry.IsProceduralCinema)
            {
                var tile = M3.Surface("Bubble Backing", cell, new Vector2(0f, 14f),
                    new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), new Color(0.08f, 0.11f, 0.20f, 0.96f),
                    M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy,
                    rimStrength: 0.95f, sheenStrength: 1.0f);
                M3.Icon("Icon", tile.rectTransform, "movie", 36f, Vector2.zero, Color.white);
            }
            else
            {
                var tile = M3.Surface("Bubble Backing", cell, new Vector2(0f, 14f),
                    new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), new Color(0.20f, 0.36f, 0.56f, 0.92f),
                    M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy,
                    rimStrength: 0.95f, sheenStrength: 1.0f);
                M3.Icon("Icon", tile.rectTransform, "landscape", 34f, Vector2.zero, Color.white);

                var rawImg = M3.RoundedScreen("Preview", cell, new Vector2(0f, 14f),
                    new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), M3Theme.k_ShapeFull,
                    raycastTarget: false, rimStrength: 0.92f, sheenStrength: 0.55f);
                rawImg.gameObject.SetActive(false);
                m_EnvThumbImages[entry.Id] = rawImg;
                if (Application.isPlaying)
                    PolyHavenService.Instance.RequestThumbnail(entry.Id, OnEnvThumbnailLoaded);

                if (m_EnvManageMode)
                {
                    var delBadge = M3.Surface("Remove Badge", cell, new Vector2(28f, 42f), new Vector2(26f, 26f),
                        M3Theme.Error, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy);
                    M3.Icon("Remove Icon", delBadge.rectTransform, "close", 16f, Vector2.zero, Color.white);
                }
                else
                {
                    BuildDownloadStatusOverlay(cell, entry.Id, showInstalledCheck: false);
                }
            }

            var state = M3.Surface("State Layer", cell, new Vector2(0f, 14f), new Vector2(90f, 90f), Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitTarget;
            var capturedEnvIndex = envIndex;
            var capturedId = entry.Id;
            var capturedProcedural = entry.IsProceduralCinema;
            button.onClick.AddListener(() =>
            {
                if (m_EnvManageMode && !capturedProcedural)
                    RemoveSavedEnvironment(capturedId);
                else
                    PickEnvironmentBubble(capturedEnvIndex);
            });
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, Color.white);

            BuildBubbleLabel(cell, entry.Title);

            var activeDot = M3.Surface("Active", cell, new Vector2(0f, -53f), new Vector2(14f, 4f), M3Theme.Primary,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure).gameObject;
            activeDot.SetActive(false);

            m_EnvBubbles.Add(new EnvironmentBubbleRecord
            {
                AssetId = entry.Id,
                EnvIndex = envIndex,
                ActiveRing = activeRing,
                ActiveDot = activeDot,
            });
        }

        void BuildActionBubble(Vector2 pos, int envIndex, string title, string icon, Color tint,
            VisionSurfaceStyle style, Action onClick)
        {
            var cell = M3.Group($"Environment {title}", m_EnvironmentsGrid, pos,
                new Vector2(k_EnvCellWidth, k_EnvCellHeight));
            var hitTarget = M3.Surface("Hit Target", cell, Vector2.zero, cell.sizeDelta, Color.clear,
                M3Theme.k_ShapeLarge, raycastTarget: true, style: VisionSurfaceStyle.Pure);
            var activeRing = M3.Surface("Active Ring", cell, new Vector2(0f, 14f), new Vector2(92f, 92f),
                Color.white, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.OutlineRing,
                rimStrength: 3.0f, sheenStrength: 0f);
            activeRing.enabled = false;

            var tile = M3.Surface("Bubble Backing", cell, new Vector2(0f, 14f),
                new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), tint, M3Theme.k_ShapeFull, raycastTarget: false,
                style: style, rimStrength: 0.92f, sheenStrength: 0.90f);
            M3.Icon("Icon", tile.rectTransform, icon, 34f, Vector2.zero, Color.white);

            var state = M3.Surface("State Layer", cell, new Vector2(0f, 14f), new Vector2(90f, 90f), Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitTarget;
            button.onClick.AddListener(() => onClick?.Invoke());
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, Color.white);

            BuildBubbleLabel(cell, title);

            var activeDot = M3.Surface("Active", cell, new Vector2(0f, -53f), new Vector2(14f, 4f), M3Theme.Primary,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure).gameObject;
            activeDot.SetActive(false);

            m_EnvBubbles.Add(new EnvironmentBubbleRecord
            {
                AssetId = null,
                EnvIndex = envIndex,
                ActiveRing = activeRing,
                ActiveDot = activeDot,
            });
        }

        void BuildCategoryBubbles(float gridCenterY)
        {
            var categories = PolyHavenClient.BuildCategorySummaries(m_PolyHavenCatalog);
            var total = categories.Count;
            for (var i = 0; i < total; i++)
            {
                var cat = categories[i];
                var pos = EnvironmentBubblePosition(i, total) + new Vector2(0f, gridCenterY);
                var cell = M3.Group($"Category {cat.Slug}", m_EnvironmentsGrid, pos,
                    new Vector2(k_EnvCellWidth, k_EnvCellHeight));

                var hitTarget = M3.Surface("Hit Target", cell, Vector2.zero, cell.sizeDelta, Color.clear,
                    M3Theme.k_ShapeLarge, raycastTarget: true, style: VisionSurfaceStyle.Pure);

                var tile = M3.Surface("Bubble Backing", cell, new Vector2(0f, 14f),
                    new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), cat.Tint, M3Theme.k_ShapeFull,
                    raycastTarget: false, style: VisionSurfaceStyle.Glossy, rimStrength: 0.95f, sheenStrength: 1.0f);
                M3.Icon("Icon", tile.rectTransform, cat.Icon, 34f, Vector2.zero, Color.white);

                if (!string.IsNullOrEmpty(cat.RepresentativeAssetId))
                {
                    var rawImg = M3.RoundedScreen("Preview", cell, new Vector2(0f, 14f),
                        new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), M3Theme.k_ShapeFull,
                        raycastTarget: false, rimStrength: 0.92f, sheenStrength: 0.55f);
                    rawImg.gameObject.SetActive(false);
                    m_EnvThumbImages[cat.RepresentativeAssetId] = rawImg;
                    if (Application.isPlaying)
                        PolyHavenService.Instance.RequestThumbnail(cat.RepresentativeAssetId, OnEnvThumbnailLoaded);
                }

                var countLabel = cat.Count > 0 ? cat.Count.ToString() : (m_CatalogFetching ? "..." : "•");
                var badge = M3.Surface("Count Badge", cell, new Vector2(26f, 42f), new Vector2(38f, 22f),
                    new Color(0.08f, 0.10f, 0.14f, 0.88f), M3Theme.k_ShapeFull, raycastTarget: false,
                    style: VisionSurfaceStyle.Frosted);
                var badgeTxt = M3.Text("Count", badge.rectTransform, countLabel, M3Theme.Type.LabelLarge,
                    Vector2.zero, new Vector2(34f, 20f), Color.white);
                badgeTxt.textWrappingMode = TextWrappingModes.NoWrap;

                var state = M3.Surface("State Layer", cell, new Vector2(0f, 14f), new Vector2(90f, 90f), Color.clear,
                    M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
                var button = cell.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.targetGraphic = hitTarget;
                var capturedCat = cat;
                button.onClick.AddListener(() => OpenPolyHavenCategory(capturedCat));
                cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, Color.white);

                BuildBubbleLabel(cell, cat.DisplayNameIt);
            }
        }

        void BuildPolyHavenBrowseBubbles(Vector2 panelSize, float gridCenterY)
        {
            var items = GetBrowseResults();
            if (items.Count == 0)
            {
                m_EnvTotalPages = 1;
                m_EnvBrowsePage = 0;
                var msg = m_CatalogFetching
                    ? "Caricamento catalogo Poly Haven..."
                    : (!string.IsNullOrEmpty(m_EnvSearchQuery)
                        ? $"Nessun risultato per \"{m_EnvSearchQuery}\""
                        : "Nessun ambiente disponibile in questa categoria");
                var emptyText = M3.Text("Browse Empty", m_EnvironmentsGrid, msg, M3Theme.Type.BodyLarge,
                    new Vector2(0f, gridCenterY), new Vector2(panelSize.x - 80f, 60f), M3Theme.OnSurface);
                emptyText.alignment = TextAlignmentOptions.Center;
                return;
            }

            m_EnvTotalPages = Mathf.Max(1, (items.Count + k_EnvBrowsePerPage - 1) / k_EnvBrowsePerPage);
            m_EnvBrowsePage = Mathf.Clamp(m_EnvBrowsePage, 0, m_EnvTotalPages - 1);
            var start = m_EnvBrowsePage * k_EnvBrowsePerPage;
            var countOnPage = Mathf.Min(k_EnvBrowsePerPage, items.Count - start);

            for (var slot = 0; slot < countOnPage; slot++)
            {
                var asset = items[start + slot];
                var pos = EnvironmentBubblePosition(slot, countOnPage) + new Vector2(0f, gridCenterY);
                BuildPolyHavenAssetBubble(pos, asset);
            }

            BuildEnvPagingArrows(panelSize, gridCenterY, m_EnvBrowsePage, m_EnvTotalPages);
        }

        void BuildPolyHavenAssetBubble(Vector2 pos, PolyHavenAssetEntry asset)
        {
            var cell = M3.Group($"PolyHaven {asset.Id}", m_EnvironmentsGrid, pos,
                new Vector2(k_EnvCellWidth, k_EnvCellHeight));

            var hitTarget = M3.Surface("Hit Target", cell, Vector2.zero, cell.sizeDelta, Color.clear,
                M3Theme.k_ShapeLarge, raycastTarget: true, style: VisionSurfaceStyle.Pure);

            var activeRing = M3.Surface("Active Ring", cell, new Vector2(0f, 14f), new Vector2(92f, 92f),
                Color.white, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.OutlineRing,
                rimStrength: 3.0f, sheenStrength: 0f);
            activeRing.enabled = false;

            var tile = M3.Surface("Bubble Backing", cell, new Vector2(0f, 14f),
                new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), new Color(0.18f, 0.30f, 0.46f, 0.92f),
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy,
                rimStrength: 0.95f, sheenStrength: 1.0f);
            M3.Icon("Icon", tile.rectTransform, "landscape", 34f, Vector2.zero, Color.white);

            var rawImg = M3.RoundedScreen("Preview", cell, new Vector2(0f, 14f),
                new Vector2(k_EnvBubbleSize, k_EnvBubbleSize), M3Theme.k_ShapeFull,
                raycastTarget: false, rimStrength: 0.92f, sheenStrength: 0.55f);
            rawImg.gameObject.SetActive(false);
            m_EnvThumbImages[asset.Id] = rawImg;
            if (Application.isPlaying)
                PolyHavenService.Instance.RequestThumbnail(asset.Id, OnEnvThumbnailLoaded);

            BuildDownloadStatusOverlay(cell, asset.Id, showInstalledCheck: true);

            var state = M3.Surface("State Layer", cell, new Vector2(0f, 14f), new Vector2(90f, 90f), Color.clear,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var button = cell.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = hitTarget;
            var capturedAsset = asset;
            button.onClick.AddListener(() => PickPolyHavenAssetBubble(capturedAsset));
            cell.gameObject.AddComponent<M3StateLayer>().Initialize(state, button, Color.white);

            BuildBubbleLabel(cell, asset.Name);

            var activeDot = M3.Surface("Active", cell, new Vector2(0f, -53f), new Vector2(14f, 4f), M3Theme.Primary,
                M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Pure).gameObject;
            activeDot.SetActive(false);

            m_EnvBubbles.Add(new EnvironmentBubbleRecord
            {
                AssetId = asset.Id,
                EnvIndex = EnvironmentLibraryStore.IndexOfId(asset.Id),
                ActiveRing = activeRing,
                ActiveDot = activeDot,
            });
        }

        void BuildDownloadStatusOverlay(RectTransform cell, string assetId, bool showInstalledCheck)
        {
            var progress = 0f;
            var isDownloading = Application.isPlaying &&
                                PolyHavenService.Instance.TryGetDownloadProgress(assetId, out progress);
            var isCached = EnvironmentLibraryStore.IsPanoramaCached(assetId);
            var isSaved = EnvironmentLibraryStore.Contains(assetId);

            if (isCached && isSaved && showInstalledCheck)
            {
                var checkBadge = M3.Surface("Installed Badge", cell, new Vector2(28f, 42f), new Vector2(24f, 24f),
                    M3Theme.Primary, M3Theme.k_ShapeFull, raycastTarget: false, style: VisionSurfaceStyle.Glossy);
                M3.Icon("Check", checkBadge.rectTransform, "check", 15f, Vector2.zero, Color.white);
                m_EnvDownloadedBadges[assetId] = checkBadge.gameObject;
            }
            else if (!isCached)
            {
                var dlBadge = M3.Surface("Download Badge", cell, new Vector2(28f, 42f), new Vector2(24f, 24f),
                    new Color(0.10f, 0.14f, 0.22f, 0.88f), M3Theme.k_ShapeFull, raycastTarget: false,
                    style: VisionSurfaceStyle.Frosted);
                M3.Icon("DL", dlBadge.rectTransform, "cloud_download", 14f, Vector2.zero, Color.white);
                dlBadge.gameObject.SetActive(!isDownloading);
                m_EnvDownloadedBadges[assetId] = dlBadge.gameObject;
            }

            var progBg = M3.Surface("Progress Pill", cell, new Vector2(0f, 14f), new Vector2(64f, 26f),
                new Color(0.06f, 0.10f, 0.18f, 0.90f), M3Theme.k_ShapeFull, raycastTarget: false,
                style: VisionSurfaceStyle.Frosted);
            var progTxt = M3.Text("Progress Text", progBg.rectTransform,
                isDownloading ? $"{Mathf.RoundToInt(progress * 100f)}%" : "",
                M3Theme.Type.LabelLarge, Vector2.zero, new Vector2(60f, 22f), Color.white);
            progTxt.textWrappingMode = TextWrappingModes.NoWrap;
            progBg.gameObject.SetActive(isDownloading);
            m_EnvProgressBadges[assetId] = progBg.gameObject;
            m_EnvProgressLabels[assetId] = progTxt;
        }

        static void BuildBubbleLabel(RectTransform cell, string title)
        {
            var label = M3.TextWithShadow("Name", cell, title, M3Theme.Type.LabelLarge, new Vector2(0f, -39f),
                new Vector2(k_EnvCellWidth + 6f, 24f), Color.white);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.enableAutoSizing = true;
            label.fontSizeMin = 10.5f;
            label.fontSizeMax = 13.5f;
        }

        void BuildEnvPagingArrows(Vector2 panelSize, float gridCenterY, int page, int totalPages)
        {
            if (totalPages <= 1)
                return;
            var x = panelSize.x * 0.5f - k_Padding - k_ArrowSize * 0.5f;
            m_EnvPrevious = M3.IconButton("Previous Environments", m_EnvironmentsGrid, "chevron_left",
                M3Button.Variant.Tonal, new Vector2(-x, gridCenterY), () => TurnEnvironments(-1));
            m_EnvNext = M3.IconButton("Next Environments", m_EnvironmentsGrid, "chevron_right",
                M3Button.Variant.Tonal, new Vector2(x, gridCenterY), () => TurnEnvironments(1));
            m_EnvPrevious.Interactable = page > 0;
            m_EnvNext.Interactable = page < totalPages - 1;
        }

        void OpenPolyHavenCategories()
        {
            m_EnvManageMode = false;
            m_EnvMode = EnvPickerMode.PolyHavenCategories;
            m_SelectedCategoryApiName = null;
            m_SelectedCategoryTitleIt = null;
            m_EnvSearchQuery = "";
            m_EnvStatusMessage = "";
            EnsurePolyHavenCatalogLoaded();
            RebuildEnvironmentsGrid();
        }

        void OpenPolyHavenCategory(PolyHavenCategoryInfo cat)
        {
            m_EnvMode = EnvPickerMode.PolyHavenBrowse;
            m_SelectedCategoryApiName = cat.ApiName;
            m_SelectedCategoryTitleIt = cat.DisplayNameIt;
            m_EnvBrowsePage = 0;
            m_EnvStatusMessage = "";
            EnsurePolyHavenCatalogLoaded();
            RebuildEnvironmentsGrid();
        }

        void OnEnvBackClicked()
        {
            m_SearchKeyboard?.Hide();
            if (m_EnvMode == EnvPickerMode.PolyHavenBrowse)
            {
                m_EnvMode = EnvPickerMode.PolyHavenCategories;
                m_SelectedCategoryApiName = null;
                m_SelectedCategoryTitleIt = null;
                m_EnvSearchQuery = "";
                m_EnvStatusMessage = "";
                RebuildEnvironmentsGrid();
                return;
            }
            m_EnvMode = EnvPickerMode.MyEnvironments;
            m_EnvSearchQuery = "";
            m_EnvStatusMessage = "";
            RebuildEnvironmentsGrid();
        }

        void ToggleEnvManageMode()
        {
            m_EnvManageMode = !m_EnvManageMode;
            RebuildEnvironmentsGrid();
        }

        void RemoveSavedEnvironment(string assetId)
        {
            if (string.IsNullOrEmpty(assetId) || assetId == EnvironmentLibraryStore.ProceduralCinemaId)
                return;
            var wasActive = EnvironmentManager.IsIsolated &&
                            string.Equals(EnvironmentManager.CurrentId, assetId, StringComparison.Ordinal);
            EnvironmentLibraryStore.Remove(assetId);
            if (wasActive)
                EnvironmentManager.SetIsolatedGlobal(false);
        }

        void EnsurePolyHavenCatalogLoaded()
        {
            if (!Application.isPlaying)
                return;
            var svc = PolyHavenService.Instance;
            if (svc.IsCatalogLoaded)
            {
                m_PolyHavenCatalog = svc.CachedCatalog;
                m_CatalogFetching = false;
                return;
            }
            if (m_CatalogFetching)
                return;
            m_CatalogFetching = true;
            m_EnvStatusMessage = "Connessione a Poly Haven...";
            svc.FetchCatalog((catalog, error) =>
            {
                m_CatalogFetching = false;
                m_PolyHavenCatalog = catalog;
                m_EnvStatusMessage = string.IsNullOrEmpty(error) ? "" : error;
                if (m_EnvironmentsShown && m_EnvMode != EnvPickerMode.MyEnvironments)
                    RebuildEnvironmentsGrid();
            });
        }

        void OpenSearchKeyboard()
        {
            EnsurePolyHavenCatalogLoaded();
            if (m_SearchKeyboard == null)
            {
                m_SearchKeyboard = EnvironmentSearchKeyboard.Create(
                    transform.parent,
                    ResolveHeadTransform,
                    OnEnvSearchQueryChanged);
            }
            m_SearchKeyboard.SetQuery(m_EnvSearchQuery);
            m_SearchKeyboard.Show();
        }

        Transform ResolveHeadTransform()
        {
            var fusion = FindAnyObjectByType<HeadPoseFusion>();
            if (fusion != null)
                return fusion.transform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        void OnEnvSearchQueryChanged(string query)
        {
            m_EnvSearchQuery = query ?? "";
            m_EnvBrowsePage = 0;
            if (!string.IsNullOrWhiteSpace(m_EnvSearchQuery) && m_EnvMode == EnvPickerMode.PolyHavenCategories)
                m_EnvMode = EnvPickerMode.PolyHavenBrowse;
            if (m_EnvironmentsShown)
                RebuildEnvironmentsGrid();
        }

        void ClearEnvSearch()
        {
            m_EnvSearchQuery = "";
            m_EnvBrowsePage = 0;
            m_SearchKeyboard?.SetQuery("");
            if (m_EnvironmentsShown)
                RebuildEnvironmentsGrid();
        }

        public static Rect ComputeSquareCoverUvRect(int texWidth, int texHeight)
        {
            if (texWidth <= 0 || texHeight <= 0)
                return new Rect(0f, 0f, 1f, 1f);
            if (texWidth > texHeight)
            {
                var uWidth = texHeight / (float)texWidth;
                return new Rect((1f - uWidth) * 0.5f, 0f, uWidth, 1f);
            }
            if (texHeight > texWidth)
            {
                var vHeight = texWidth / (float)texHeight;
                return new Rect(0f, (1f - vHeight) * 0.5f, 1f, vHeight);
            }
            return new Rect(0f, 0f, 1f, 1f);
        }

        void OnEnvThumbnailLoaded(string assetId, Texture2D tex)
        {
            if (tex == null || !m_EnvThumbImages.TryGetValue(assetId, out var rawImg) || rawImg == null)
                return;
            rawImg.texture = tex;
            rawImg.uvRect = ComputeSquareCoverUvRect(tex.width, tex.height);
            rawImg.gameObject.SetActive(true);
        }

        void OnEnvironmentLibraryChanged()
        {
            if (m_EnvironmentsShown && m_EnvMode == EnvPickerMode.MyEnvironments)
                RebuildEnvironmentsGrid();
        }

        void OnPolyHavenDownloadProgress(string assetId, float progress)
        {
            if (!m_EnvironmentsShown || string.IsNullOrEmpty(assetId))
                return;
            if (m_EnvDownloadedBadges.TryGetValue(assetId, out var dlBadge) && dlBadge != null)
                dlBadge.SetActive(false);
            if (m_EnvProgressBadges.TryGetValue(assetId, out var badge) && badge != null)
                badge.SetActive(true);
            if (m_EnvProgressLabels.TryGetValue(assetId, out var label) && label != null)
                label.text = $"{Mathf.RoundToInt(progress * 100f)}%";
        }

        void OnPolyHavenDownloadFinished(string assetId, bool success, string error)
        {
            if (!m_EnvironmentsShown)
                return;
            if (!success && !string.IsNullOrEmpty(error))
                m_EnvStatusMessage = error;
            else if (success)
                m_EnvStatusMessage = "";
            RebuildEnvironmentsGrid();
        }

        void PickPolyHavenAssetBubble(PolyHavenAssetEntry asset)
        {
            if (Time.unscaledTime - m_EnvShownAt < k_EnvPickGuardSeconds)
                return;
            if (!Application.isPlaying)
                return;

            if (PolyHavenService.Instance.IsDownloading(asset.Id))
                return;

            if (EnvironmentLibraryStore.IsPanoramaCached(asset.Id))
            {
                EnvironmentLibraryStore.AddOrUpdate(asset.Id, asset.Name, asset.TopCategory, asset.ThumbnailUrl);
                EnvironmentManager.SelectEnvironmentByIdGlobal(asset.Id);
                RefreshEnvironmentSelection();
                return;
            }

            m_EnvStatusMessage = $"Download {asset.Name}...";
            OnPolyHavenDownloadProgress(asset.Id, 0.02f);
            PolyHavenService.Instance.DownloadPanorama(asset.Id, null, (id, ok, err) =>
            {
                if (!ok)
                {
                    m_EnvStatusMessage = string.IsNullOrEmpty(err) ? "Download fallito" : err;
                    if (m_EnvironmentsShown)
                        RebuildEnvironmentsGrid();
                    return;
                }
                EnvironmentLibraryStore.AddOrUpdate(asset.Id, asset.Name, asset.TopCategory, asset.ThumbnailUrl);
                EnvironmentManager.SelectEnvironmentByIdGlobal(asset.Id);
                if (m_EnvironmentsShown)
                    RebuildEnvironmentsGrid();
            });
        }

        void PickEnvironmentBubble(int envIndex)
        {
            if (Time.unscaledTime - m_EnvShownAt < k_EnvPickGuardSeconds)
            {
                Debug.Log($"[Environment] Ignored bubble tap {Time.unscaledTime - m_EnvShownAt:F2} s after picker opened");
                return;
            }
            if (envIndex < 0)
                EnvironmentManager.SetIsolatedGlobal(false);
            else
                EnvironmentManager.SelectEnvironmentGlobal(envIndex);
            RefreshEnvironmentSelection();
        }

        void RefreshEnvironmentSelection()
        {
            var activeIndex = EnvironmentManager.IsIsolated ? EnvironmentManager.CurrentIndex : -1;
            var activeId = EnvironmentManager.IsIsolated ? EnvironmentManager.CurrentId : null;
            for (var i = 0; i < m_EnvBubbles.Count; i++)
            {
                var record = m_EnvBubbles[i];
                var selected = !string.IsNullOrEmpty(record.AssetId)
                    ? string.Equals(record.AssetId, activeId, StringComparison.Ordinal)
                    : record.EnvIndex == activeIndex;
                if (record.ActiveRing != null)
                    record.ActiveRing.enabled = selected;
                if (record.ActiveDot != null)
                    record.ActiveDot.SetActive(selected);
            }
        }
    }

    sealed class AppLibraryScrollTarget : MonoBehaviour, IVisualScrollTarget
    {
        AppLibrary m_Library;
        RectTransform m_Rect;
        float m_Accum;
        float m_LastTurnAt = float.NegativeInfinity;

        public RectTransform ScrollRectTransform => m_Rect;

        public void Initialize(AppLibrary library)
        {
            m_Library = library;
            m_Rect = GetComponent<RectTransform>();
        }

        public bool CanVisualScroll =>
            m_Library != null && m_Rect != null &&
            ((m_Library.IsShown && m_Library.PageCount > 1) || m_Library.CanScrollEnvironments);

        public void OnVisualTouchBegin(Vector2 localPoint) => m_Accum = 0f;

        public void OnVisualTouchMove(Vector2 localPoint, Vector2 localDelta)
        {
            if (!CanVisualScroll)
                return;
            var primary = Mathf.Abs(localDelta.x) >= Mathf.Abs(localDelta.y) ? localDelta.x : -localDelta.y;
            m_Accum += primary;
            if (Mathf.Abs(m_Accum) < 65f)
                return;
            if (Time.unscaledTime - m_LastTurnAt < 0.28f)
            {
                m_Accum = 0f;
                return;
            }
            m_LastTurnAt = Time.unscaledTime;
            var step = m_Accum < 0f ? 1 : -1;
            if (m_Library.CanScrollEnvironments)
                m_Library.TurnEnvironments(step);
            else
                m_Library.Turn(step);
            m_Accum = 0f;
        }

        public void OnVisualTouchEnd(Vector2 localPoint, bool cancelled) => m_Accum = 0f;
    }
}
