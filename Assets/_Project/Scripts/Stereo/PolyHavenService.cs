using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CardboardXR.Stereo
{
    // Runtime service executing all Poly Haven HTTP requests with the mandatory User-Agent header,
    // caching the HDRI catalog and thumbnails on disk, and downloading/converting 360° panoramas
    // safely within mobile memory budgets.
    public class PolyHavenService : MonoBehaviour
    {
        const int k_MaxConcurrentThumbDownloads = 4;
        const int k_MaxInMemoryThumbs = 96;
        const int k_MaxPanoramaDim = 4096;
        static readonly TimeSpan k_CatalogRefreshInterval = TimeSpan.FromHours(24);

        static PolyHavenService s_Instance;

        readonly List<PolyHavenAssetEntry> m_Catalog = new();
        bool m_CatalogLoaded;
        bool m_CatalogLoading;
        readonly List<Action<IReadOnlyList<PolyHavenAssetEntry>, string>> m_CatalogWaiters = new();

        readonly Dictionary<string, Texture2D> m_ThumbCache = new(StringComparer.Ordinal);
        readonly Queue<string> m_ThumbOrder = new();
        readonly Dictionary<string, List<Action<string, Texture2D>>> m_ThumbWaiters = new(StringComparer.Ordinal);
        readonly Queue<string> m_ThumbQueue = new();
        int m_ActiveThumbDownloads;

        readonly Dictionary<string, float> m_ActiveDownloads = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<(Action<string, float> progress, Action<string, bool, string> done)>> m_DownloadWaiters =
            new(StringComparer.Ordinal);

        public static event Action<string, float> DownloadProgressChanged;
        public static event Action<string, bool, string> DownloadFinished;

        public static PolyHavenService Instance
        {
            get
            {
                if (s_Instance != null)
                    return s_Instance;
                var existing = FindAnyObjectByType<PolyHavenService>();
                if (existing != null)
                {
                    s_Instance = existing;
                    return s_Instance;
                }
                var host = new GameObject("PolyHavenService");
                if (Application.isPlaying)
                    DontDestroyOnLoad(host);
                s_Instance = host.AddComponent<PolyHavenService>();
                return s_Instance;
            }
        }

        public bool IsCatalogLoaded => m_CatalogLoaded;
        public IReadOnlyList<PolyHavenAssetEntry> CachedCatalog => m_Catalog;

        public static UnityWebRequest CreateGetRequest(string url, int timeoutSeconds = 25)
        {
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("User-Agent", PolyHavenClient.UserAgent);
            req.timeout = Mathf.Max(5, timeoutSeconds);
            return req;
        }

        public bool TryGetDownloadProgress(string assetId, out float progress)
        {
            if (!string.IsNullOrEmpty(assetId) && m_ActiveDownloads.TryGetValue(assetId, out progress))
                return true;
            progress = 0f;
            return false;
        }

        public bool IsDownloading(string assetId) =>
            !string.IsNullOrEmpty(assetId) && m_ActiveDownloads.ContainsKey(assetId);

        void Awake()
        {
            if (s_Instance == null)
                s_Instance = this;
            else if (s_Instance != this)
                Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            foreach (var tex in m_ThumbCache.Values)
            {
                if (tex != null)
                    Destroy(tex);
            }
            m_ThumbCache.Clear();
            m_ThumbOrder.Clear();
        }

        public void FetchCatalog(Action<IReadOnlyList<PolyHavenAssetEntry>, string> onDone, bool forceRefresh = false)
        {
            if (m_CatalogLoaded && !forceRefresh)
            {
                onDone?.Invoke(m_Catalog, null);
                return;
            }
            if (onDone != null)
                m_CatalogWaiters.Add(onDone);
            if (m_CatalogLoading)
                return;
            m_CatalogLoading = true;
            StartCoroutine(FetchCatalogCoroutine(forceRefresh));
        }

        IEnumerator FetchCatalogCoroutine(bool forceRefresh)
        {
            var cachePath = EnvironmentLibraryStore.GetCatalogCachePath();
            var usedWarmCache = false;

            if (!forceRefresh && File.Exists(cachePath))
            {
                string cachedJson = null;
                var isFresh = false;
                try
                {
                    var info = new FileInfo(cachePath);
                    isFresh = DateTime.UtcNow - info.LastWriteTimeUtc < k_CatalogRefreshInterval;
                    cachedJson = File.ReadAllText(cachePath);
                }
                catch (Exception) { }

                if (!string.IsNullOrEmpty(cachedJson))
                {
                    var parsed = PolyHavenClient.ParseAssetsCatalog(cachedJson);
                    if (parsed.Count > 0)
                    {
                        m_Catalog.Clear();
                        m_Catalog.AddRange(parsed);
                        m_CatalogLoaded = true;
                        usedWarmCache = true;
                        if (isFresh)
                        {
                            m_CatalogLoading = false;
                            FlushCatalogWaiters(null);
                            yield break;
                        }
                        FlushCatalogWaiters(null);
                    }
                }
            }

            using var req = CreateGetRequest(PolyHavenClient.AssetsEndpoint, timeoutSeconds: 30);
            yield return req.SendWebRequest();

            m_CatalogLoading = false;
            if (req.result == UnityWebRequest.Result.Success)
            {
                var json = req.downloadHandler.text;
                var parsed = PolyHavenClient.ParseAssetsCatalog(json);
                if (parsed.Count > 0)
                {
                    m_Catalog.Clear();
                    m_Catalog.AddRange(parsed);
                    m_CatalogLoaded = true;
                    TryWriteTextSafe(cachePath, json);
                    FlushCatalogWaiters(null);
                    yield break;
                }
                if (!usedWarmCache)
                    FlushCatalogWaiters("Risposta catalogo Poly Haven non valida");
                yield break;
            }

            if (!usedWarmCache)
                FlushCatalogWaiters(req.error ?? "Errore di rete Poly Haven");
        }

        void FlushCatalogWaiters(string error)
        {
            if (m_CatalogWaiters.Count == 0)
                return;
            var snapshot = m_CatalogWaiters.ToArray();
            m_CatalogWaiters.Clear();
            for (var i = 0; i < snapshot.Length; i++)
                snapshot[i]?.Invoke(m_Catalog, error);
        }

        public void RequestThumbnail(string assetId, Action<string, Texture2D> onLoaded)
        {
            if (string.IsNullOrWhiteSpace(assetId) || assetId == EnvironmentLibraryStore.ProceduralCinemaId)
                return;

            if (m_ThumbCache.TryGetValue(assetId, out var existing) && existing != null)
            {
                onLoaded?.Invoke(assetId, existing);
                return;
            }

            if (m_ThumbWaiters.TryGetValue(assetId, out var list))
            {
                if (onLoaded != null)
                    list.Add(onLoaded);
                return;
            }

            var waiters = new List<Action<string, Texture2D>>();
            if (onLoaded != null)
                waiters.Add(onLoaded);
            m_ThumbWaiters[assetId] = waiters;
            m_ThumbQueue.Enqueue(assetId);
            PumpThumbnailQueue();
        }

        void PumpThumbnailQueue()
        {
            while (m_ActiveThumbDownloads < k_MaxConcurrentThumbDownloads && m_ThumbQueue.Count > 0)
            {
                var assetId = m_ThumbQueue.Dequeue();
                m_ActiveThumbDownloads++;
                StartCoroutine(LoadThumbnailCoroutine(assetId));
            }
        }

        IEnumerator LoadThumbnailCoroutine(string assetId)
        {
            Texture2D loadedTex = null;
            var cachePath = EnvironmentLibraryStore.GetThumbnailCachePath(assetId);

            if (File.Exists(cachePath))
            {
                byte[] diskBytes = null;
                try { diskBytes = File.ReadAllBytes(cachePath); }
                catch (Exception) { }
                loadedTex = DecodeThumbTexture(diskBytes);
            }

            if (loadedTex == null)
            {
                var url = PolyHavenClient.BuildThumbnailJpgUrl(assetId);
                using var req = CreateGetRequest(url, timeoutSeconds: 20);
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    var bytes = req.downloadHandler.data;
                    loadedTex = DecodeThumbTexture(bytes);
                    if (loadedTex != null)
                        TryWriteBytesAtomic(cachePath, bytes);
                }
            }

            if (loadedTex != null)
                CacheThumbnailInMemory(assetId, loadedTex);

            if (m_ThumbWaiters.TryGetValue(assetId, out var waiters))
            {
                m_ThumbWaiters.Remove(assetId);
                for (var i = 0; i < waiters.Count; i++)
                    waiters[i]?.Invoke(assetId, loadedTex);
            }

            m_ActiveThumbDownloads = Mathf.Max(0, m_ActiveThumbDownloads - 1);
            PumpThumbnailQueue();
        }

        static Texture2D DecodeThumbTexture(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 16)
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                return tex;
            Destroy(tex);
            return null;
        }

        void CacheThumbnailInMemory(string assetId, Texture2D tex)
        {
            if (m_ThumbCache.ContainsKey(assetId))
                return;
            m_ThumbCache[assetId] = tex;
            m_ThumbOrder.Enqueue(assetId);
            while (m_ThumbOrder.Count > k_MaxInMemoryThumbs)
            {
                var oldest = m_ThumbOrder.Dequeue();
                if (m_ThumbCache.TryGetValue(oldest, out var oldTex))
                {
                    m_ThumbCache.Remove(oldest);
                    if (oldTex != null)
                        Destroy(oldTex);
                }
            }
        }

        public void DownloadPanorama(string assetId, Action<string, float> onProgress,
            Action<string, bool, string> onComplete)
        {
            if (string.IsNullOrWhiteSpace(assetId) || assetId == EnvironmentLibraryStore.ProceduralCinemaId)
            {
                onComplete?.Invoke(assetId, false, "ID ambiente non valido");
                return;
            }

            if (EnvironmentLibraryStore.IsPanoramaCached(assetId))
            {
                onProgress?.Invoke(assetId, 1f);
                onComplete?.Invoke(assetId, true, null);
                return;
            }

            if (!m_DownloadWaiters.TryGetValue(assetId, out var waiters))
            {
                waiters = new List<(Action<string, float>, Action<string, bool, string>)>();
                m_DownloadWaiters[assetId] = waiters;
            }
            waiters.Add((onProgress, onComplete));

            if (m_ActiveDownloads.ContainsKey(assetId))
            {
                onProgress?.Invoke(assetId, m_ActiveDownloads[assetId]);
                return;
            }

            m_ActiveDownloads[assetId] = 0.02f;
            StartCoroutine(DownloadPanoramaCoroutine(assetId));
        }

        IEnumerator DownloadPanoramaCoroutine(string assetId)
        {
            ReportDownloadProgress(assetId, 0.04f);

            var filesUrl = PolyHavenClient.BuildFilesUrl(assetId);
            PolyHavenDownloadChoice choice = default;
            using (var filesReq = CreateGetRequest(filesUrl, timeoutSeconds: 25))
            {
                yield return filesReq.SendWebRequest();
                if (filesReq.result != UnityWebRequest.Result.Success)
                {
                    CompleteDownload(assetId, false, filesReq.error ?? "Errore lettura file Poly Haven");
                    yield break;
                }

                if (!PolyHavenClient.TrySelectBestDownload(filesReq.downloadHandler.text, out choice))
                {
                    CompleteDownload(assetId, false, "Nessun formato HDRI compatibile disponibile");
                    yield break;
                }
            }

            ReportDownloadProgress(assetId, 0.08f);

            byte[] rawBytes;
            using (var dlReq = CreateGetRequest(choice.Url, timeoutSeconds: 120))
            {
                var op = dlReq.SendWebRequest();
                while (!op.isDone)
                {
                    var p = Mathf.Lerp(0.08f, 0.84f, Mathf.Clamp01(dlReq.downloadProgress));
                    ReportDownloadProgress(assetId, p);
                    yield return null;
                }

                if (dlReq.result != UnityWebRequest.Result.Success)
                {
                    CompleteDownload(assetId, false, dlReq.error ?? "Download ambiente fallito");
                    yield break;
                }
                rawBytes = dlReq.downloadHandler.data;
            }

            if (rawBytes == null || rawBytes.Length < 256)
            {
                CompleteDownload(assetId, false, "File ambiente vuoto o corrotto");
                yield break;
            }

            ReportDownloadProgress(assetId, 0.88f);
            var targetPath = EnvironmentLibraryStore.GetPanoramaCachePath(assetId);
            byte[] finalJpgBytes = null;
            string decodeError = null;

            if (choice.IsHdr)
            {
                int w = 0, h = 0;
                byte[] rgb24 = null;
                var decodeTask = Task.Run(() =>
                    RadianceHdrDecoder.TryDecodeToRgb24(rawBytes, out w, out h, out rgb24, out decodeError));
                while (!decodeTask.IsCompleted)
                    yield return null;

                if (!decodeTask.Result || rgb24 == null)
                {
                    CompleteDownload(assetId, false, decodeError ?? "Decodifica HDR fallita");
                    yield break;
                }

                ReportDownloadProgress(assetId, 0.94f);
                var tempTex = new Texture2D(w, h, TextureFormat.RGB24, mipChain: false);
                tempTex.LoadRawTextureData(rgb24);
                tempTex.Apply(false, false);
                finalJpgBytes = ImageConversion.EncodeToJPG(tempTex, 90);
                Destroy(tempTex);
            }
            else
            {
                var hasDims = PolyHavenClient.TryReadJpegDimensions(rawBytes, out var jw, out _);
                var needsDownsample = hasDims && jw > k_MaxPanoramaDim;
                if (!needsDownsample)
                {
                    finalJpgBytes = rawBytes;
                }
                else
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    var androidTask = Task.Run(() =>
                        TryDownsampleJpegFileAndroid(rawBytes, jw, k_MaxPanoramaDim, targetPath, out decodeError));
                    while (!androidTask.IsCompleted)
                        yield return null;

                    if (!androidTask.IsFaulted && androidTask.Result)
                    {
                        ReportDownloadProgress(assetId, 1f);
                        CompleteDownload(assetId, true, null);
                        yield break;
                    }

                    var androidErr = androidTask.IsFaulted
                        ? androidTask.Exception?.GetBaseException().Message
                        : decodeError;
                    Debug.LogWarning($"[PolyHaven] Android BitmapFactory downsample failed for {assetId} ({androidErr}), falling back to GPU");
                    finalJpgBytes = DownsampleJpegWithGpu(rawBytes, k_MaxPanoramaDim);
                    if (finalJpgBytes == null && jw <= SystemInfo.maxTextureSize)
                        finalJpgBytes = rawBytes;
                    if (finalJpgBytes == null)
                    {
                        CompleteDownload(assetId, false, androidErr ?? "Ridimensionamento immagine fallito");
                        yield break;
                    }
#else
                    finalJpgBytes = DownsampleJpegWithGpu(rawBytes, k_MaxPanoramaDim);
                    if (finalJpgBytes == null)
                    {
                        CompleteDownload(assetId, false, "Ridimensionamento immagine fallito");
                        yield break;
                    }
#endif
                }
            }

            ReportDownloadProgress(assetId, 0.97f);
            var writeOk = TryWriteBytesAtomic(targetPath, finalJpgBytes);
            if (!writeOk)
            {
                CompleteDownload(assetId, false, "Impossibile salvare l'ambiente nella cache");
                yield break;
            }

            ReportDownloadProgress(assetId, 1f);
            CompleteDownload(assetId, true, null);
        }

        void ReportDownloadProgress(string assetId, float progress)
        {
            m_ActiveDownloads[assetId] = progress;
            if (m_DownloadWaiters.TryGetValue(assetId, out var waiters))
            {
                for (var i = 0; i < waiters.Count; i++)
                    waiters[i].progress?.Invoke(assetId, progress);
            }
            DownloadProgressChanged?.Invoke(assetId, progress);
        }

        void CompleteDownload(string assetId, bool success, string error)
        {
            if (!success)
                Debug.LogWarning($"[PolyHaven] Download failed for {assetId}: {error}");
            m_ActiveDownloads.Remove(assetId);
            if (m_DownloadWaiters.TryGetValue(assetId, out var waiters))
            {
                m_DownloadWaiters.Remove(assetId);
                for (var i = 0; i < waiters.Count; i++)
                    waiters[i].done?.Invoke(assetId, success, error);
            }
            DownloadFinished?.Invoke(assetId, success, error);
        }

        static byte[] DownsampleJpegWithGpu(byte[] rawBytes, int maxWidth)
        {
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            if (!ImageConversion.LoadImage(src, rawBytes, markNonReadable: true))
            {
                Destroy(src);
                return null;
            }

            var targetW = Mathf.Min(maxWidth, src.width);
            var targetH = Mathf.Max(1, Mathf.RoundToInt(src.height * (targetW / (float)src.width)));
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var dst = new Texture2D(targetW, targetH, TextureFormat.RGB24, mipChain: false);
            dst.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            dst.Apply(false, false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            Destroy(src);

            var jpg = ImageConversion.EncodeToJPG(dst, 90);
            Destroy(dst);
            return jpg;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool TryDownsampleJpegFileAndroid(byte[] rawBytes, int srcWidth, int maxWidth, string targetPath, out string error)
        {
            error = null;
            var rawTmpPath = targetPath + ".raw";
            var jpgTmpPath = targetPath + ".tmp";
            var attached = false;
            try
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(rawTmpPath, rawBytes);

                var sampleSize = 1;
                while (srcWidth / (sampleSize * 2) >= maxWidth)
                    sampleSize *= 2;
                if (srcWidth / sampleSize > maxWidth)
                    sampleSize *= 2;
                sampleSize = Math.Max(1, sampleSize);

                attached = AndroidJNI.AttachCurrentThread() == 0;
                using var decodeOpts = new AndroidJavaObject("android.graphics.BitmapFactory$Options");
                decodeOpts.Set("inSampleSize", sampleSize);
                using var factory = new AndroidJavaClass("android.graphics.BitmapFactory");
                using var bmp = factory.CallStatic<AndroidJavaObject>("decodeFile", rawTmpPath, decodeOpts);
                if (bmp == null)
                {
                    error = "BitmapFactory.decodeFile returned null";
                    return false;
                }

                try
                {
                    using var fos = new AndroidJavaObject("java.io.FileOutputStream", jpgTmpPath);
                    try
                    {
                        using var fmtClass = new AndroidJavaClass("android.graphics.Bitmap$CompressFormat");
                        using var jpegFmt = fmtClass.GetStatic<AndroidJavaObject>("JPEG");
                        var ok = bmp.Call<bool>("compress", jpegFmt, 90, fos);
                        fos.Call("flush");
                        if (!ok)
                        {
                            error = "Bitmap.compress returned false";
                            return false;
                        }
                    }
                    finally
                    {
                        fos.Call("close");
                    }
                }
                finally
                {
                    bmp.Call("recycle");
                }

                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                File.Move(jpgTmpPath, targetPath);
                return File.Exists(targetPath) && new FileInfo(targetPath).Length > 1024;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                if (attached)
                    AndroidJNI.DetachCurrentThread();
                try
                {
                    if (File.Exists(rawTmpPath))
                        File.Delete(rawTmpPath);
                    if (File.Exists(jpgTmpPath))
                        File.Delete(jpgTmpPath);
                }
                catch (Exception) { }
            }
        }
#endif

        static bool TryWriteTextSafe(string path, string text)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool TryWriteBytesAtomic(string path, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return false;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
