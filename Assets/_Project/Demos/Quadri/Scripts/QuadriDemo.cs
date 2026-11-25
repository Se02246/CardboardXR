using System;
using System.Collections.Generic;
using System.IO;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.Quadri
{
    // Quadri: lets the user pick photos from the Android phone gallery and place clean, borderless
    // rounded spatial windows anywhere in the room (with full move, resize, pin/float, and close controls).
    // Logs [Quadri].
    public class QuadriDemo : Demo
    {
        const float k_PickerDistance = 1.50f;
        const float k_FrameDistance = 1.48f;
        const float k_PollIntervalSeconds = 0.18f;
        const int k_FullImageMaxDim = 1600;
        const float k_TileWidth = 148f;
        const float k_TileHeight = 132f;
        const float k_TileStepX = 164f;
        const float k_TileStepY = 146f;

        struct PhotoItem
        {
            public long Id;
            public string Name;
            public int Width;
            public int Height;
        }

        sealed class GalleryTile
        {
            public GameObject Root;
            public RawImage Thumbnail;
            public TMP_Text LoadingLabel;
        }

        sealed class PlacedPhotoWindow
        {
            public long Id;
            public RectTransform Panel;
            public Texture2D Texture;
        }

        CoreServices m_Core;
        RectTransform m_PickerPanel;
        TMP_Text m_SubtitleText;
        M3Button m_ClearBtn;
        GameObject m_GridRoot;
        GameObject m_PermissionCard;
        TMP_Text m_EmptyText;
        M3Button m_PrevBtn;
        TMP_Text m_PageText;
        M3Button m_NextBtn;
        GameObject m_FooterRoot;

        readonly GalleryTile[] m_Tiles = new GalleryTile[QuadriLayout.PerPage];
        readonly List<PhotoItem> m_Photos = new();
        readonly Dictionary<long, Texture2D> m_ThumbTextures = new();
        readonly List<PlacedPhotoWindow> m_PlacedWindows = new();

        int m_Page;
        int m_KnownListVersion = -1;
        int m_KnownThumbVersion = -1;
        int m_KnownFullVersion = -1;
        bool m_HasPermission;
        bool m_WasRefreshing;
        long m_PendingFullId = -1;
        PhotoItem m_PendingFullItem;
        float m_NextPollAt;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Bridge;
#endif

        bool IsPickerVisible => m_PickerPanel != null && M3Fade.IsShown(m_PickerPanel.gameObject);

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            BuildPicker();
            InitBridge();
            RefreshPermissionAndGallery(forceRefresh: true);
            Debug.Log("[Quadri] App Quadri aperta");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (core != null)
                m_Core = core;
            var head = m_Core != null ? m_Core.Head : null;
            WorldUi.BringToFront(m_PickerPanel, head, k_PickerDistance, 0f, 0f);
            RefreshPermissionAndGallery(forceRefresh: false);
            UpdatePickerUiState();
        }

        public override void OnDemoExit(CoreServices core)
        {
            CleanupAllResources();
        }

        void OnDestroy()
        {
            CleanupAllResources();
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || m_PickerPanel == null)
                return;
            var grantedBefore = m_HasPermission;
            var grantedNow = CheckPermissionNative();
            if (grantedNow && !grantedBefore)
            {
                m_HasPermission = true;
                RequestRefreshNative();
                UpdatePickerUiState();
            }
        }

        void Update()
        {
            if (m_PickerPanel == null || Time.unscaledTime < m_NextPollAt)
                return;
            m_NextPollAt = Time.unscaledTime + k_PollIntervalSeconds;
            PollNativeUpdates();
        }

        void BuildPicker()
        {
            if (m_PickerPanel != null)
                return;

            m_PickerPanel = WorldUi.Panel("Quadri Gallery", transform, new Vector2(720f, 620f), k_PickerDistance);

            // Header
            M3.Icon("GalleryIcon", m_PickerPanel, "photo_library", 28f, new Vector2(-316f, 264f), M3Theme.GlassAccent);
            var title = M3.Text("Title", m_PickerPanel, "Quadri", M3Theme.Type.TitleLarge,
                new Vector2(-216f, 264f), new Vector2(160f, 34f), M3Theme.OnSurface, TextAlignmentOptions.Left);
            title.textWrappingMode = TextWrappingModes.NoWrap;

            m_SubtitleText = M3.Text("Subtitle", m_PickerPanel, "", M3Theme.Type.BodyMedium,
                new Vector2(-18f, 264f), new Vector2(228f, 28f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_SubtitleText.textWrappingMode = TextWrappingModes.NoWrap;

            m_ClearBtn = M3.Button("ClearFrames", m_PickerPanel, "Pulisci", M3Button.Variant.Tonal,
                new Vector2(154f, 264f), ClearAllPlacedFrames, "delete", width: 116f);
            m_ClearBtn.gameObject.SetActive(false);

            M3.IconButton("Refresh", m_PickerPanel, "refresh", M3Button.Variant.Tonal,
                new Vector2(248f, 264f), ForceRefreshGallery, 44f);
            M3.IconButton("Close", m_PickerPanel, "close", M3Button.Variant.Tonal,
                new Vector2(304f, 264f), OnPickerClosed, 44f);

            // Thumbnail Grid (4 x 3)
            var gridGo = new GameObject("Gallery Grid", typeof(RectTransform));
            gridGo.transform.SetParent(m_PickerPanel, false);
            var gridRect = (RectTransform)gridGo.transform;
            gridRect.anchoredPosition = Vector2.zero;
            gridRect.sizeDelta = new Vector2(680f, 444f);
            m_GridRoot = gridGo;

            var startX = -(QuadriLayout.Columns - 1) * k_TileStepX * 0.5f;
            var startY = (QuadriLayout.Rows - 1) * k_TileStepY * 0.5f - 6f;
            for (var i = 0; i < QuadriLayout.PerPage; i++)
            {
                var col = i % QuadriLayout.Columns;
                var row = i / QuadriLayout.Columns;
                var pos = new Vector2(startX + col * k_TileStepX, startY - row * k_TileStepY);
                m_Tiles[i] = CreateTile(gridRect, i, pos);
            }

            // Permission Card (shown on Android before READ_MEDIA_IMAGES / READ_EXTERNAL_STORAGE is granted)
            var permBg = M3.Surface("Permission Card", m_PickerPanel, new Vector2(0f, -8f), new Vector2(540f, 200f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, raycastTarget: false, style: VisionSurfaceStyle.Recessed);
            m_PermissionCard = permBg.gameObject;
            M3.Icon("PermIcon", permBg.rectTransform, "photo_library", 40f, new Vector2(0f, 52f), M3Theme.GlassAccent);
            M3.Text("PermMsg", permBg.rectTransform,
                "Consenti l'accesso alle foto del telefono per scegliere immagini e appenderle come finestre pulite nella stanza.",
                M3Theme.Type.BodyMedium, new Vector2(0f, 0f), new Vector2(476f, 52f),
                M3Theme.OnSurface, TextAlignmentOptions.Center);
            M3.Button("AllowGallery", permBg.rectTransform, "Consenti accesso", M3Button.Variant.Filled,
                new Vector2(0f, -58f), RequestPermissionClicked, "check", width: 224f);
            m_PermissionCard.SetActive(false);

            m_EmptyText = M3.Text("EmptyMsg", m_PickerPanel, "", M3Theme.Type.BodyLarge,
                new Vector2(0f, -8f), new Vector2(540f, 72f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_EmptyText.gameObject.SetActive(false);

            // Footer pagination
            var footerGo = new GameObject("Footer", typeof(RectTransform));
            footerGo.transform.SetParent(m_PickerPanel, false);
            var footerRect = (RectTransform)footerGo.transform;
            footerRect.anchoredPosition = new Vector2(0f, -266f);
            footerRect.sizeDelta = new Vector2(400f, 44f);
            m_FooterRoot = footerGo;

            m_PrevBtn = M3.IconButton("PrevPage", footerRect, "chevron_left", M3Button.Variant.Tonal,
                new Vector2(-116f, 0f), () => ChangePage(-1), 40f);
            m_PageText = M3.Text("PageText", footerRect, "Pagina 1 / 1", M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(150f, 30f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_NextBtn = M3.IconButton("NextPage", footerRect, "chevron_right", M3Button.Variant.Tonal,
                new Vector2(116f, 0f), () => ChangePage(1), 40f);

            m_PickerPanel.gameObject.AddComponent<QuadriScrollTarget>().Initialize(this);

            var head = m_Core != null ? m_Core.Head : null;
            if (head != null)
                WorldUi.PlaceInFront(m_PickerPanel, head, k_PickerDistance, 0f);
            M3Fade.Show(m_PickerPanel.gameObject);
            FloatingWindowHandle.Configure(m_PickerPanel, OnPickerClosed, restoreOnRecenter: true);
        }

        GalleryTile CreateTile(RectTransform parent, int slot, Vector2 pos)
        {
            var go = new GameObject($"Photo Tile {slot}", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(k_TileWidth, k_TileHeight);

            var backing = M3.Surface("Backing", rect, Vector2.zero, new Vector2(k_TileWidth, k_TileHeight),
                M3Theme.GlassRecessed, M3Theme.k_ShapeCard, raycastTarget: true, style: VisionSurfaceStyle.Recessed);

            var thumb = M3.RoundedScreen("Thumb", rect, Vector2.zero, new Vector2(k_TileWidth - 4f, k_TileHeight - 4f),
                M3Theme.k_ShapeCard - 2f, raycastTarget: false, rimStrength: 0.45f, sheenStrength: 0.14f);
            thumb.color = new Color(1f, 1f, 1f, 0.98f);
            thumb.gameObject.SetActive(false);

            var stateImg = M3.Surface("State", rect, Vector2.zero, new Vector2(k_TileWidth, k_TileHeight),
                Color.clear, M3Theme.k_ShapeCard, raycastTarget: false, style: VisionSurfaceStyle.Pure);

            var loadingLbl = M3.Text("Loading", rect, "…", M3Theme.Type.TitleMedium,
                Vector2.zero, new Vector2(k_TileWidth - 16f, 28f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);

            var btn = go.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = backing;
            var stateLayer = go.AddComponent<M3StateLayer>();
            stateLayer.Initialize(stateImg, btn, M3Theme.OnSurface);

            var captured = slot;
            btn.onClick.AddListener(() => OnTileClicked(captured));

            go.SetActive(false);
            return new GalleryTile
            {
                Root = go,
                Thumbnail = thumb,
                LoadingLabel = loadingLbl,
            };
        }

        void RefreshPermissionAndGallery(bool forceRefresh)
        {
            m_HasPermission = CheckPermissionNative();
            if (m_HasPermission && (forceRefresh || m_Photos.Count == 0))
                RequestRefreshNative();
            UpdatePickerUiState();
        }

        void ForceRefreshGallery()
        {
            ClearThumbnailCache();
            m_KnownListVersion = -1;
            m_KnownThumbVersion = -1;
            RefreshPermissionAndGallery(forceRefresh: true);
        }

        void RequestPermissionClicked()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("requestPermission");
#endif
        }

        void ChangePage(int delta)
        {
            var pages = QuadriLayout.PageCount(m_Photos.Count);
            var next = Mathf.Clamp(m_Page + delta, 0, pages - 1);
            if (next == m_Page)
                return;
            m_Page = next;
            UpdatePickerUiState();
        }

        void OnTileClicked(int slot)
        {
            var index = m_Page * QuadriLayout.PerPage + slot;
            if (index < 0 || index >= m_Photos.Count)
                return;

            var item = m_Photos[index];
            m_PendingFullId = item.Id;
            m_PendingFullItem = item;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge != null)
            {
                var existing = m_Bridge.Call<string>("getFullImagePath", item.Id);
                if (!string.IsNullOrEmpty(existing) && File.Exists(existing))
                {
                    m_PendingFullId = -1;
                    SpawnPhotoWindow(item, existing);
                    return;
                }
                m_Bridge.Call("requestFullImage", item.Id, k_FullImageMaxDim);
                UpdateSubtitle();
            }
#endif
        }

        void SpawnPhotoWindow(PhotoItem photo, string filePath)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quadri] Impossibile leggere immagine {filePath}: {ex.Message}");
                return;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
            {
                Destroy(tex);
                Debug.LogWarning($"[Quadri] Impossibile decodificare immagine {photo.Name}");
                return;
            }

            if (m_PlacedWindows.Count >= QuadriLayout.MaxPlacedFrames)
            {
                var oldest = m_PlacedWindows[0];
                m_PlacedWindows.RemoveAt(0);
                DestroyPlacedWindow(oldest);
            }

            var size = QuadriLayout.ComputeWindowSize(tex.width, tex.height);
            var panel = WorldUi.BorderlessWindow($"Quadro {photo.Id}", transform, size, k_FrameDistance);
            var screen = M3.RoundedScreen("Photo", panel, Vector2.zero, size, M3Theme.k_ShapePanel,
                raycastTarget: true, rimStrength: 0.55f, sheenStrength: 0.18f);
            screen.texture = tex;

            var win = new PlacedPhotoWindow
            {
                Id = photo.Id,
                Panel = panel,
                Texture = tex,
            };
            m_PlacedWindows.Add(win);

            var head = m_Core != null ? m_Core.Head : null;
            if (head != null)
                WorldUi.PlaceInFront(panel, head, k_FrameDistance, 0f);
            M3Fade.Show(panel.gameObject);
            FloatingWindowHandle.Configure(panel, () => ClosePhotoWindow(win), restoreOnRecenter: true);

            if (m_PickerPanel != null && M3Fade.IsShown(m_PickerPanel.gameObject))
                M3Fade.Hide(m_PickerPanel.gameObject);

            UpdateSubtitle();
            Debug.Log($"[Quadri] Aperta foto '{photo.Name}' ({tex.width}x{tex.height}) -> finestra {size.x:F0}x{size.y:F0} dp");
        }

        void ClosePhotoWindow(PlacedPhotoWindow win)
        {
            if (win == null)
                return;
            m_PlacedWindows.Remove(win);
            DestroyPlacedWindow(win);
            UpdatePickerUiState();
            if (m_PlacedWindows.Count == 0 && !IsPickerVisible)
                m_Core?.Demos?.CloseApp("quadri");
        }

        void ClearAllPlacedFrames()
        {
            for (var i = 0; i < m_PlacedWindows.Count; i++)
                DestroyPlacedWindow(m_PlacedWindows[i]);
            m_PlacedWindows.Clear();
            UpdatePickerUiState();
        }

        void OnPickerClosed()
        {
            if (m_PickerPanel != null)
                M3Fade.Hide(m_PickerPanel.gameObject);
            if (m_PlacedWindows.Count == 0)
                m_Core?.Demos?.CloseApp("quadri");
        }

        void UpdatePickerUiState()
        {
            if (m_PickerPanel == null)
                return;

            var isAndroidDevice = false;
#if UNITY_ANDROID && !UNITY_EDITOR
            isAndroidDevice = m_Bridge != null;
#endif
            var showPerm = isAndroidDevice && !m_HasPermission;
            var hasPhotos = !showPerm && m_Photos.Count > 0;

            if (m_PermissionCard != null)
                m_PermissionCard.SetActive(showPerm);
            if (m_GridRoot != null)
                m_GridRoot.SetActive(hasPhotos);
            if (m_FooterRoot != null)
                m_FooterRoot.SetActive(hasPhotos);
            if (m_ClearBtn != null)
                m_ClearBtn.gameObject.SetActive(m_PlacedWindows.Count > 0);

            if (m_EmptyText != null)
            {
                var showEmpty = !showPerm && !hasPhotos;
                m_EmptyText.gameObject.SetActive(showEmpty);
                if (showEmpty)
                {
                    m_EmptyText.text = !isAndroidDevice
                        ? "La galleria del telefono è disponibile su Android."
                        : (m_WasRefreshing ? "Caricamento foto dalla galleria…" : "Nessuna foto trovata nella galleria.");
                }
            }

            UpdateSubtitle();
            if (!hasPhotos)
                return;

            var pages = QuadriLayout.PageCount(m_Photos.Count);
            m_Page = Mathf.Clamp(m_Page, 0, pages - 1);
            if (m_PageText != null)
                m_PageText.text = $"Pagina {m_Page + 1} / {pages}";
            if (m_PrevBtn != null)
                m_PrevBtn.Interactable = m_Page > 0;
            if (m_NextBtn != null)
                m_NextBtn.Interactable = m_Page < pages - 1;

            var startIndex = m_Page * QuadriLayout.PerPage;
            RequestThumbnailsNative(startIndex, QuadriLayout.PerPage);
            RefreshVisibleTiles(startIndex);
        }

        void UpdateSubtitle()
        {
            if (m_SubtitleText == null)
                return;
            if (m_PendingFullId >= 0)
            {
                m_SubtitleText.text = "Apertura foto…";
                return;
            }
            if (!m_HasPermission)
            {
                m_SubtitleText.text = "Accesso galleria richiesto";
                return;
            }
            if (m_PlacedWindows.Count > 0)
            {
                m_SubtitleText.text = m_PlacedWindows.Count == 1
                    ? "1 quadro nella stanza"
                    : $"{m_PlacedWindows.Count} quadri nella stanza";
                return;
            }
            m_SubtitleText.text = m_Photos.Count == 1 ? "1 foto" : $"{m_Photos.Count} foto";
        }

        void RefreshVisibleTiles(int startIndex)
        {
            var tileAspect = (k_TileWidth - 4f) / (k_TileHeight - 4f);
            for (var i = 0; i < m_Tiles.Length; i++)
            {
                var tile = m_Tiles[i];
                var idx = startIndex + i;
                var valid = idx < m_Photos.Count;
                tile.Root.SetActive(valid);
                if (!valid)
                    continue;

                var item = m_Photos[idx];
                var tex = GetOrLoadThumbTexture(item.Id);
                if (tex != null)
                {
                    tile.Thumbnail.texture = tex;
                    tile.Thumbnail.uvRect = QuadriLayout.ComputeCenterCropUv(tex.width, tex.height, tileAspect);
                    tile.Thumbnail.gameObject.SetActive(true);
                    tile.LoadingLabel.gameObject.SetActive(false);
                }
                else
                {
                    tile.Thumbnail.texture = null;
                    tile.Thumbnail.gameObject.SetActive(false);
                    tile.LoadingLabel.gameObject.SetActive(true);
                }
            }
        }

        Texture2D GetOrLoadThumbTexture(long id)
        {
            if (m_ThumbTextures.TryGetValue(id, out var cached) && cached != null)
                return cached;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge == null)
                return null;
            var path = m_Bridge.Call<string>("getThumbnailPath", id);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                {
                    m_ThumbTextures[id] = tex;
                    return tex;
                }
                Destroy(tex);
            }
            catch (Exception) { }
#endif
            return null;
        }

        void InitBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge != null)
                return;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                if (activity != null)
                    m_Bridge = new AndroidJavaObject("com.brenpoly.cardboardxr.gallery.GalleryBridge", activity);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quadri] Bridge Android non disponibile: {ex.Message}");
            }
#endif
        }

        bool CheckPermissionNative()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return m_Bridge != null && m_Bridge.Call<bool>("hasPermission");
#else
            return true;
#endif
        }

        void RequestRefreshNative()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("refreshAsync");
#endif
        }

        void RequestThumbnailsNative(int startIndex, int count)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("requestThumbnails", startIndex, count);
#endif
        }

        void PollNativeUpdates()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge == null)
                return;

            var granted = m_Bridge.Call<bool>("hasPermission");
            if (granted != m_HasPermission)
            {
                m_HasPermission = granted;
                if (granted)
                    m_Bridge.Call("refreshAsync");
                UpdatePickerUiState();
            }

            if (!m_HasPermission)
                return;

            var refreshing = m_Bridge.Call<bool>("isRefreshing");
            var listVer = m_Bridge.Call<int>("getListVersion");
            if (listVer != m_KnownListVersion)
            {
                m_KnownListVersion = listVer;
                m_WasRefreshing = refreshing;
                ReadPhotoListNative();
                UpdatePickerUiState();
            }
            else if (refreshing != m_WasRefreshing)
            {
                m_WasRefreshing = refreshing;
                UpdatePickerUiState();
            }

            var thumbVer = m_Bridge.Call<int>("getThumbVersion");
            if (thumbVer != m_KnownThumbVersion)
            {
                m_KnownThumbVersion = thumbVer;
                if (m_GridRoot != null && m_GridRoot.activeSelf)
                    RefreshVisibleTiles(m_Page * QuadriLayout.PerPage);
            }

            var fullVer = m_Bridge.Call<int>("getFullVersion");
            if (fullVer != m_KnownFullVersion || m_PendingFullId >= 0)
            {
                m_KnownFullVersion = fullVer;
                if (m_PendingFullId >= 0)
                {
                    var fullPath = m_Bridge.Call<string>("getFullImagePath", m_PendingFullId);
                    if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    {
                        var pending = m_PendingFullItem;
                        m_PendingFullId = -1;
                        SpawnPhotoWindow(pending, fullPath);
                    }
                }
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void ReadPhotoListNative()
        {
            m_Photos.Clear();
            if (m_Bridge == null)
                return;
            var count = m_Bridge.Call<int>("getPhotoCount");
            for (var i = 0; i < count; i++)
            {
                m_Photos.Add(new PhotoItem
                {
                    Id = m_Bridge.Call<long>("getPhotoId", i),
                    Name = m_Bridge.Call<string>("getPhotoName", i) ?? "",
                    Width = m_Bridge.Call<int>("getPhotoWidth", i),
                    Height = m_Bridge.Call<int>("getPhotoHeight", i),
                });
            }
            m_Page = Mathf.Clamp(m_Page, 0, QuadriLayout.PageCount(m_Photos.Count) - 1);
        }
#endif

        void DestroyPlacedWindow(PlacedPhotoWindow win)
        {
            if (win == null)
                return;
            if (win.Panel != null)
                Destroy(win.Panel.gameObject);
            if (win.Texture != null)
                Destroy(win.Texture);
        }

        void ClearThumbnailCache()
        {
            foreach (var kv in m_ThumbTextures)
            {
                if (kv.Value != null)
                    Destroy(kv.Value);
            }
            m_ThumbTextures.Clear();
        }

        void CleanupAllResources()
        {
            for (var i = 0; i < m_PlacedWindows.Count; i++)
                DestroyPlacedWindow(m_PlacedWindows[i]);
            m_PlacedWindows.Clear();
            ClearThumbnailCache();
            if (m_PickerPanel != null)
            {
                Destroy(m_PickerPanel.gameObject);
                m_PickerPanel = null;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge != null)
            {
                try { m_Bridge.Call("shutdown"); } catch (Exception) { }
                m_Bridge.Dispose();
                m_Bridge = null;
            }
#endif
        }

        sealed class QuadriScrollTarget : MonoBehaviour, IWatchScrollable
        {
            QuadriDemo m_Demo;
            float m_Accum;
            float m_LastTurnAt = float.NegativeInfinity;

            public void Initialize(QuadriDemo demo) => m_Demo = demo;

            public bool TryHandleWatchScroll(float rotarySteps)
            {
                if (m_Demo == null || Mathf.Abs(rotarySteps) < 1e-4f)
                    return false;
                m_Accum += rotarySteps;
                if (Mathf.Abs(m_Accum) < 0.08f)
                    return true;
                var dir = m_Accum > 0f ? 1 : -1;
                m_Accum = 0f;
                if (Time.unscaledTime - m_LastTurnAt < 0.28f)
                    return true;
                m_LastTurnAt = Time.unscaledTime;
                m_Demo.ChangePage(dir);
                return true;
            }
        }
    }
}
