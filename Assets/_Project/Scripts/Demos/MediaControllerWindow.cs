using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos
{
    // Spatial floating media controller panel: uses the active song's album cover art as its
    // borderless rounded glass background with interactive Indietro, Pausa/Play, Avanti, and Seek bar.
    public class MediaControllerWindow : MonoBehaviour
    {
        const float k_Width = 500f;
        const float k_Height = 310f;
        const float k_Distance = 1.42f;
        const float k_ArtAlphaWithCover = 0.96f;
        static readonly Color s_NoArtColor = new(0.11f, 0.13f, 0.22f, 0.92f);

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] DemoManager m_Demos;

        RectTransform m_Panel;
        RawImage m_ArtImage;
        TMP_Text m_AppText;
        TMP_Text m_TitleText;
        TMP_Text m_ArtistText;
        M3Slider m_SeekSlider;
        TMP_Text m_ElapsedText;
        TMP_Text m_DurationText;
        M3Button m_PlayPauseBtn;
        M3Button m_OpenAppBtn;

        bool m_Shown;
        bool m_Subscribed;
        long m_DisplayPosMs;
        long m_LastMediaPosMs;
        float m_PosAnchorTime;
        float m_OptimisticUntil;

        void OnEnable() => SettingsPanelHub.MediaPanelRequested += OpenOrBringToFront;

        void OnDisable() => SettingsPanelHub.MediaPanelRequested -= OpenOrBringToFront;

        void Start() => EnsureBridgeSubscribed();

        void OnDestroy()
        {
            if (m_Subscribed && SystemBridge.Instance != null)
                SystemBridge.Instance.MediaChanged -= OnMediaChanged;
            m_Subscribed = false;
        }

        public void Toggle()
        {
            if (m_Shown && m_Panel != null && M3Fade.IsShown(m_Panel.gameObject))
                Hide();
            else
                OpenOrBringToFront();
        }

        public void OpenOrBringToFront()
        {
            AppLibrary.DismissGlobal();
            EnsureBuilt();
            EnsureBridgeSubscribed();

            m_Shown = true;
            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), true);

            var head = m_Fusion != null ? m_Fusion.transform : null;
            WorldUi.BringToFront(m_Panel, head, k_Distance, 2f, 0f);
            FloatingWindowHandle.Configure(m_Panel, Hide, restoreOnRecenter: true);

            OnMediaChanged();
        }

        public void Hide()
        {
            m_Shown = false;
            SystemBridge.Instance?.SetFastPoll(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this), false);
            if (m_Panel != null)
                M3Fade.Hide(m_Panel.gameObject);
        }

        void EnsureBridgeSubscribed()
        {
            if (m_Subscribed || SystemBridge.Instance == null)
                return;
            SystemBridge.Instance.MediaChanged += OnMediaChanged;
            m_Subscribed = true;
        }

        void EnsureBuilt()
        {
            if (m_Panel != null)
                return;

            m_Panel = M3.Canvas("Media Controller", null, new Vector2(k_Width, k_Height), k_Distance,
                Color.clear, M3Theme.k_ShapePanel, style: VisionSurfaceStyle.Pure);
            m_Panel.gameObject.AddComponent<GazeCanvas>();

            m_ArtImage = M3.RoundedScreen("AlbumArt", m_Panel, Vector2.zero, new Vector2(k_Width, k_Height),
                M3Theme.k_ShapePanel, raycastTarget: true, rimStrength: 0.7f, sheenStrength: 0.22f);
            m_ArtImage.color = s_NoArtColor;

            // Semi-transparent frosted dark scrim so text & transport pills remain readable on bright cover art.
            M3.Surface("Scrim", m_Panel, Vector2.zero, new Vector2(k_Width, k_Height),
                new Color(0.03f, 0.04f, 0.08f, 0.56f), M3Theme.k_ShapePanel,
                raycastTarget: false, style: VisionSurfaceStyle.Recessed, rimStrength: 0f, sheenStrength: 0f);

            M3.Icon("MusicIcon", m_Panel, "music_note", 22f, new Vector2(-212f, 122f), M3Theme.GlassAccent);
            m_AppText = M3.Text("AppName", m_Panel, "Gestore Multimediale", M3Theme.Type.LabelLarge,
                new Vector2(-14f, 122f), new Vector2(346f, 26f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_AppText.textWrappingMode = TextWrappingModes.NoWrap;
            m_OpenAppBtn = M3.IconButton("OpenApp", m_Panel, "open_in_new", M3Button.Variant.Tonal,
                new Vector2(214f, 120f), OnOpenAppClicked, 40f);
            m_OpenAppBtn.gameObject.SetActive(false);

            m_TitleText = M3.Text("TrackTitle", m_Panel, "Nessun contenuto in riproduzione", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 66f), new Vector2(436f, 40f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            m_TitleText.textWrappingMode = TextWrappingModes.NoWrap;

            m_ArtistText = M3.Text("ArtistAlbum", m_Panel, "Avvia un brano o un video sul dispositivo", M3Theme.Type.BodyLarge,
                new Vector2(0f, 26f), new Vector2(436f, 28f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            m_ArtistText.textWrappingMode = TextWrappingModes.NoWrap;

            m_SeekSlider = M3.Slider("Seek", m_Panel, new Vector2(0f, -24f), 420f);
            m_SeekSlider.Clicked += OnSeekClicked;

            m_ElapsedText = M3.Text("Elapsed", m_Panel, "0:00", M3Theme.Type.LabelLarge,
                new Vector2(-168f, -48f), new Vector2(96f, 24f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_DurationText = M3.Text("Duration", m_Panel, "0:00", M3Theme.Type.LabelLarge,
                new Vector2(168f, -48f), new Vector2(96f, 24f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Right);

            m_PlayPauseBtn = M3.IconButton("PlayPause", m_Panel, "play_arrow", M3Button.Variant.Filled,
                new Vector2(0f, -104f), OnPlayPauseClicked, 68f);
            M3.IconButton("Previous", m_Panel, "skip_previous", M3Button.Variant.Tonal,
                new Vector2(-106f, -104f), OnPrevClicked, 54f);
            M3.IconButton("Next", m_Panel, "skip_next", M3Button.Variant.Tonal,
                new Vector2(106f, -104f), OnNextClicked, 54f);

            m_Panel.gameObject.SetActive(false);
        }

        void OnMediaChanged()
        {
            if (m_Panel == null)
                return;

            var bridge = SystemBridge.Instance;
            var media = bridge != null ? bridge.CurrentMedia : default;
            var art = bridge != null ? bridge.MediaArtTexture : null;

            if (art != null)
            {
                if (m_ArtImage.texture != art)
                    m_ArtImage.texture = art;
                m_ArtImage.color = new Color(1f, 1f, 1f, k_ArtAlphaWithCover);
                m_ArtImage.uvRect = ComputeCenterCropUv(art.width, art.height, k_Width / k_Height);
            }
            else
            {
                m_ArtImage.texture = null;
                m_ArtImage.color = s_NoArtColor;
                m_ArtImage.uvRect = new Rect(0f, 0f, 1f, 1f);
            }

            if (media.HasSession)
            {
                m_AppText.text = string.IsNullOrEmpty(media.PackageName) ? "In riproduzione" : media.PackageName;
                m_TitleText.text = string.IsNullOrEmpty(media.Title) ? "Senza titolo" : media.Title;
                m_ArtistText.text = string.IsNullOrEmpty(media.Artist)
                    ? (media.Album ?? "")
                    : (string.IsNullOrEmpty(media.Album) ? media.Artist : $"{media.Artist} • {media.Album}");
            }
            else
            {
                m_AppText.text = "Gestore Multimediale";
                m_TitleText.text = "Nessun contenuto in riproduzione";
                m_ArtistText.text = "Avvia musica o video sul telefono";
            }

            m_PlayPauseBtn.Icon = media.HasSession && media.IsPlaying ? "pause" : "play_arrow";
            if (m_OpenAppBtn != null)
                m_OpenAppBtn.gameObject.SetActive(media.HasSession && !string.IsNullOrWhiteSpace(media.PackageName));

            if (Time.unscaledTime >= m_OptimisticUntil)
            {
                m_LastMediaPosMs = media.PositionMs;
                m_PosAnchorTime = Time.unscaledTime;
                m_DisplayPosMs = media.PositionMs;
            }
            ApplyProgress(media);
        }

        void Update()
        {
            if (!m_Shown || m_Panel == null)
                return;
            if (SettingsSummoner.SuppressForFullscreen)
            {
                Hide();
                return;
            }
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            var media = bridge.CurrentMedia;
            if (media.HasSession && media.IsPlaying && Time.unscaledTime >= m_OptimisticUntil)
            {
                var elapsedMs = (long)((Time.unscaledTime - m_PosAnchorTime) * 1000f);
                m_DisplayPosMs = media.DurationMs > 0
                    ? System.Math.Min(m_LastMediaPosMs + elapsedMs, media.DurationMs)
                    : m_LastMediaPosMs + elapsedMs;
                ApplyProgress(media);
            }
        }

        void ApplyProgress(SystemBridge.MediaInfo media)
        {
            if (media.DurationMs > 0)
            {
                m_SeekSlider.Value = Mathf.Clamp01((float)m_DisplayPosMs / media.DurationMs);
                m_ElapsedText.text = SystemBridge.FormatDuration(m_DisplayPosMs);
                m_DurationText.text = SystemBridge.FormatDuration(media.DurationMs);
            }
            else
            {
                m_SeekSlider.Value = 0f;
                m_ElapsedText.text = SystemBridge.FormatDuration(m_DisplayPosMs);
                m_DurationText.text = "0:00";
            }
        }

        void OnSeekClicked(float fraction)
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            var media = bridge.CurrentMedia;
            if (!media.HasSession || media.DurationMs <= 0)
                return;
            var targetMs = (long)(Mathf.Clamp01(fraction) * media.DurationMs);
            m_DisplayPosMs = targetMs;
            m_LastMediaPosMs = targetMs;
            m_PosAnchorTime = Time.unscaledTime;
            m_OptimisticUntil = Time.unscaledTime + 0.6f;
            ApplyProgress(media);
            bridge.MediaSeekTo(targetMs);
        }

        void OnPlayPauseClicked() => SystemBridge.Instance?.MediaTogglePlayPause();

        void OnPrevClicked() => SystemBridge.Instance?.MediaPrevious();

        void OnNextClicked() => SystemBridge.Instance?.MediaNext();

        void OnOpenAppClicked()
        {
            var bridge = SystemBridge.Instance;
            if (bridge == null)
                return;
            var media = bridge.CurrentMedia;
            if (!media.HasSession || string.IsNullOrWhiteSpace(media.PackageName))
                return;
            var demos = m_Demos != null ? m_Demos : GetComponent<DemoManager>();
            if (demos == null)
                demos = FindAnyObjectByType<DemoManager>();
            Hide();
            PhoneAppShortcutStore.LaunchPackage(demos, media.PackageName, media.PackageName);
        }

        static Rect ComputeCenterCropUv(int texW, int texH, float cardAspect)
        {
            if (texW <= 0 || texH <= 0 || cardAspect <= 0f)
                return new Rect(0f, 0f, 1f, 1f);
            var texAspect = (float)texW / texH;
            if (texAspect > cardAspect)
            {
                var w = cardAspect / texAspect;
                return new Rect((1f - w) * 0.5f, 0f, w, 1f);
            }
            var h = texAspect / cardAspect;
            return new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }
    }
}
