using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.Oggetti
{
    // Sketchfab 3D Catalog browser UI for the "Oggetti" app.
    // Supports all 18 Sketchfab categories, server-side search & sorting, cursor pagination,
    // end-user Sketchfab API Token login (with direct browser link to recover token & 1-click paste),
    // and spawning persistent AR-anchored objects via OggettiRuntimeHost. Logs [Oggetti].
    public class OggettiDemo : Demo
    {
        const float k_PickerDistance = 1.50f;
        const float k_TileWidth = 162f;
        const float k_TileHeight = 116f;
        const float k_TileStepX = 172f;
        const float k_TileStepY = 126f;

        enum CatalogView
        {
            BrowseModels,
            AllCategories,
            AccountAuth,
        }

        sealed class ModelTile
        {
            public GameObject Root;
            public RawImage Thumb;
            public TMP_Text TitleLabel;
            public TMP_Text MetaLabel;
            public GameObject CachedDot;
            public GameObject DownloadOverlay;
            public TMP_Text DownloadPercentLabel;
        }

        [SerializeField] Material m_ModelBaseMaterial;

        CoreServices m_Core;
        OggettiRuntimeHost m_Host;
        RectTransform m_PickerPanel;
        TMP_Text m_SubtitleText;
        M3Button m_ClearBtn;
        M3Button m_AccountBtn;
        M3Button m_SearchBtn;

        GameObject m_FilterBarRoot;
        readonly List<(string slug, M3Button button)> m_QuickCatButtons = new();
        M3Button m_AllCategoriesChip;
        M3Button m_SortBtn;

        GameObject m_GridRoot;
        GameObject m_FooterRoot;
        TMP_Text m_EmptyText;
        M3Button m_PrevBtn;
        TMP_Text m_PageText;
        M3Button m_NextBtn;

        GameObject m_CategoriesGridRoot;
        readonly List<(string slug, M3Button button)> m_AllCatButtons = new();

        GameObject m_AuthCardRoot;
        TMP_Text m_AuthStatusText;
        M3Button m_AuthDisconnectBtn;

        EnvironmentSearchKeyboard m_SearchKeyboard;
        EnvironmentSearchKeyboard m_TokenKeyboard;

        readonly ModelTile[] m_Tiles = new ModelTile[OggettiLayout.PerPage];
        readonly Stack<string> m_CursorHistory = new();

        CatalogView m_View = CatalogView.BrowseModels;
        string m_SelectedCategorySlug = "";
        string m_SearchQuery = "";
        int m_SortIndex;
        string m_CurrentCursor = "";
        int m_PageNumber = 1;
        SketchfabModelEntry? m_PendingAfterAuth;

        bool IsPickerVisible => m_PickerPanel != null && M3Fade.IsShown(m_PickerPanel.gameObject);

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            m_Host = OggettiRuntimeHost.GetOrCreate(core, m_ModelBaseMaterial);
            m_Host.AuthStateChanged += OnAuthStateChanged;
            m_Host.CatalogPageLoaded += OnCatalogPageLoaded;
            m_Host.ThumbnailUpdated += OnThumbnailUpdated;
            m_Host.DownloadProgressUpdated += OnDownloadProgressUpdated;
            m_Host.PlacedCountChanged += OnPlacedCountChanged;
            m_Host.StatusUpdated += OnStatusUpdated;

            BuildPicker();
            m_Host.VerifySavedTokenIfAny();
            FetchCurrentSearchPage(resetHistory: true);
            RefreshUi();
            Debug.Log("[Oggetti] App Oggetti (Sketchfab v3) aperta");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (core != null)
                m_Core = core;
            if (m_Host == null)
                m_Host = OggettiRuntimeHost.GetOrCreate(m_Core, m_ModelBaseMaterial);
            var head = m_Core != null ? m_Core.Head : null;
            WorldUi.BringToFront(m_PickerPanel, head, k_PickerDistance, 0f, 0f);
            RefreshUi();
        }

        public override void OnDemoExit(CoreServices core)
        {
            UnsubscribeHost();
            DestroyKeyboards();
            if (m_PickerPanel != null)
            {
                Destroy(m_PickerPanel.gameObject);
                m_PickerPanel = null;
            }
        }

        void OnDestroy()
        {
            UnsubscribeHost();
            DestroyKeyboards();
        }

        void DestroyKeyboards()
        {
            if (m_SearchKeyboard != null)
            {
                m_SearchKeyboard.Hide();
                Destroy(m_SearchKeyboard.gameObject);
                m_SearchKeyboard = null;
            }
            if (m_TokenKeyboard != null)
            {
                m_TokenKeyboard.Hide();
                Destroy(m_TokenKeyboard.gameObject);
                m_TokenKeyboard = null;
            }
        }

        void UnsubscribeHost()
        {
            if (m_Host == null)
                return;
            m_Host.AuthStateChanged -= OnAuthStateChanged;
            m_Host.CatalogPageLoaded -= OnCatalogPageLoaded;
            m_Host.ThumbnailUpdated -= OnThumbnailUpdated;
            m_Host.DownloadProgressUpdated -= OnDownloadProgressUpdated;
            m_Host.PlacedCountChanged -= OnPlacedCountChanged;
            m_Host.StatusUpdated -= OnStatusUpdated;
        }

        void BuildPicker()
        {
            if (m_PickerPanel != null)
                return;

            m_PickerPanel = WorldUi.Panel("Oggetti Catalog", transform, new Vector2(760f, 670f), k_PickerDistance);

            // Header row (y = +290)
            M3.Icon("ObjIcon", m_PickerPanel, "view_in_ar", 28f, new Vector2(-342f, 290f), M3Theme.GlassAccent);
            var title = M3.Text("Title", m_PickerPanel, "Oggetti", M3Theme.Type.TitleLarge,
                new Vector2(-268f, 290f), new Vector2(112f, 34f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            m_SubtitleText = M3.Text("Subtitle", m_PickerPanel, "", M3Theme.Type.BodyMedium,
                new Vector2(-126f, 290f), new Vector2(160f, 28f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_SubtitleText.textWrappingMode = TextWrappingModes.NoWrap;

            m_ClearBtn = M3.Button("ClearObjects", m_PickerPanel, "Pulisci", M3Button.Variant.Tonal,
                new Vector2(22f, 290f), () => m_Host?.ClearAllObjects(), "delete", width: 108f);
            m_ClearBtn.gameObject.SetActive(false);

            m_AccountBtn = M3.Button("Account", m_PickerPanel, "Accedi", M3Button.Variant.Tonal,
                new Vector2(154f, 290f), ToggleAuthView, "verified_user", width: 140f);
            if (m_AccountBtn.Label != null)
            {
                m_AccountBtn.Label.fontSize = 12f;
                m_AccountBtn.Label.textWrappingMode = TextWrappingModes.NoWrap;
            }

            m_SearchBtn = M3.IconButton("Search", m_PickerPanel, "search", M3Button.Variant.Tonal,
                new Vector2(254f, 290f), ToggleSearchKeyboard, 44f);
            M3.IconButton("Close", m_PickerPanel, "close", M3Button.Variant.Tonal,
                new Vector2(308f, 290f), OnPickerClosed, 44f);

            // Filter & Sort bar (y = +232)
            var filterGo = new GameObject("Filter Bar", typeof(RectTransform));
            filterGo.transform.SetParent(m_PickerPanel, false);
            var filterRect = (RectTransform)filterGo.transform;
            filterRect.anchoredPosition = new Vector2(0f, 232f);
            filterRect.sizeDelta = new Vector2(720f, 40f);
            m_FilterBarRoot = filterGo;

            var quickCats = new List<SketchfabCategory>();
            var allCats = SketchfabClient.Categories;
            for (var i = 0; i < allCats.Count; i++)
            {
                if (allCats[i].IsQuickFilter)
                    quickCats.Add(allCats[i]);
            }

            const float chipW = 70f;
            const float chipGap = 3f;
            const float chipStartX = -354f + chipW * 0.5f;
            m_QuickCatButtons.Clear();
            for (var i = 0; i < quickCats.Count; i++)
            {
                var cat = quickCats[i];
                var btn = M3.Key($"QuickCat_{cat.Slug}", filterRect, cat.DisplayNameIt, M3Button.Variant.Tonal,
                    new Vector2(chipStartX + i * (chipW + chipGap), 0f), new Vector2(chipW, 32f),
                    () => SelectCategory(cat.Slug));
                if (btn.Label != null)
                    btn.Label.fontSize = 11f;
                m_QuickCatButtons.Add((cat.Slug, btn));
            }

            m_AllCategoriesChip = M3.Key("AllCategories", filterRect, "Tutte (18)", M3Button.Variant.Tonal,
                new Vector2(chipStartX + quickCats.Count * (chipW + chipGap), 0f), new Vector2(chipW, 32f),
                ToggleCategoriesView);
            if (m_AllCategoriesChip.Label != null)
                m_AllCategoriesChip.Label.fontSize = 10.5f;

            m_SortBtn = M3.Key("SortCycle", filterRect, "Popolari", M3Button.Variant.Outlined,
                new Vector2(296f, 0f), new Vector2(106f, 32f), CycleSortOrder);
            if (m_SortBtn.Label != null)
                m_SortBtn.Label.fontSize = 11.5f;

            // View 1: 4 x 3 Models Grid (y = -18)
            var gridGo = new GameObject("Models Grid", typeof(RectTransform));
            gridGo.transform.SetParent(m_PickerPanel, false);
            var gridRect = (RectTransform)gridGo.transform;
            gridRect.anchoredPosition = new Vector2(0f, -18f);
            gridRect.sizeDelta = new Vector2(720f, 390f);
            m_GridRoot = gridGo;

            var startX = -(OggettiLayout.Columns - 1) * k_TileStepX * 0.5f;
            var startY = (OggettiLayout.Rows - 1) * k_TileStepY * 0.5f;
            for (var i = 0; i < OggettiLayout.PerPage; i++)
            {
                var col = i % OggettiLayout.Columns;
                var row = i / OggettiLayout.Columns;
                var pos = new Vector2(startX + col * k_TileStepX, startY - row * k_TileStepY);
                m_Tiles[i] = CreateTile(gridRect, i, pos);
            }

            m_EmptyText = M3.Text("EmptyMsg", m_PickerPanel, "Caricamento catalogo Sketchfab…", M3Theme.Type.BodyLarge,
                new Vector2(0f, -18f), new Vector2(560f, 64f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_EmptyText.gameObject.SetActive(false);

            var footerGo = new GameObject("Footer", typeof(RectTransform));
            footerGo.transform.SetParent(m_PickerPanel, false);
            var footerRect = (RectTransform)footerGo.transform;
            footerRect.anchoredPosition = new Vector2(0f, -264f);
            footerRect.sizeDelta = new Vector2(700f, 44f);
            m_FooterRoot = footerGo;

            m_PrevBtn = M3.IconButton("PrevPage", footerRect, "chevron_left", M3Button.Variant.Tonal,
                new Vector2(-116f, 0f), () => TurnPage(-1), 40f);
            m_PageText = M3.Text("PageText", footerRect, "Pagina 1", M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(150f, 30f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_NextBtn = M3.IconButton("NextPage", footerRect, "chevron_right", M3Button.Variant.Tonal,
                new Vector2(116f, 0f), () => TurnPage(1), 40f);

            // View 2: All 18 Sketchfab Categories Grid (3 x 6)
            BuildCategoriesView();

            // View 3: Sketchfab End-User API Token Login Card
            BuildAuthView();

            var attr = M3.Text("Attribution", m_PickerPanel, SketchfabClient.AttributionText, M3Theme.Type.BodyMedium,
                new Vector2(0f, -306f), new Vector2(400f, 24f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            attr.fontSize = 12f;

            m_PickerPanel.gameObject.AddComponent<OggettiScrollTarget>().Initialize(this);

            var head = m_Core != null ? m_Core.Head : null;
            if (head != null)
                WorldUi.PlaceInFront(m_PickerPanel, head, k_PickerDistance, 0f);
            M3Fade.Show(m_PickerPanel.gameObject);
            FloatingWindowHandle.Configure(m_PickerPanel, OnPickerClosed, restoreOnRecenter: true);
        }

        void BuildCategoriesView()
        {
            var catGo = new GameObject("All Categories Grid", typeof(RectTransform));
            catGo.transform.SetParent(m_PickerPanel, false);
            var catRect = (RectTransform)catGo.transform;
            catRect.anchoredPosition = new Vector2(0f, -36f);
            catRect.sizeDelta = new Vector2(720f, 460f);
            m_CategoriesGridRoot = catGo;

            var allCats = SketchfabClient.Categories;
            var list = new List<SketchfabCategory>();
            for (var i = 0; i < allCats.Count; i++)
            {
                if (!string.IsNullOrEmpty(allCats[i].Slug))
                    list.Add(allCats[i]);
            }

            const int cols = 3;
            const int rows = 6;
            const float stepX = 232f;
            const float stepY = 62f;
            var startX = -(cols - 1) * stepX * 0.5f;
            var startY = (rows - 1) * stepY * 0.5f + 22f;
            m_AllCatButtons.Clear();
            for (var i = 0; i < list.Count; i++)
            {
                var cat = list[i];
                var c = i % cols;
                var r = i / cols;
                var btn = M3.Key($"AllCat_{cat.Slug}", catRect, cat.DisplayNameIt, M3Button.Variant.Tonal,
                    new Vector2(startX + c * stepX, startY - r * stepY), new Vector2(216f, 48f),
                    () => SelectCategory(cat.Slug));
                if (btn.Label != null)
                    btn.Label.fontSize = 14f;
                m_AllCatButtons.Add((cat.Slug, btn));
            }

            M3.Button("BackToModels", catRect, "Torna ai modelli", M3Button.Variant.Outlined,
                new Vector2(0f, -200f), () => SwitchView(CatalogView.BrowseModels), "chevron_left", width: 220f);

            catGo.SetActive(false);
        }

        void BuildAuthView()
        {
            var authSurface = M3.Surface("Auth Card", m_PickerPanel, new Vector2(0f, -34f), new Vector2(680f, 444f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, raycastTarget: false, style: VisionSurfaceStyle.Recessed);
            m_AuthCardRoot = authSurface.gameObject;
            var parent = authSurface.rectTransform;

            M3.Icon("AuthIcon", parent, "verified_user", 40f, new Vector2(0f, 166f), M3Theme.GlassAccent);
            M3.Text("AuthTitle", parent, "Account Sketchfab", M3Theme.Type.TitleLarge,
                new Vector2(0f, 120f), new Vector2(460f, 34f), M3Theme.OnSurface, TextAlignmentOptions.Center);

            M3.Text("AuthDesc", parent,
                "Per scaricare i modelli 3D è necessario accedere con il tuo API Token personale Sketchfab (gratuito).\nApri il link qui sotto sul browser, copia l'API Token in Password & API e incollalo qui.",
                M3Theme.Type.BodyMedium, new Vector2(0f, 60f), new Vector2(600f, 66f),
                M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);

            m_AuthStatusText = M3.Text("AuthStatus", parent, "Stato: Non connesso", M3Theme.Type.LabelLarge,
                new Vector2(0f, 4f), new Vector2(580f, 32f), M3Theme.OnSurface, TextAlignmentOptions.Center);

            M3.Button("OpenSketchfabToken", parent, "Recupera Token (Browser)", M3Button.Variant.Filled,
                new Vector2(-128f, -54f), OpenTokenBrowserLink, "open_in_new", width: 240f);
            M3.Button("PasteToken", parent, "Incolla dagli appunti", M3Button.Variant.Filled,
                new Vector2(128f, -54f), PasteTokenFromClipboard, "check", width: 240f);

            M3.Button("TypeToken", parent, "Digita con tastiera", M3Button.Variant.Tonal,
                new Vector2(-128f, -114f), OpenTokenKeyboard, "keyboard", width: 240f);
            M3.Button("BackFromAuth", parent, "Torna al catalogo", M3Button.Variant.Tonal,
                new Vector2(128f, -114f), () => SwitchView(CatalogView.BrowseModels), "chevron_left", width: 240f);

            m_AuthDisconnectBtn = M3.Button("DisconnectAuth", parent, "Disconnetti account", M3Button.Variant.Outlined,
                new Vector2(0f, -174f), OnDisconnectClicked, "delete", width: 216f);

            m_AuthCardRoot.SetActive(false);
        }

        ModelTile CreateTile(RectTransform parent, int slot, Vector2 pos)
        {
            var go = new GameObject($"Model Tile {slot}", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(k_TileWidth, k_TileHeight);

            var backing = M3.Surface("Backing", rect, Vector2.zero, new Vector2(k_TileWidth, k_TileHeight),
                M3Theme.GlassRecessed, M3Theme.k_ShapeCard, raycastTarget: true, style: VisionSurfaceStyle.Recessed);

            var thumb = M3.RoundedScreen("Thumb", rect, new Vector2(0f, 15f), new Vector2(k_TileWidth - 10f, k_TileHeight - 38f),
                M3Theme.k_ShapeSmall, raycastTarget: false, rimStrength: 0.45f, sheenStrength: 0.14f);
            thumb.color = new Color(1f, 1f, 1f, 0.98f);
            thumb.gameObject.SetActive(false);

            var stateImg = M3.Surface("State", rect, Vector2.zero, new Vector2(k_TileWidth, k_TileHeight),
                Color.clear, M3Theme.k_ShapeCard, raycastTarget: false, style: VisionSurfaceStyle.Pure);

            var titleLbl = M3.Text("Name", rect, "", M3Theme.Type.LabelLarge,
                new Vector2(0f, -k_TileHeight * 0.5f + 22f), new Vector2(k_TileWidth - 12f, 20f),
                M3Theme.OnSurface, TextAlignmentOptions.Center);
            titleLbl.fontSize = 11.5f;
            titleLbl.textWrappingMode = TextWrappingModes.NoWrap;

            var metaLbl = M3.Text("Meta", rect, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -k_TileHeight * 0.5f + 8f), new Vector2(k_TileWidth - 12f, 16f),
                M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            metaLbl.fontSize = 9.5f;
            metaLbl.textWrappingMode = TextWrappingModes.NoWrap;

            var badge = M3.Surface("CachedDot", rect, new Vector2(k_TileWidth * 0.5f - 14f, k_TileHeight * 0.5f - 14f),
                new Vector2(10f, 10f), new Color(0.32f, 0.88f, 0.58f, 0.98f), M3Theme.k_ShapeFull,
                raycastTarget: false, style: VisionSurfaceStyle.Pure);
            badge.gameObject.SetActive(false);

            var dlOverlay = M3.Surface("DownloadOverlay", rect, new Vector2(0f, 15f),
                new Vector2(k_TileWidth - 10f, k_TileHeight - 38f),
                new Color(0.04f, 0.06f, 0.10f, 0.78f), M3Theme.k_ShapeSmall,
                raycastTarget: false, style: VisionSurfaceStyle.Pure);
            var dlPercentLbl = M3.Text("DownloadPercent", dlOverlay.rectTransform, "0%", M3Theme.Type.TitleMedium,
                Vector2.zero, new Vector2(k_TileWidth - 18f, 36f),
                Color.white, TextAlignmentOptions.Center);
            dlPercentLbl.fontSize = 20f;
            dlPercentLbl.fontStyle = FontStyles.Bold;
            dlPercentLbl.textWrappingMode = TextWrappingModes.NoWrap;
            dlOverlay.gameObject.SetActive(false);

            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = backing;
            var stateLayer = go.AddComponent<M3StateLayer>();
            stateLayer.Initialize(stateImg, btn, M3Theme.OnSurface);

            var captured = slot;
            btn.onClick.AddListener(() => OnTileClicked(captured));

            go.SetActive(false);
            return new ModelTile
            {
                Root = go,
                Thumb = thumb,
                TitleLabel = titleLbl,
                MetaLabel = metaLbl,
                CachedDot = badge.gameObject,
                DownloadOverlay = dlOverlay.gameObject,
                DownloadPercentLabel = dlPercentLbl,
            };
        }

        void FetchCurrentSearchPage(bool resetHistory)
        {
            if (m_Host == null)
                return;
            if (resetHistory)
            {
                m_CursorHistory.Clear();
                m_CurrentCursor = "";
                m_PageNumber = 1;
            }
            var sortBy = SketchfabClient.SortOptions[m_SortIndex].SortKey;
            m_Host.RequestSearchPage(m_SelectedCategorySlug, m_SearchQuery, sortBy, m_CurrentCursor);
        }

        void SelectCategory(string slug)
        {
            m_SelectedCategorySlug = slug ?? "";
            m_View = CatalogView.BrowseModels;
            FetchCurrentSearchPage(resetHistory: true);
            RefreshUi();
        }

        void CycleSortOrder()
        {
            m_SortIndex = (m_SortIndex + 1) % SketchfabClient.SortOptions.Length;
            FetchCurrentSearchPage(resetHistory: true);
            RefreshUi();
        }

        void SwitchView(CatalogView view)
        {
            m_View = view;
            if (view != CatalogView.BrowseModels)
            {
                m_SearchKeyboard?.Hide();
                if (view != CatalogView.AccountAuth)
                    m_TokenKeyboard?.Hide();
            }
            RefreshUi();
        }

        void ToggleCategoriesView() =>
            SwitchView(m_View == CatalogView.AllCategories ? CatalogView.BrowseModels : CatalogView.AllCategories);

        void ToggleAuthView() =>
            SwitchView(m_View == CatalogView.AccountAuth ? CatalogView.BrowseModels : CatalogView.AccountAuth);

        void OpenTokenBrowserLink()
        {
            if (PhoneAppShortcutStore.VrBrowserRouter.OpenUrl(m_Core != null ? m_Core.Demos : null, SketchfabClient.TokenSettingsUrl))
            {
                if (m_AuthStatusText != null)
                {
                    m_AuthStatusText.text = "Pagina Sketchfab aperta nel Browser VR: accedi, copia l'API Token e tocca 'Incolla dagli appunti'.";
                    m_AuthStatusText.color = M3Theme.GlassAccent;
                }
            }
            else if (m_AuthStatusText != null)
            {
                m_AuthStatusText.text = "Apri Web Cinema e visita sketchfab.com/settings/password per copiare il token.";
                m_AuthStatusText.color = M3Theme.GlassAccent;
            }
        }

        void PasteTokenFromClipboard()
        {
            var clip = GUIUtility.systemCopyBuffer;
            SubmitToken(clip);
        }

        void OpenTokenKeyboard()
        {
            m_SearchKeyboard?.Hide();
            if (m_TokenKeyboard == null)
            {
                m_TokenKeyboard = EnvironmentSearchKeyboard.Create(transform,
                    () => m_Core != null ? m_Core.Head : null,
                    onQueryChanged: null,
                    placeholder: "Digita o incolla il tuo Sketchfab API Token...",
                    onSubmit: SubmitToken,
                    submitLabel: "Conferma");
            }
            m_TokenKeyboard.SetQuery(m_Host != null ? m_Host.ApiToken : "");
            m_TokenKeyboard.Show();
        }

        void SubmitToken(string rawToken)
        {
            if (m_Host == null)
                return;
            m_Host.SetTokenAndVerify(rawToken, (ok, info) =>
            {
                if (!ok)
                {
                    RefreshUi();
                    return;
                }
                m_TokenKeyboard?.Hide();
                SwitchView(CatalogView.BrowseModels);
                if (m_PendingAfterAuth.HasValue)
                {
                    var pending = m_PendingAfterAuth.Value;
                    m_PendingAfterAuth = null;
                    StartModelDownload(pending);
                }
            });
            RefreshUi();
        }

        void OnDisconnectClicked()
        {
            m_PendingAfterAuth = null;
            m_Host?.ClearToken();
            RefreshUi();
        }

        void ToggleSearchKeyboard()
        {
            m_TokenKeyboard?.Hide();
            if (m_SearchKeyboard == null)
            {
                m_SearchKeyboard = EnvironmentSearchKeyboard.Create(transform,
                    () => m_Core != null ? m_Core.Head : null,
                    OnSearchQueryChanged,
                    placeholder: "Cerca modelli 3D su Sketchfab...");
                m_SearchKeyboard.SetQuery(m_SearchQuery);
                m_SearchKeyboard.Show();
                return;
            }
            m_SearchKeyboard.Toggle();
        }

        void OnSearchQueryChanged(string query)
        {
            m_SearchQuery = query ?? "";
            m_View = CatalogView.BrowseModels;
            FetchCurrentSearchPage(resetHistory: true);
            RefreshUi();
        }

        void TurnPage(int delta)
        {
            if (m_Host == null || m_Host.IsSearching)
                return;
            if (delta > 0)
            {
                if (string.IsNullOrEmpty(m_Host.NextCursor))
                    return;
                m_CursorHistory.Push(m_CurrentCursor);
                m_CurrentCursor = m_Host.NextCursor;
                m_PageNumber++;
                FetchCurrentSearchPage(resetHistory: false);
            }
            else if (delta < 0)
            {
                if (m_CursorHistory.Count == 0)
                    return;
                m_CurrentCursor = m_CursorHistory.Pop();
                m_PageNumber = Mathf.Max(1, m_PageNumber - 1);
                FetchCurrentSearchPage(resetHistory: false);
            }
        }

        void OnTileClicked(int slot)
        {
            if (m_Host == null || !string.IsNullOrEmpty(m_Host.DownloadingAssetId))
                return;
            var results = m_Host.CurrentResults;
            if (slot < 0 || slot >= results.Count)
                return;
            StartModelDownload(results[slot]);
        }

        void StartModelDownload(SketchfabModelEntry entry)
        {
            m_Host.DownloadAndPlace(entry, (ok, needsAuth) =>
            {
                if (needsAuth)
                {
                    m_PendingAfterAuth = entry;
                    SwitchView(CatalogView.AccountAuth);
                    return;
                }
                if (!ok)
                {
                    RefreshUi();
                    return;
                }
                m_PendingAfterAuth = null;
                m_SearchKeyboard?.Hide();
                m_TokenKeyboard?.Hide();
                if (m_PickerPanel != null && M3Fade.IsShown(m_PickerPanel.gameObject))
                    M3Fade.Hide(m_PickerPanel.gameObject);
            });
            UpdateSubtitle();
        }

        void OnPickerClosed()
        {
            m_SearchKeyboard?.Hide();
            m_TokenKeyboard?.Hide();
            if (m_PickerPanel != null)
                M3Fade.Hide(m_PickerPanel.gameObject);
            if (m_Host == null || m_Host.PlacedCount == 0)
                m_Core?.Demos?.CloseApp("oggetti");
        }

        void OnAuthStateChanged() => RefreshUi();
        void OnCatalogPageLoaded() => RefreshUi();
        void OnPlacedCountChanged() => RefreshUi();
        void OnStatusUpdated(string _) => UpdateSubtitle();

        void OnThumbnailUpdated(string _)
        {
            if (m_PickerPanel != null && IsPickerVisible && m_View == CatalogView.BrowseModels)
                RefreshTilesOnly();
        }

        void RefreshUi()
        {
            if (m_PickerPanel == null || m_Host == null)
                return;

            // Header buttons
            if (m_ClearBtn != null)
                m_ClearBtn.gameObject.SetActive(m_Host.PlacedCount > 0);
            if (m_AccountBtn != null)
            {
                var label = m_Host.HasToken
                    ? (!string.IsNullOrEmpty(m_Host.ConnectedUsername) ? $"@{m_Host.ConnectedUsername}" : "Connesso")
                    : "Accedi";
                m_AccountBtn.Text = label;
                m_AccountBtn.Selected = m_Host.HasToken || m_View == CatalogView.AccountAuth;
            }
            if (m_SearchBtn != null)
                m_SearchBtn.Selected = !string.IsNullOrWhiteSpace(m_SearchQuery);

            // Category chips & sort pill
            if (m_FilterBarRoot != null)
                m_FilterBarRoot.SetActive(m_View != CatalogView.AccountAuth);
            var isQuickSelected = false;
            for (var i = 0; i < m_QuickCatButtons.Count; i++)
            {
                var (slug, btn) = m_QuickCatButtons[i];
                var sel = slug == m_SelectedCategorySlug;
                btn.Selected = sel;
                if (sel)
                    isQuickSelected = true;
            }
            if (m_AllCategoriesChip != null)
                m_AllCategoriesChip.Selected = m_View == CatalogView.AllCategories || !isQuickSelected;
            if (m_SortBtn != null)
            {
                var hasQuery = !string.IsNullOrWhiteSpace(m_SearchQuery);
                m_SortBtn.Text = hasQuery ? "Pertinenza" : SketchfabClient.SortOptions[m_SortIndex].LabelIt;
                m_SortBtn.Interactable = !hasQuery;
            }

            // Switch visible view
            var showCategories = m_View == CatalogView.AllCategories;
            var showAuth = m_View == CatalogView.AccountAuth;
            if (m_CategoriesGridRoot != null)
                m_CategoriesGridRoot.SetActive(showCategories);
            if (m_AuthCardRoot != null)
                m_AuthCardRoot.SetActive(showAuth);

            if (showCategories)
            {
                for (var i = 0; i < m_AllCatButtons.Count; i++)
                    m_AllCatButtons[i].button.Selected = m_AllCatButtons[i].slug == m_SelectedCategorySlug;
            }

            if (showAuth)
                RefreshAuthCard();

            var showBrowse = m_View == CatalogView.BrowseModels;
            var results = m_Host.CurrentResults;
            var hasItems = results.Count > 0;

            if (m_GridRoot != null)
                m_GridRoot.SetActive(showBrowse && hasItems);
            if (m_FooterRoot != null)
                m_FooterRoot.SetActive(showBrowse && hasItems);

            if (m_EmptyText != null)
            {
                var showEmpty = showBrowse && !hasItems;
                m_EmptyText.gameObject.SetActive(showEmpty);
                if (showEmpty)
                {
                    m_EmptyText.text = m_Host.IsSearching
                        ? "Ricerca modelli 3D su Sketchfab…"
                        : (!string.IsNullOrWhiteSpace(m_SearchQuery)
                            ? $"Nessun modello Sketchfab trovato per \"{m_SearchQuery}\""
                            : "Nessun modello disponibile.");
                }
            }

            UpdateSubtitle();
            if (!showBrowse || !hasItems)
                return;

            if (m_PageText != null)
                m_PageText.text = $"Pagina {m_PageNumber}";
            if (m_PrevBtn != null)
                m_PrevBtn.Interactable = m_CursorHistory.Count > 0 && !m_Host.IsSearching;
            if (m_NextBtn != null)
                m_NextBtn.Interactable = !string.IsNullOrEmpty(m_Host.NextCursor) && !m_Host.IsSearching;

            RefreshTilesOnly();
        }

        void RefreshAuthCard()
        {
            if (m_Host == null)
                return;
            if (m_AuthDisconnectBtn != null)
                m_AuthDisconnectBtn.gameObject.SetActive(m_Host.HasToken);
            if (m_AuthStatusText == null)
                return;

            if (m_Host.IsVerifyingToken)
            {
                m_AuthStatusText.text = "Verifica token Sketchfab in corso…";
                m_AuthStatusText.color = M3Theme.GlassAccent;
            }
            else if (!string.IsNullOrEmpty(m_Host.AuthError))
            {
                m_AuthStatusText.text = m_Host.AuthError;
                m_AuthStatusText.color = M3Theme.Error;
            }
            else if (m_Host.HasToken)
            {
                var who = string.IsNullOrEmpty(m_Host.ConnectedUsername) ? "utente" : $"@{m_Host.ConnectedUsername}";
                m_AuthStatusText.text = $"Connesso come {who} • Pronto per il download";
                m_AuthStatusText.color = new Color(0.36f, 0.90f, 0.60f, 1f);
            }
            else
            {
                m_AuthStatusText.text = "Stato: Non connesso";
                m_AuthStatusText.color = M3Theme.OnSurface;
            }
        }

        void RefreshTilesOnly()
        {
            if (m_Host == null)
                return;
            var results = m_Host.CurrentResults;
            var thumbAspect = (k_TileWidth - 10f) / (k_TileHeight - 38f);
            for (var i = 0; i < m_Tiles.Length; i++)
            {
                var tile = m_Tiles[i];
                var valid = i < results.Count;
                tile.Root.SetActive(valid);
                if (!valid)
                    continue;

                var entry = results[i];
                tile.TitleLabel.text = entry.Name;
                var sizeStr = SketchfabClient.FormatBytes(entry.DownloadSizeBytes);
                tile.MetaLabel.text = string.IsNullOrEmpty(entry.AuthorName)
                    ? sizeStr
                    : (string.IsNullOrEmpty(sizeStr) ? entry.AuthorName : $"{entry.AuthorName} • {sizeStr}");
                tile.CachedDot.SetActive(m_Host.IsModelCached(entry.Uid));

                var tex = m_Host.GetOrRequestThumbnail(entry);
                if (tex != null)
                {
                    tile.Thumb.texture = tex;
                    tile.Thumb.uvRect = QuadriCropUv(tex.width, tex.height, thumbAspect);
                    tile.Thumb.gameObject.SetActive(true);
                }
                else
                {
                    tile.Thumb.texture = null;
                    tile.Thumb.gameObject.SetActive(false);
                }
            }
            UpdateDownloadTileOverlays();
        }

        void OnDownloadProgressUpdated()
        {
            UpdateSubtitle();
            UpdateDownloadTileOverlays();
        }

        void UpdateDownloadTileOverlays()
        {
            if (m_Host == null)
                return;
            var results = m_Host.CurrentResults;
            var downloadingId = m_Host.DownloadingAssetId;
            var pct = Mathf.RoundToInt(Mathf.Clamp01(m_Host.DownloadProgress) * 100f);
            for (var i = 0; i < m_Tiles.Length; i++)
            {
                var tile = m_Tiles[i];
                if (tile == null || tile.DownloadOverlay == null)
                    continue;
                var isThis = i < results.Count && !string.IsNullOrEmpty(downloadingId) && results[i].Uid == downloadingId;
                if (tile.DownloadOverlay.activeSelf != isThis)
                    tile.DownloadOverlay.SetActive(isThis);
                if (isThis && tile.DownloadPercentLabel != null)
                    tile.DownloadPercentLabel.text = $"{pct}%";
                if (i < results.Count)
                {
                    if (isThis && tile.MetaLabel != null)
                    {
                        tile.MetaLabel.text = $"Download… {pct}%";
                    }
                    else if (!isThis && tile.MetaLabel != null)
                    {
                        var entry = results[i];
                        var sizeStr = SketchfabClient.FormatBytes(entry.DownloadSizeBytes);
                        tile.MetaLabel.text = string.IsNullOrEmpty(entry.AuthorName)
                            ? sizeStr
                            : (string.IsNullOrEmpty(sizeStr) ? entry.AuthorName : $"{entry.AuthorName} • {sizeStr}");
                        if (tile.CachedDot != null)
                            tile.CachedDot.SetActive(m_Host.IsModelCached(entry.Uid));
                    }
                }
            }
        }

        static Rect QuadriCropUv(int texW, int texH, float targetAspect)
        {
            if (texW <= 0 || texH <= 0 || targetAspect <= 0f)
                return new Rect(0f, 0f, 1f, 1f);
            var texAspect = (float)texW / texH;
            if (texAspect > targetAspect)
            {
                var w = targetAspect / texAspect;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            var h = texAspect / targetAspect;
            return new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }

        void UpdateSubtitle()
        {
            if (m_SubtitleText == null || m_Host == null)
                return;
            if (!string.IsNullOrEmpty(m_Host.DownloadingAssetId))
            {
                var pct = Mathf.RoundToInt(Mathf.Clamp01(m_Host.DownloadProgress) * 100f);
                m_SubtitleText.text = $"Download… {pct}%";
                return;
            }
            if (!string.IsNullOrEmpty(m_Host.StatusMessage))
            {
                m_SubtitleText.text = m_Host.StatusMessage;
                return;
            }
            if (m_Host.PlacedCount > 0)
            {
                m_SubtitleText.text = m_Host.PlacedCount == 1
                    ? "1 oggetto fissato"
                    : $"{m_Host.PlacedCount} oggetti fissati";
                return;
            }
            m_SubtitleText.text = $"{m_Host.CurrentResults.Count} modelli Sketchfab";
        }

        sealed class OggettiScrollTarget : MonoBehaviour, IWatchScrollable
        {
            OggettiDemo m_Demo;
            float m_Accum;
            float m_LastTurnAt = float.NegativeInfinity;

            public void Initialize(OggettiDemo demo) => m_Demo = demo;

            public bool TryHandleWatchScroll(float rotarySteps)
            {
                if (m_Demo == null || m_Demo.m_View != CatalogView.BrowseModels || Mathf.Abs(rotarySteps) < 1e-4f)
                    return false;
                m_Accum += rotarySteps;
                if (Mathf.Abs(m_Accum) < 0.08f)
                    return true;
                var dir = m_Accum > 0f ? 1 : -1;
                m_Accum = 0f;
                if (Time.unscaledTime - m_LastTurnAt < 0.28f)
                    return true;
                m_LastTurnAt = Time.unscaledTime;
                m_Demo.TurnPage(dir);
                return true;
            }
        }
    }
}
