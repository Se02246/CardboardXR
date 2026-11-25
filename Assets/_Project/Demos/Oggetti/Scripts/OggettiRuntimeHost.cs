using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.Networking;

namespace CardboardXR.Demos.Oggetti
{
    // Persistent session-level host keeping all placed Sketchfab 3D objects alive and anchored
    // on their surfaces across apps, while managing Sketchfab Data API v3 token auth, server-side
    // search/pagination, thumbnail caching, GLB/glTF archive extraction, and surface manipulation.
    public class OggettiRuntimeHost : MonoBehaviour, IWatchRotaryHandler
    {
        const string k_TokenPrefsKey = "CardboardXR.SketchfabApiToken";
        const float k_FallbackDistance = 1.65f;
        const float k_FallbackFloorDepth = 1.35f;
        const float k_RotateHeadSensitivity = 2.4f;
        const float k_ScaleHeadSensitivity = 0.045f;
        const float k_CrownRotateStepDeg = 18f;
        const float k_CrownScaleStep = 0.14f;

        enum InteractionMode
        {
            None,
            PlacingNew,
            MovingExisting,
            RotatingExisting,
            ScalingExisting,
        }

        static OggettiRuntimeHost s_Instance;

        readonly List<SketchfabModelEntry> m_CurrentResults = new();
        readonly Dictionary<string, Texture2D> m_ThumbTextures = new(StringComparer.Ordinal);
        readonly HashSet<string> m_ThumbInFlight = new(StringComparer.Ordinal);
        readonly List<PlacedPolyHavenObject> m_PlacedObjects = new();

        Material m_BaseMaterialTemplate;
        HeadPoseFusion m_Fusion;
        GazePointer m_Gaze;

        string m_ApiToken = "";
        string m_ConnectedUsername = "";
        bool m_IsVerifyingToken;
        string m_AuthError = "";

        Coroutine m_SearchCoroutine;
        bool m_IsSearching;
        string m_NextCursor = "";
        string m_PreviousCursor = "";

        string m_DownloadingAssetId = "";
        float m_DownloadProgress;
        string m_StatusMessage = "";

        InteractionMode m_Mode = InteractionMode.None;
        PlacedPolyHavenObject m_ActiveTarget;
        int m_ArmFrame = -1;
        float m_StartHeadYaw;
        float m_StartObjectYaw;
        float m_StartHeadPitch;
        float m_StartScale = 1f;
        IWatchRotaryHandler m_PrevRotary;

        public static OggettiRuntimeHost Instance => s_Instance;

        public IReadOnlyList<SketchfabModelEntry> CurrentResults => m_CurrentResults;
        public bool IsSearching => m_IsSearching;
        public string NextCursor => m_NextCursor;
        public string PreviousCursor => m_PreviousCursor;

        public string ApiToken => m_ApiToken;
        public bool HasToken => !string.IsNullOrEmpty(m_ApiToken);
        public string ConnectedUsername => m_ConnectedUsername;
        public bool IsVerifyingToken => m_IsVerifyingToken;
        public string AuthError => m_AuthError;

        public string DownloadingAssetId => m_DownloadingAssetId;
        public float DownloadProgress => m_DownloadProgress;
        public string StatusMessage => m_StatusMessage;
        public int PlacedCount => m_PlacedObjects.Count;
        public Transform HeadTransform => m_Fusion != null ? m_Fusion.transform : null;
        public GazePointer ActiveGaze => m_Gaze;

        public event Action AuthStateChanged;
        public event Action CatalogPageLoaded;
        public event Action<string> ThumbnailUpdated;
        public event Action DownloadProgressUpdated;
        public event Action PlacedCountChanged;
        public event Action<string> StatusUpdated;

        static string ThumbsCacheDir => Path.Combine(Application.persistentDataPath, "sketchfab_thumbs");
        static string ModelsCacheDir => Path.Combine(Application.persistentDataPath, "sketchfab_models");

        public static OggettiRuntimeHost GetOrCreate(CoreServices core, Material baseMaterialTemplate)
        {
            if (s_Instance == null)
            {
                var go = new GameObject("Oggetti Runtime Host");
                DontDestroyOnLoad(go);
                s_Instance = go.AddComponent<OggettiRuntimeHost>();
            }
            s_Instance.Configure(core, baseMaterialTemplate);
            return s_Instance;
        }

        void Awake()
        {
            m_ApiToken = SketchfabClient.SanitizeToken(PlayerPrefs.GetString(k_TokenPrefsKey, ""));
        }

        void Configure(CoreServices core, Material baseMaterialTemplate)
        {
            if (baseMaterialTemplate != null)
                m_BaseMaterialTemplate = baseMaterialTemplate;
            if (core != null)
            {
                m_Fusion = core.Fusion;
                m_Gaze = core.Gaze;
            }
        }

        void OnDestroy()
        {
            CancelInteraction(revertIfNew: true);
            if (s_Instance == this)
                s_Instance = null;
            foreach (var kv in m_ThumbTextures)
            {
                if (kv.Value != null)
                    Destroy(kv.Value);
            }
            m_ThumbTextures.Clear();
        }

        public void VerifySavedTokenIfAny()
        {
            if (HasToken && string.IsNullOrEmpty(m_ConnectedUsername) && !m_IsVerifyingToken)
                StartCoroutine(VerifyTokenCoroutine(m_ApiToken, clearOnFailure: false, onDone: null));
        }

        public void SetTokenAndVerify(string rawToken, Action<bool, string> onDone = null)
        {
            var token = SketchfabClient.SanitizeToken(rawToken);
            if (string.IsNullOrEmpty(token))
            {
                m_AuthError = "Nessun token valido inserito.";
                AuthStateChanged?.Invoke();
                onDone?.Invoke(false, m_AuthError);
                return;
            }
            StartCoroutine(VerifyTokenCoroutine(token, clearOnFailure: true, onDone));
        }

        public void ClearToken()
        {
            m_ApiToken = "";
            m_ConnectedUsername = "";
            m_AuthError = "";
            PlayerPrefs.DeleteKey(k_TokenPrefsKey);
            PlayerPrefs.Save();
            AuthStateChanged?.Invoke();
        }

        IEnumerator VerifyTokenCoroutine(string candidateToken, bool clearOnFailure, Action<bool, string> onDone)
        {
            m_IsVerifyingToken = true;
            m_AuthError = "";
            AuthStateChanged?.Invoke();

            using var req = UnityWebRequest.Get(SketchfabClient.BuildMeEndpoint());
            req.SetRequestHeader("Authorization", $"Token {candidateToken}");
            req.SetRequestHeader("User-Agent", PolyHavenClient.UserAgent);
            req.timeout = 15;
            yield return req.SendWebRequest();

            m_IsVerifyingToken = false;
            if (req.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(req.downloadHandler.text))
            {
                var profile = SketchfabClient.ParseUserProfile(req.downloadHandler.text);
                if (profile.Valid)
                {
                    m_ApiToken = candidateToken;
                    m_ConnectedUsername = !string.IsNullOrEmpty(profile.Username) ? profile.Username : profile.DisplayName;
                    m_AuthError = "";
                    PlayerPrefs.SetString(k_TokenPrefsKey, m_ApiToken);
                    PlayerPrefs.Save();
                    AuthStateChanged?.Invoke();
                    onDone?.Invoke(true, m_ConnectedUsername);
                    Debug.Log($"[Oggetti] Connesso a Sketchfab come @{m_ConnectedUsername}");
                    yield break;
                }
            }

            if (clearOnFailure || req.responseCode == 401 || req.responseCode == 403)
            {
                m_ApiToken = "";
                m_ConnectedUsername = "";
                PlayerPrefs.DeleteKey(k_TokenPrefsKey);
                PlayerPrefs.Save();
            }
            m_AuthError = req.responseCode == 401 || req.responseCode == 403
                ? "Token Sketchfab non valido o scaduto."
                : "Verifica token fallita (controlla la connessione).";
            AuthStateChanged?.Invoke();
            onDone?.Invoke(false, m_AuthError);
        }

        public void RequestSearchPage(string categorySlug, string query, string sortBy, string cursor)
        {
            if (m_SearchCoroutine != null)
            {
                StopCoroutine(m_SearchCoroutine);
                m_SearchCoroutine = null;
            }
            m_SearchCoroutine = StartCoroutine(FetchSearchCoroutine(categorySlug, query, sortBy, cursor));
        }

        IEnumerator FetchSearchCoroutine(string categorySlug, string query, string sortBy, string cursor)
        {
            m_IsSearching = true;
            m_StatusMessage = "Caricamento catalogo Sketchfab…";
            StatusUpdated?.Invoke(m_StatusMessage);
            CatalogPageLoaded?.Invoke();

            var url = SketchfabClient.BuildSearchUrl(categorySlug, query, sortBy, cursor, OggettiLayout.PerPage);
            using var req = UnityWebRequest.Get(url);
            if (HasToken)
                req.SetRequestHeader("Authorization", $"Token {m_ApiToken}");
            req.SetRequestHeader("User-Agent", PolyHavenClient.UserAgent);
            req.timeout = 20;
            yield return req.SendWebRequest();

            m_IsSearching = false;
            m_SearchCoroutine = null;
            if (req.result == UnityWebRequest.Result.Success && !string.IsNullOrEmpty(req.downloadHandler.text))
            {
                var page = SketchfabClient.ParseSearchResponse(req.downloadHandler.text);
                m_CurrentResults.Clear();
                if (page.Results != null)
                    m_CurrentResults.AddRange(page.Results);
                m_NextCursor = page.NextCursor ?? "";
                m_PreviousCursor = page.PreviousCursor ?? "";
                m_StatusMessage = "";
                StatusUpdated?.Invoke("");
                CatalogPageLoaded?.Invoke();
                yield break;
            }

            m_StatusMessage = "Impossibile caricare il catalogo Sketchfab.";
            StatusUpdated?.Invoke(m_StatusMessage);
            CatalogPageLoaded?.Invoke();
        }

        public Texture2D GetOrRequestThumbnail(SketchfabModelEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Uid))
                return null;
            if (m_ThumbTextures.TryGetValue(entry.Uid, out var existing) && existing != null)
                return existing;

            var thumbPath = Path.Combine(ThumbsCacheDir, entry.Uid + ".jpg");
            if (File.Exists(thumbPath))
            {
                var tex = LoadTextureFromFile(thumbPath);
                if (tex != null)
                {
                    m_ThumbTextures[entry.Uid] = tex;
                    return tex;
                }
            }

            if (!m_ThumbInFlight.Contains(entry.Uid) && !string.IsNullOrEmpty(entry.ThumbnailUrl))
            {
                m_ThumbInFlight.Add(entry.Uid);
                StartCoroutine(DownloadThumbnailCoroutine(entry.Uid, entry.ThumbnailUrl, thumbPath));
            }
            return null;
        }

        IEnumerator DownloadThumbnailCoroutine(string uid, string url, string destPath)
        {
            using var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("User-Agent", PolyHavenClient.UserAgent);
            req.timeout = 15;
            yield return req.SendWebRequest();

            m_ThumbInFlight.Remove(uid);
            if (req.result != UnityWebRequest.Result.Success)
                yield break;

            var bytes = req.downloadHandler.data;
            if (bytes == null || bytes.Length == 0)
                yield break;

            TryWriteBytesAtomic(destPath, bytes);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
            {
                m_ThumbTextures[uid] = tex;
                ThumbnailUpdated?.Invoke(uid);
            }
            else
            {
                Destroy(tex);
            }
        }

        static string GetModelDir(string uid) => Path.Combine(ModelsCacheDir, uid);
        static string GetGlbLocalPath(string uid) => Path.Combine(GetModelDir(uid), "model.glb");

        static string FindCachedGltfPath(string uid)
        {
            var dir = GetModelDir(uid);
            if (!Directory.Exists(dir))
                return null;
            var preferred = Path.Combine(dir, "scene.gltf");
            if (File.Exists(preferred))
                return preferred;
            try
            {
                var files = Directory.GetFiles(dir, "*.gltf", SearchOption.AllDirectories);
                return files.Length > 0 ? files[0] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool IsModelCached(string uid) =>
            !string.IsNullOrEmpty(uid) && (File.Exists(GetGlbLocalPath(uid)) || !string.IsNullOrEmpty(FindCachedGltfPath(uid)));

        public void DownloadAndPlace(SketchfabModelEntry entry, Action<bool, bool> onDone)
        {
            if (string.IsNullOrEmpty(entry.Uid) || !string.IsNullOrEmpty(m_DownloadingAssetId))
                return;

            CancelInteraction(revertIfNew: true);

            var cachedGlb = GetGlbLocalPath(entry.Uid);
            if (File.Exists(cachedGlb))
            {
                var model = PolyHavenGltfLoader.LoadFromGlbFile(cachedGlb, m_BaseMaterialTemplate);
                if (model != null && SpawnLoadedModel(entry.Uid, entry.Name, model))
                {
                    onDone?.Invoke(true, false);
                    return;
                }
                model?.DestroyResources();
            }

            var cachedGltf = FindCachedGltfPath(entry.Uid);
            if (!string.IsNullOrEmpty(cachedGltf))
            {
                var model = PolyHavenGltfLoader.LoadFromFile(cachedGltf, m_BaseMaterialTemplate);
                if (model != null && SpawnLoadedModel(entry.Uid, entry.Name, model))
                {
                    onDone?.Invoke(true, false);
                    return;
                }
                model?.DestroyResources();
            }

            if (!HasToken)
            {
                m_AuthError = "Inserisci il tuo Token API Sketchfab per scaricare il modello.";
                AuthStateChanged?.Invoke();
                onDone?.Invoke(false, true);
                return;
            }

            StartCoroutine(DownloadModelCoroutine(entry, onDone));
        }

        IEnumerator DownloadModelCoroutine(SketchfabModelEntry entry, Action<bool, bool> onDone)
        {
            m_DownloadingAssetId = entry.Uid;
            m_DownloadProgress = 0.03f;
            m_StatusMessage = $"Richiesta {entry.Name}…";
            DownloadProgressUpdated?.Invoke();
            StatusUpdated?.Invoke(m_StatusMessage);

            var downloadApi = SketchfabClient.BuildDownloadEndpoint(entry.Uid);
            using var metaReq = UnityWebRequest.Get(downloadApi);
            metaReq.SetRequestHeader("Authorization", $"Token {m_ApiToken}");
            metaReq.SetRequestHeader("User-Agent", PolyHavenClient.UserAgent);
            metaReq.timeout = 20;
            yield return metaReq.SendWebRequest();

            if (metaReq.responseCode == 401 || metaReq.responseCode == 403)
            {
                m_AuthError = "Token Sketchfab non valido o scaduto.";
                AuthStateChanged?.Invoke();
                FinishDownloadFailure(m_AuthError, needsAuth: true, onDone);
                yield break;
            }
            if (metaReq.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(metaReq.downloadHandler.text))
            {
                FinishDownloadFailure($"Errore download {entry.Name}.", needsAuth: false, onDone);
                yield break;
            }

            var link = SketchfabClient.ParseDownloadResponse(metaReq.downloadHandler.text);
            if (!link.IsValid)
            {
                FinishDownloadFailure($"Formato glTF/GLB non disponibile per {entry.Name}.", needsAuth: false, onDone);
                yield break;
            }

            m_StatusMessage = $"Scaricamento {entry.Name}…";
            StatusUpdated?.Invoke(m_StatusMessage);

            // Signed S3 archive URL carries temporary query auth — do not attach Authorization header.
            using var archiveReq = UnityWebRequest.Get(link.Url);
            archiveReq.timeout = 60;
            var op = archiveReq.SendWebRequest();
            while (!op.isDone)
            {
                m_DownloadProgress = 0.05f + 0.90f * archiveReq.downloadProgress;
                DownloadProgressUpdated?.Invoke();
                yield return null;
            }

            if (archiveReq.result != UnityWebRequest.Result.Success || archiveReq.downloadHandler.data == null)
            {
                FinishDownloadFailure($"Download interrotto per {entry.Name}.", needsAuth: false, onDone);
                yield break;
            }

            var data = archiveReq.downloadHandler.data;
            LoadedGltfModel loadedModel = null;
            if (link.IsGlb)
            {
                var glbPath = GetGlbLocalPath(entry.Uid);
                TryWriteBytesAtomic(glbPath, data);
                loadedModel = PolyHavenGltfLoader.LoadFromGlbBytes(data, m_BaseMaterialTemplate);
            }
            else
            {
                var modelDir = GetModelDir(entry.Uid);
                if (ExtractZipSafely(data, modelDir))
                {
                    var gltfPath = FindCachedGltfPath(entry.Uid);
                    if (!string.IsNullOrEmpty(gltfPath))
                        loadedModel = PolyHavenGltfLoader.LoadFromFile(gltfPath, m_BaseMaterialTemplate);
                }
            }

            if (loadedModel == null || loadedModel.Root == null)
            {
                loadedModel?.DestroyResources();
                FinishDownloadFailure($"Impossibile aprire il modello {entry.Name}.", needsAuth: false, onDone);
                yield break;
            }

            m_DownloadingAssetId = "";
            m_DownloadProgress = 1f;
            m_StatusMessage = "";
            DownloadProgressUpdated?.Invoke();
            StatusUpdated?.Invoke("");

            var ok = SpawnLoadedModel(entry.Uid, entry.Name, loadedModel);
            onDone?.Invoke(ok, false);
        }

        static bool ExtractZipSafely(byte[] zipBytes, string destDir)
        {
            try
            {
                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, recursive: true);
                Directory.CreateDirectory(destDir);
                var fullDestRoot = Path.GetFullPath(destDir);

                using var ms = new MemoryStream(zipBytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;
                    var cleanRel = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    var targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, cleanRel));
                    if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parentDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        Directory.CreateDirectory(parentDir);
                    using var entryStream = entry.Open();
                    using var outStream = File.Create(targetPath);
                    entryStream.CopyTo(outStream);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Oggetti] Estrazione zip fallita: {ex.Message}");
                return false;
            }
        }

        void FinishDownloadFailure(string message, bool needsAuth, Action<bool, bool> onDone)
        {
            m_DownloadingAssetId = "";
            m_DownloadProgress = 0f;
            m_StatusMessage = message;
            DownloadProgressUpdated?.Invoke();
            StatusUpdated?.Invoke(message);
            onDone?.Invoke(false, needsAuth);
        }

        bool SpawnLoadedModel(string assetId, string assetName, LoadedGltfModel model)
        {
            if (model == null || model.Root == null)
                return false;

            if (m_PlacedObjects.Count >= OggettiLayout.MaxPlacedObjects)
            {
                var oldest = m_PlacedObjects[0];
                RemoveObject(oldest);
            }

            var hostGo = new GameObject($"PlacedObject - {assetId}");
            hostGo.transform.SetParent(transform, false);
            var obj = hostGo.AddComponent<PlacedPolyHavenObject>();
            obj.Initialize(this, assetId, assetName, model);
            m_PlacedObjects.Add(obj);
            PlacedCountChanged?.Invoke();

            BeginInteraction(InteractionMode.PlacingNew, obj);
            Debug.Log($"[Oggetti] Iniziato posizionamento '{assetName}' ({assetId})");
            return true;
        }

        public void BeginMove(PlacedPolyHavenObject obj)
        {
            if (obj == null)
                return;
            if (m_Mode == InteractionMode.MovingExisting && m_ActiveTarget == obj)
            {
                ConfirmCurrentInteraction();
                return;
            }
            CancelInteraction(revertIfNew: true);
            BeginInteraction(InteractionMode.MovingExisting, obj);
        }

        public void BeginRotate(PlacedPolyHavenObject obj)
        {
            if (obj == null)
                return;
            if (m_Mode == InteractionMode.RotatingExisting && m_ActiveTarget == obj)
            {
                ConfirmCurrentInteraction();
                return;
            }
            CancelInteraction(revertIfNew: true);
            BeginInteraction(InteractionMode.RotatingExisting, obj);
        }

        public void BeginScale(PlacedPolyHavenObject obj)
        {
            if (obj == null)
                return;
            if (m_Mode == InteractionMode.ScalingExisting && m_ActiveTarget == obj)
            {
                ConfirmCurrentInteraction();
                return;
            }
            CancelInteraction(revertIfNew: true);
            BeginInteraction(InteractionMode.ScalingExisting, obj);
        }

        public void RemoveObject(PlacedPolyHavenObject obj)
        {
            if (obj == null)
                return;
            if (m_ActiveTarget == obj)
                CancelInteraction(revertIfNew: false);
            m_PlacedObjects.Remove(obj);
            Destroy(obj.gameObject);
            PlacedCountChanged?.Invoke();
        }

        public void ClearAllObjects()
        {
            CancelInteraction(revertIfNew: false);
            for (var i = m_PlacedObjects.Count - 1; i >= 0; i--)
            {
                if (m_PlacedObjects[i] != null)
                    Destroy(m_PlacedObjects[i].gameObject);
            }
            m_PlacedObjects.Clear();
            PlacedCountChanged?.Invoke();
        }

        void BeginInteraction(InteractionMode mode, PlacedPolyHavenObject obj)
        {
            m_Mode = mode;
            m_ActiveTarget = obj;
            m_ArmFrame = Time.frameCount;

            var head = HeadTransform;
            if (head != null)
            {
                var fwd = head.forward;
                m_StartHeadYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                m_StartHeadPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;
            }
            m_StartObjectYaw = obj.CurrentYawDeg;
            m_StartScale = obj.CurrentScale;

            obj.SetInteractionFlags(
                isMoving: mode == InteractionMode.PlacingNew || mode == InteractionMode.MovingExisting,
                isRotating: mode == InteractionMode.RotatingExisting,
                isScaling: mode == InteractionMode.ScalingExisting);

            if (SpatialSurfaceLocator.Instance != null)
            {
                SpatialSurfaceLocator.Instance.ExternalPlacementRequested =
                    mode == InteractionMode.PlacingNew || mode == InteractionMode.MovingExisting;
            }

            if (m_PrevRotary == null && !ReferenceEquals(PinchRouter.RotaryHandler, this))
                m_PrevRotary = PinchRouter.RotaryHandler;
            PinchRouter.RotaryHandler = this;
            GazePointer.TriggerInterceptor = OnGazeClickIntercepted;
            UpdateActiveInteractionPose();
        }

        bool OnGazeClickIntercepted()
        {
            if (m_Mode == InteractionMode.None || Time.frameCount == m_ArmFrame)
                return false;
            ConfirmCurrentInteraction();
            return true;
        }

        void ConfirmCurrentInteraction()
        {
            if (m_Mode == InteractionMode.None || m_ActiveTarget == null)
            {
                CancelInteraction(revertIfNew: false);
                return;
            }

            var target = m_ActiveTarget;
            var pos = target.transform.position;
            var yaw = target.CurrentYawDeg;
            EndInteractionCleanup();
            target.SetInteractionFlags(false, false, false);
            target.AnchorAt(pos, yaw);
            Debug.Log($"[Oggetti] Ancorato '{target.AssetName}' a {pos} (yaw={yaw:F0}°, scala={target.CurrentScale:F2}x)");
        }

        void CancelInteraction(bool revertIfNew)
        {
            if (m_Mode == InteractionMode.None)
                return;
            var prevMode = m_Mode;
            var target = m_ActiveTarget;
            EndInteractionCleanup();
            if (target == null)
                return;
            if (revertIfNew && prevMode == InteractionMode.PlacingNew)
            {
                m_PlacedObjects.Remove(target);
                Destroy(target.gameObject);
                PlacedCountChanged?.Invoke();
            }
            else
            {
                target.SetInteractionFlags(false, false, false);
            }
        }

        void EndInteractionCleanup()
        {
            m_Mode = InteractionMode.None;
            m_ActiveTarget = null;
            if (SpatialSurfaceLocator.Instance != null)
                SpatialSurfaceLocator.Instance.ExternalPlacementRequested = false;
            if (GazePointer.TriggerInterceptor == OnGazeClickIntercepted)
                GazePointer.TriggerInterceptor = null;
            if (ReferenceEquals(PinchRouter.RotaryHandler, this))
                PinchRouter.RotaryHandler = m_PrevRotary;
            m_PrevRotary = null;
        }

        void Update()
        {
            if (m_Mode == InteractionMode.None || m_ActiveTarget == null)
                return;
            UpdateActiveInteractionPose();
        }

        void UpdateActiveInteractionPose()
        {
            if (m_ActiveTarget == null)
                return;

            var head = HeadTransform;
            if (head == null)
                return;

            if (m_Mode == InteractionMode.PlacingNew || m_Mode == InteractionMode.MovingExisting)
            {
                var ray = m_Gaze != null ? m_Gaze.PointerRay : new Ray(head.position, head.forward);
                Vector3 targetPos;
                var locator = SpatialSurfaceLocator.Instance;
                if (locator != null && locator.TrySampleSurface(ray, out var hit))
                {
                    targetPos = hit.Point;
                }
                else
                {
                    var dir = ray.direction.sqrMagnitude > 1e-6f ? ray.direction.normalized : Vector3.forward;
                    if (dir.y < -0.18f)
                    {
                        var t = Mathf.Clamp(-k_FallbackFloorDepth / dir.y, 0.75f, 4.5f);
                        targetPos = ray.origin + dir * t;
                    }
                    else
                    {
                        targetPos = ray.origin + dir * k_FallbackDistance;
                    }
                }
                var flatFwd = Vector3.ProjectOnPlane(head.position - targetPos, Vector3.up);
                var yaw = m_Mode == InteractionMode.MovingExisting
                    ? m_StartObjectYaw
                    : (flatFwd.sqrMagnitude > 1e-4f ? Mathf.Atan2(flatFwd.x, flatFwd.z) * Mathf.Rad2Deg : m_StartObjectYaw);
                m_ActiveTarget.SetPreviewPose(targetPos, yaw);
                return;
            }

            if (m_Mode == InteractionMode.RotatingExisting)
            {
                var fwd = head.forward;
                var headYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                var deltaYaw = Mathf.DeltaAngle(m_StartHeadYaw, headYaw) * k_RotateHeadSensitivity;
                m_ActiveTarget.SetYaw(m_StartObjectYaw + deltaYaw);
                return;
            }

            if (m_Mode == InteractionMode.ScalingExisting)
            {
                var fwd = head.forward;
                var headPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -0.95f, 0.95f)) * Mathf.Rad2Deg;
                var deltaPitch = headPitch - m_StartHeadPitch;
                var factor = Mathf.Exp(deltaPitch * k_ScaleHeadSensitivity);
                m_ActiveTarget.SetScale(m_StartScale * factor);
            }
        }

        public bool TryHandleRotary(GameObject gazeTarget, float steps)
        {
            if (m_ActiveTarget == null || Mathf.Abs(steps) < 1e-4f)
                return false;
            if (m_Mode == InteractionMode.RotatingExisting)
            {
                m_StartObjectYaw += steps * k_CrownRotateStepDeg;
                m_ActiveTarget.SetYaw(m_ActiveTarget.CurrentYawDeg + steps * k_CrownRotateStepDeg);
                return true;
            }
            if (m_Mode == InteractionMode.ScalingExisting)
            {
                var factor = Mathf.Exp(steps * k_CrownScaleStep);
                m_StartScale = OggettiLayout.ClampScale(m_StartScale * factor);
                m_ActiveTarget.SetScale(m_ActiveTarget.CurrentScale * factor);
                return true;
            }
            return false;
        }

        static Texture2D LoadTextureFromFile(string path)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes == null || bytes.Length == 0)
                    return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                    return tex;
                Destroy(tex);
            }
            catch (Exception) { }
            return null;
        }

        static void TryWriteBytesAtomic(string destPath, byte[] bytes)
        {
            try
            {
                var dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var tmp = destPath + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(tmp, destPath);
            }
            catch (Exception) { }
        }
    }
}
