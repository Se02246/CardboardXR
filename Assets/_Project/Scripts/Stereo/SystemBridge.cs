using System;
using System.Collections.Generic;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Core-scene runtime bridge connecting Unity to CardboardSystemBridge.java for:
    //   - Shared Shizuku status & permission triggers
    //   - Live active MediaSession metadata, album cover art, seek, and transport controls
    //   - Live Android notifications (head-up incoming trigger, bounded history, package icons)
    public class SystemBridge : MonoBehaviour
    {
        public const int StatusNotInstalled = 0;
        public const int StatusNotRunning = 1;
        public const int StatusPermissionRequired = 2;
        public const int StatusConnecting = 3;
        public const int StatusReady = 4;
        public const int StatusError = 5;

        const float k_IdlePollSeconds = 0.50f;
        const float k_FastPollSeconds = 0.25f;

        public struct NotificationItem : IEquatable<NotificationItem>
        {
            public int Id;
            public string Key;
            public string PackageName;
            public string AppName;
            public string Title;
            public string Text;
            public long PostTimeMs;

            public bool Equals(NotificationItem other) =>
                Id == other.Id && Key == other.Key && PackageName == other.PackageName && Title == other.Title;
        }

        public struct MediaInfo : IEquatable<MediaInfo>
        {
            public bool HasSession;
            public bool IsPlaying;
            public string PackageName;
            public string Title;
            public string Artist;
            public string Album;
            public long DurationMs;
            public long PositionMs;
            public int ArtVersion;

            public bool Equals(MediaInfo other) =>
                HasSession == other.HasSession &&
                IsPlaying == other.IsPlaying &&
                PackageName == other.PackageName &&
                Title == other.Title &&
                Artist == other.Artist &&
                Album == other.Album &&
                DurationMs == other.DurationMs &&
                PositionMs == other.PositionMs &&
                ArtVersion == other.ArtVersion;
        }

        public static SystemBridge Instance { get; private set; }

        readonly List<NotificationItem> m_Notifications = new();
        readonly Dictionary<string, Texture2D> m_IconCache = new();
        readonly HashSet<int> m_FastPollRequests = new();

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Bridge;
#endif

        int m_ShizukuStatusCode = StatusNotRunning;
        string m_ShizukuStatusMessage = "";
        int m_KnownShizukuVersion = -1;
        bool m_NotificationAccessEnabled;
        int m_KnownHistoryVersion = -1;
        int m_KnownIncomingId;
        int m_UnreadCount;
        MediaInfo m_Media;
        int m_KnownArtVersion = -1;
        Texture2D m_MediaArtTexture;
        float m_NextPollAt;
        bool m_Paused;

        public int ShizukuStatusCode => m_ShizukuStatusCode;
        public string ShizukuStatusMessage => m_ShizukuStatusMessage;
        public bool IsShizukuReady => m_ShizukuStatusCode == StatusReady;
        public bool NotificationAccessEnabled => m_NotificationAccessEnabled;
        public IReadOnlyList<NotificationItem> Notifications => m_Notifications;
        public int UnreadCount => m_UnreadCount;
        public MediaInfo CurrentMedia => m_Media;
        public Texture2D MediaArtTexture => m_MediaArtTexture;

        public event Action<NotificationItem> IncomingNotification;
        public event Action NotificationsChanged;
        public event Action MediaChanged;
        public event Action ShizukuStatusChanged;

        void Awake()
        {
            Instance = this;
            InitNativeBridge();
        }

        void Start() => PollAll(emitIncoming: false);

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            SettingsPanelHub.NotifyUnreadNotifications(0);
            SettingsPanelHub.NotifyMediaPlaying(false);
            if (m_MediaArtTexture != null)
            {
                DestroyObjectSafe(m_MediaArtTexture);
                m_MediaArtTexture = null;
            }
            foreach (var tex in m_IconCache.Values)
            {
                if (tex != null)
                    DestroyObjectSafe(tex);
            }
            m_IconCache.Clear();
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge != null)
            {
                try
                {
                    m_Bridge.Call("dispose");
                    m_Bridge.Dispose();
                }
                catch (Exception) { }
                m_Bridge = null;
            }
#endif
        }

        static void DestroyObjectSafe(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        void OnApplicationPause(bool paused)
        {
            m_Paused = paused;
#if UNITY_ANDROID && !UNITY_EDITOR
            try { m_Bridge?.Call("setSuppressHeadsUp", !paused); }
            catch (Exception) { }
#endif
            if (!paused)
            {
                RefreshShizuku();
                m_NextPollAt = 0f;
            }
        }

        public void SetFastPoll(int requesterId, bool active)
        {
            if (active)
                m_FastPollRequests.Add(requesterId);
            else
                m_FastPollRequests.Remove(requesterId);
        }

        void Update()
        {
            if (m_Paused)
                return;
            var now = Time.unscaledTime;
            if (now < m_NextPollAt)
                return;
            m_NextPollAt = now + (m_FastPollRequests.Count > 0 ? k_FastPollSeconds : k_IdlePollSeconds);
            PollAll(emitIncoming: true);
        }

        void InitNativeBridge()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                m_Bridge = new AndroidJavaObject("com.brenpoly.cardboardxr.system.CardboardSystemBridge", activity);
            }
            catch (Exception e)
            {
                m_ShizukuStatusCode = StatusError;
                m_ShizukuStatusMessage = $"Errore bridge sistema: {e.Message}";
                Debug.LogWarning($"[SystemBridge] Init failed: {e.Message}");
            }
#else
            m_ShizukuStatusCode = StatusReady;
            m_ShizukuStatusMessage = "Shizuku pronto (Editor)";
            m_NotificationAccessEnabled = true;
#endif
        }

        public void PollAll(bool emitIncoming)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge == null)
                return;
            try
            {
                var shizukuVer = m_Bridge.Call<int>("getShizukuStatusVersion");
                var access = m_Bridge.Call<bool>("isNotificationAccessEnabled");
                if (shizukuVer != m_KnownShizukuVersion || access != m_NotificationAccessEnabled)
                {
                    m_KnownShizukuVersion = shizukuVer;
                    m_ShizukuStatusCode = m_Bridge.Call<int>("getShizukuStatusCode");
                    m_ShizukuStatusMessage = m_Bridge.Call<string>("getShizukuStatusMessage") ?? "";
                    m_NotificationAccessEnabled = access;
                    ShizukuStatusChanged?.Invoke();
                }

                var histVer = m_Bridge.Call<int>("getNotificationHistoryVersion");
                var latestIncoming = m_Bridge.Call<int>("getLatestIncomingNotificationId");
                if (histVer != m_KnownHistoryVersion)
                {
                    m_KnownHistoryVersion = histVer;
                    var count = m_Bridge.Call<int>("syncNotifications");
                    m_Notifications.Clear();
                    for (var i = 0; i < count; i++)
                    {
                        m_Notifications.Add(new NotificationItem
                        {
                            Id = m_Bridge.Call<int>("getNotifId", i),
                            Key = m_Bridge.Call<string>("getNotifKey", i) ?? "",
                            PackageName = m_Bridge.Call<string>("getNotifPackage", i) ?? "",
                            AppName = m_Bridge.Call<string>("getNotifAppName", i) ?? "",
                            Title = m_Bridge.Call<string>("getNotifTitle", i) ?? "",
                            Text = m_Bridge.Call<string>("getNotifText", i) ?? "",
                            PostTimeMs = m_Bridge.Call<long>("getNotifPostTimeMs", i),
                        });
                    }
                    NotificationsChanged?.Invoke();
                }

                if (latestIncoming > m_KnownIncomingId)
                {
                    m_KnownIncomingId = latestIncoming;
                    if (emitIncoming)
                    {
                        m_UnreadCount++;
                        SettingsPanelHub.NotifyUnreadNotifications(m_UnreadCount);
                        for (var i = 0; i < m_Notifications.Count; i++)
                        {
                            if (m_Notifications[i].Id == latestIncoming)
                            {
                                IncomingNotification?.Invoke(m_Notifications[i]);
                                break;
                            }
                        }
                    }
                }

                m_Bridge.Call("refreshMediaState");
                var nextMedia = new MediaInfo
                {
                    HasSession = m_Bridge.Call<bool>("isMediaHasSession"),
                    IsPlaying = m_Bridge.Call<bool>("isMediaPlaying"),
                    PackageName = m_Bridge.Call<string>("getMediaPackage") ?? "",
                    Title = m_Bridge.Call<string>("getMediaTitle") ?? "",
                    Artist = m_Bridge.Call<string>("getMediaArtist") ?? "",
                    Album = m_Bridge.Call<string>("getMediaAlbum") ?? "",
                    DurationMs = m_Bridge.Call<long>("getMediaDurationMs"),
                    PositionMs = m_Bridge.Call<long>("getMediaPositionMs"),
                    ArtVersion = m_Bridge.Call<int>("getMediaArtVersion"),
                };
                if (nextMedia.ArtVersion != m_KnownArtVersion)
                {
                    m_KnownArtVersion = nextMedia.ArtVersion;
                    UpdateMediaArtTexture(m_Bridge.Call<byte[]>("getMediaArtPng"));
                }
                if (!nextMedia.Equals(m_Media))
                {
                    m_Media = nextMedia;
                    SettingsPanelHub.NotifyMediaPlaying(m_Media.HasSession && m_Media.IsPlaying);
                    MediaChanged?.Invoke();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SystemBridge] Poll failed: {e.Message}");
            }
#endif
        }

        void UpdateMediaArtTexture(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                if (m_MediaArtTexture != null)
                {
                    DestroyObjectSafe(m_MediaArtTexture);
                    m_MediaArtTexture = null;
                }
                return;
            }
            if (m_MediaArtTexture == null)
            {
                m_MediaArtTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "Media Album Art",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            if (!ImageConversion.LoadImage(m_MediaArtTexture, bytes, markNonReadable: true))
            {
                DestroyObjectSafe(m_MediaArtTexture);
                m_MediaArtTexture = null;
            }
        }

        public void RefreshShizuku()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("refreshShizuku");
            PollAll(emitIncoming: false);
#else
            ShizukuStatusChanged?.Invoke();
#endif
        }

        public void RequestShizukuPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("requestShizukuPermission");
            RefreshShizuku();
#endif
        }

        public void OpenShizukuApp()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("openShizukuApp");
#endif
        }

        public bool GrantNotificationAccessViaShizuku()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge == null)
                return false;
            var ok = m_Bridge.Call<bool>("grantNotificationAccessViaShizuku");
            PollAll(emitIncoming: false);
            return ok;
#else
            return true;
#endif
        }

        public void OpenNotificationAccessSettings()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("openNotificationAccessSettings");
#endif
        }

        public void ClearUnreadCount()
        {
            if (m_UnreadCount == 0)
                return;
            m_UnreadCount = 0;
            SettingsPanelHub.NotifyUnreadNotifications(0);
        }

        public void DismissNotification(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("dismissNotification", key);
            PollAll(emitIncoming: false);
#else
            for (var i = m_Notifications.Count - 1; i >= 0; i--)
            {
                if (m_Notifications[i].Key == key)
                    m_Notifications.RemoveAt(i);
            }
            NotificationsChanged?.Invoke();
#endif
        }

        public void ClearNotifications()
        {
            ClearUnreadCount();
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("clearNotifications");
            PollAll(emitIncoming: false);
#else
            m_Notifications.Clear();
            NotificationsChanged?.Invoke();
#endif
        }

        public Texture2D GetAppIcon(string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
                return null;
            if (m_IconCache.TryGetValue(packageName, out var existing))
                return existing;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Bridge == null)
                return null;
            try
            {
                var bytes = m_Bridge.Call<byte[]>("getAppIconPng", packageName);
                if (bytes != null && bytes.Length > 0)
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                    if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                    {
                        m_IconCache[packageName] = tex;
                        return tex;
                    }
                    DestroyObjectSafe(tex);
                }
            }
            catch (Exception) { }
#endif
            return null;
        }

        public void MediaTogglePlayPause()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("mediaTogglePlayPause");
            m_NextPollAt = 0f;
#else
            m_Media.IsPlaying = !m_Media.IsPlaying;
            SettingsPanelHub.NotifyMediaPlaying(m_Media.HasSession && m_Media.IsPlaying);
            MediaChanged?.Invoke();
#endif
        }

        public void MediaNext()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("mediaNext");
            m_NextPollAt = 0f;
#endif
        }

        public void MediaPrevious()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("mediaPrevious");
            m_NextPollAt = 0f;
#endif
        }

        public void MediaSeekTo(long positionMs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Bridge?.Call("mediaSeekTo", positionMs);
            m_NextPollAt = 0f;
#else
            m_Media.PositionMs = Math.Max(0L, positionMs);
            MediaChanged?.Invoke();
#endif
        }

        public void PushTestNotification(NotificationItem item, bool isIncoming = true)
        {
            m_Notifications.RemoveAll(n => n.Key == item.Key);
            m_Notifications.Insert(0, item);
            if (isIncoming)
            {
                m_UnreadCount++;
                SettingsPanelHub.NotifyUnreadNotifications(m_UnreadCount);
                IncomingNotification?.Invoke(item);
            }
            NotificationsChanged?.Invoke();
        }

        public void SetTestMediaState(MediaInfo info)
        {
            m_Media = info;
            SettingsPanelHub.NotifyMediaPlaying(info.HasSession && info.IsPlaying);
            MediaChanged?.Invoke();
        }

        public static string FormatDuration(long ms)
        {
            if (ms <= 0)
                return "0:00";
            var totalSec = ms / 1000L;
            var sec = totalSec % 60L;
            var min = (totalSec / 60L) % 60L;
            var hours = totalSec / 3600L;
            return hours > 0
                ? $"{hours}:{min:D2}:{sec:D2}"
                : $"{min}:{sec:D2}";
        }

        public static string FormatTimestamp(long postTimeMs)
        {
            if (postTimeMs <= 0)
                return DateTime.Now.ToString("HH:mm");
            var local = DateTimeOffset.FromUnixTimeMilliseconds(postTimeMs).ToLocalTime();
            return local.ToString("HH:mm");
        }
    }
}
