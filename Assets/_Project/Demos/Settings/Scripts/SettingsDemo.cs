using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;

namespace CardboardXR.Demos.Settings
{
    // Impostazioni: app dedicata alle impostazioni di CardboardXR. Permette di:
    //  - scegliere la fotocamera passthrough (Grandangolare 0.5x di default oppure Principale 6DoF);
    //  - attivare o disattivare il pinch tramite visione (fotocamera) e scorrimento continuo;
    //  - attivare o disattivare il controllo del cursore tramite giroscopio del Pixel Watch (disattivato di default);
    //  - invertire l'asse Y del Pixel Watch;
    //  - scegliere il tracciamento spaziale 6DoF vs 3DoF.
    // Logs [Settings].
    public class SettingsDemo : Demo
    {
        const float k_PanelDistance = 1.55f;

        CoreServices m_Core;
        RectTransform m_Panel;
        M3Button[] m_CameraSegments;
        TMP_Text m_CameraHint;
        M3Button[] m_MovingCameraSegments;
        TMP_Text m_MovingCameraHint;
        M3Button[] m_VisionPinchSegments;
        TMP_Text m_VisionPinchHint;
        M3Button[] m_WatchGyroSegments;
        TMP_Text m_WatchGyroHint;
        M3Button[] m_WatchWindowGyroSegments;
        TMP_Text m_WatchWindowGyroHint;
        M3Button[] m_WatchYSegments;
        M3Button[] m_TrackingSegments;
        TMP_Text m_ShizukuHint;
        M3Button m_ShizukuBtn;

        public override void OnDemoEnter(CoreServices core)
        {
            m_Core = core;
            BuildPanel();
            RefreshUi();
            Debug.Log("[Settings] App Impostazioni aperta");
        }

        public override void OnDemoRefocus(CoreServices core)
        {
            if (m_Panel != null && core != null && core.Head != null)
                WorldUi.BringToFront(m_Panel, core.Head, k_PanelDistance, 0f);
            RefreshUi();
        }

        void BuildPanel()
        {
            m_Panel = WorldUi.Panel("Settings App Panel", transform, new Vector2(620f, 1084f), k_PanelDistance);

            M3.Text("Title", m_Panel, "Impostazioni", M3Theme.Type.HeadlineSmall,
                new Vector2(0f, 500f), new Vector2(540f, 38f));

            // Card 1: Passthrough camera (recessed visionOS grouped glass section)
            var camCard = M3.Surface("Camera Card", m_Panel, new Vector2(0f, 412f), new Vector2(556f, 114f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Camera Label", camCard.rectTransform, "Fotocamera Passthrough", M3Theme.Type.TitleMedium,
                new Vector2(0f, 36f), new Vector2(512f, 26f), M3Theme.OnSurface);
            m_CameraSegments = M3.Segments("Camera Mode", camCard.rectTransform,
                new[] { "Principale (6DoF)", "Grandangolare (0.5×)" },
                new Vector2(0f, -1f), 252f, OnSelectCamera);
            m_CameraHint = M3.Text("Camera Hint", camCard.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -40f), new Vector2(512f, 34f), M3Theme.OnSurfaceVariant);

            // Card 2: Anti-nausea moving passthrough camera toggle
            var movingCamCard = M3.Surface("Moving Camera Card", m_Panel, new Vector2(0f, 288f), new Vector2(556f, 114f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Moving Camera Label", movingCamCard.rectTransform, "Camera Mobile (Anti-Nausea)", M3Theme.Type.TitleMedium,
                new Vector2(0f, 36f), new Vector2(512f, 26f), M3Theme.OnSurface);
            m_MovingCameraSegments = M3.Segments("Moving Camera Mode", movingCamCard.rectTransform,
                new[] { "Attiva (Mobile)", "Fissa su schermo" },
                new Vector2(0f, -1f), 252f, OnSelectMovingCamera);
            m_MovingCameraHint = M3.Text("Moving Camera Hint", movingCamCard.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -40f), new Vector2(512f, 34f), M3Theme.OnSurfaceVariant);

            // Card 3: Vision pinch toggle
            var pinchCard = M3.Surface("Pinch Card", m_Panel, new Vector2(0f, 164f), new Vector2(556f, 114f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Vision Pinch Label", pinchCard.rectTransform, "Pinch tramite Fotocamera", M3Theme.Type.TitleMedium,
                new Vector2(0f, 36f), new Vector2(512f, 26f), M3Theme.OnSurface);
            m_VisionPinchSegments = M3.Segments("Vision Pinch Mode", pinchCard.rectTransform,
                new[] { "Attivo", "Disattivato" },
                new Vector2(0f, -1f), 252f, OnSelectVisionPinch);
            m_VisionPinchHint = M3.Text("Vision Pinch Hint", pinchCard.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -40f), new Vector2(512f, 34f), M3Theme.OnSurfaceVariant);

            // Card 4: Pixel Watch gyro cursor control (disabled by default -> reticle centered)
            var watchGyroCard = M3.Surface("Watch Gyro Card", m_Panel, new Vector2(0f, 40f), new Vector2(556f, 114f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Watch Gyro Label", watchGyroCard.rectTransform, "Cursore Giroscopio Pixel Watch", M3Theme.Type.TitleMedium,
                new Vector2(0f, 36f), new Vector2(512f, 26f), M3Theme.OnSurface);
            m_WatchGyroSegments = M3.Segments("Watch Gyro Mode", watchGyroCard.rectTransform,
                new[] { "Attivo", "Disattivato" },
                new Vector2(0f, -1f), 252f, OnSelectWatchGyro);
            m_WatchGyroHint = M3.Text("Watch Gyro Hint", watchGyroCard.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -40f), new Vector2(512f, 34f), M3Theme.OnSurfaceVariant);

            // Card 5: Pixel Watch gyro vs gaze for dragging/resizing windows
            var watchWinCard = M3.Surface("Watch Window Card", m_Panel, new Vector2(0f, -78f), new Vector2(556f, 102f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Watch Window Label", watchWinCard.rectTransform, "Sposta / Ridimensiona Finestre", M3Theme.Type.TitleMedium,
                new Vector2(0f, 30f), new Vector2(512f, 24f), M3Theme.OnSurface);
            m_WatchWindowGyroSegments = M3.Segments("Watch Window Mode", watchWinCard.rectTransform,
                new[] { "Giroscopio Watch", "Sguardo" },
                new Vector2(0f, -4f), 252f, OnSelectWatchWindowGyro);
            m_WatchWindowGyroHint = M3.Text("Watch Window Hint", watchWinCard.rectTransform, "", M3Theme.Type.BodyMedium,
                new Vector2(0f, -36f), new Vector2(512f, 28f), M3Theme.OnSurfaceVariant);

            // Card 6: Pixel Watch Y-axis orientation
            var watchCard = M3.Surface("Watch Y Card", m_Panel, new Vector2(0f, -181f), new Vector2(556f, 84f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Watch Y Label", watchCard.rectTransform, "Asse Y Cursore Pixel Watch", M3Theme.Type.TitleMedium,
                new Vector2(0f, 21f), new Vector2(512f, 24f), M3Theme.OnSurface);
            m_WatchYSegments = M3.Segments("Watch Y Mode", watchCard.rectTransform,
                new[] { "Normale", "Invertito" },
                new Vector2(0f, -14f), 252f, OnSelectWatchY);

            // Card 7: Spatial 6DoF tracking toggle
            var trackCard = M3.Surface("Tracking Card", m_Panel, new Vector2(0f, -275f), new Vector2(556f, 84f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Tracking Label", trackCard.rectTransform, "Tracciamento Spaziale", M3Theme.Type.TitleMedium,
                new Vector2(0f, 21f), new Vector2(512f, 24f), M3Theme.OnSurface);
            m_TrackingSegments = M3.Segments("Tracking Mode", trackCard.rectTransform,
                new[] { "6DoF (ARCore)", "3DoF (Grandangolare)" },
                new Vector2(0f, -14f), 252f, OnSelectTracking);

            // Card 8: Shizuku integration card
            var shizukuCard = M3.Surface("Shizuku Card", m_Panel, new Vector2(0f, -387f), new Vector2(556f, 120f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeDialog, style: VisionSurfaceStyle.Recessed);
            M3.Text("Shizuku Label", shizukuCard.rectTransform, "Shizuku (App, Notifiche, Media)", M3Theme.Type.TitleMedium,
                new Vector2(0f, 36f), new Vector2(512f, 26f), M3Theme.OnSurface);
            m_ShizukuHint = M3.Text("Shizuku Hint", shizukuCard.rectTransform, "Stato Shizuku in verifica…", M3Theme.Type.BodyMedium,
                new Vector2(-96f, -16f), new Vector2(316f, 52f), M3Theme.OnSurfaceVariant, TMPro.TextAlignmentOptions.Left);
            m_ShizukuBtn = M3.Button("Configura Shizuku", shizukuCard.rectTransform, "Configura", M3Button.Variant.Filled,
                new Vector2(174f, -16f), OnConfigureShizukuClicked, "verified_user", 172f);

            M3.Button("Close App", m_Panel, "Chiudi", M3Button.Variant.Tonal,
                new Vector2(0f, -496f), CloseSelf, "close", 180f);

            WorldUi.PlaceInFront(m_Panel, m_Core.Head, k_PanelDistance, 0f);
            FloatingWindowHandle.Configure(m_Panel, CloseSelf, restoreOnRecenter: false);
        }

        void CloseSelf() => m_Core?.Demos?.CloseApp("settings");

        void OnSelectTracking(int index)
        {
            if (m_Core == null || m_Core.Fusion == null)
                return;
            var sixDof = index == 0;
            m_Core.Passthrough?.SetUltrawide(!sixDof);
            m_Core.Fusion.SetMode(sixDof ? HeadPoseFusion.Mode.SixDof : HeadPoseFusion.Mode.ThreeDof);
            RefreshUi();
        }

        void OnSelectCamera(int index)
        {
            if (m_Core == null || m_Core.Passthrough == null)
                return;
            var ultrawide = index == 1;
            m_Core.Passthrough.SetUltrawide(ultrawide);
            if (!m_Core.Passthrough.IsOn)
            {
                m_Core.Fusion?.SetMode(ultrawide ? HeadPoseFusion.Mode.ThreeDof : HeadPoseFusion.Mode.SixDof);
                m_Core.Passthrough.Choose(true);
            }
            RefreshUi();
        }

        void OnSelectMovingCamera(int index)
        {
            m_Core?.Passthrough?.SetMovingCamera(index == 0);
            RefreshUi();
        }

        void OnSelectVisionPinch(int index)
        {
            PinchRouter.SetVisionPinchEnabled(index == 0);
            RefreshUi();
        }

        void OnSelectWatchGyro(int index)
        {
            WatchLink.SetGyroCursorEnabled(index == 0);
            RefreshUi();
        }

        void OnSelectWatchWindowGyro(int index)
        {
            WatchLink.SetGyroWindowControlEnabled(index == 0);
            RefreshUi();
        }

        void OnSelectWatchY(int index)
        {
            WatchLink.SetInvertPitchY(index == 1);
            RefreshUi();
        }

        void OnConfigureShizukuClicked()
        {
            SettingsPanelHub.OpenShizukuSetup();
        }

        void RefreshUi()
        {
            var ultrawide = m_Core != null && m_Core.Passthrough != null && m_Core.Passthrough.UseUltrawide;
            if (m_CameraSegments != null && m_CameraSegments.Length == 2)
            {
                m_CameraSegments[0].Selected = !ultrawide;
                m_CameraSegments[1].Selected = ultrawide;
            }
            if (m_CameraHint != null)
            {
                m_CameraHint.text = ultrawide
                    ? "Grandangolare 0.5× (~110° FOV, default): massimo angolo per mani, pinch e scorrimento."
                    : "Principale (~68° FOV): tracciamento 6DoF ARCore completo e ancoraggio spaziale.";
            }

            var movingCameraOn = m_Core == null || m_Core.Passthrough == null || m_Core.Passthrough.MovingCamera;
            if (m_MovingCameraSegments != null && m_MovingCameraSegments.Length == 2)
            {
                m_MovingCameraSegments[0].Selected = movingCameraOn;
                m_MovingCameraSegments[1].Selected = !movingCameraOn;
            }
            if (m_MovingCameraHint != null)
            {
                m_MovingCameraHint.text = movingCameraOn
                    ? "Camera mobile (6DoF): compensa la rotazione per ridurre la nausea restando ancorata al mondo."
                    : "Camera fissa su schermo: la visuale resta centrata davanti agli occhi senza ritardi di movimento.";
            }

            var visionOn = PinchRouter.VisionPinchEnabled;
            if (m_VisionPinchSegments != null && m_VisionPinchSegments.Length == 2)
            {
                m_VisionPinchSegments[0].Selected = visionOn;
                m_VisionPinchSegments[1].Selected = !visionOn;
            }
            if (m_VisionPinchHint != null)
            {
                m_VisionPinchHint.text = visionOn
                    ? "Pinch visivo attivo: tocca per cliccare o tieni premuto e trascina per scorrere."
                    : "Pinch visivo disattivato. Il rilevamento del palmo aperto per la Home resta sempre attivo.";
            }

            var watchGyroOn = WatchLink.GyroCursorEnabled;
            if (m_WatchGyroSegments != null && m_WatchGyroSegments.Length == 2)
            {
                m_WatchGyroSegments[0].Selected = watchGyroOn;
                m_WatchGyroSegments[1].Selected = !watchGyroOn;
            }
            if (m_WatchGyroHint != null)
            {
                m_WatchGyroHint.text = watchGyroOn
                    ? "Muovi il cursore a schermo ruotando il polso con il giroscopio del Pixel Watch."
                    : "Cursore fisso al centro dello schermo (come senza Watch).";
            }

            var watchWindowGyroOn = WatchLink.GyroWindowControlEnabled;
            if (m_WatchWindowGyroSegments != null && m_WatchWindowGyroSegments.Length == 2)
            {
                m_WatchWindowGyroSegments[0].Selected = watchWindowGyroOn;
                m_WatchWindowGyroSegments[1].Selected = !watchWindowGyroOn;
            }
            if (m_WatchWindowGyroHint != null)
            {
                m_WatchWindowGyroHint.text = watchWindowGyroOn
                    ? "Trascini e ridimensioni le finestre ruotando il polso con il Watch."
                    : "Le finestre seguono lo sguardo durante trascinamento e ridimensionamento.";
            }

            var invertY = WatchLink.InvertPitchY;
            if (m_WatchYSegments != null && m_WatchYSegments.Length == 2)
            {
                m_WatchYSegments[0].Selected = !invertY;
                m_WatchYSegments[1].Selected = invertY;
            }

            var sixDof = m_Core != null && m_Core.Fusion != null && m_Core.Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (m_TrackingSegments != null && m_TrackingSegments.Length == 2)
            {
                m_TrackingSegments[0].Selected = sixDof;
                m_TrackingSegments[1].Selected = !sixDof;
            }

            if (m_ShizukuHint != null)
            {
                var bridge = SystemBridge.Instance;
                if (bridge != null && bridge.IsShizukuReady)
                {
                    m_ShizukuHint.text = bridge.NotificationAccessEnabled
                        ? "Shizuku pronto • App telefono, notifiche e media attivi."
                        : "Shizuku pronto • Attiva l'accesso alle notifiche.";
                }
                else
                {
                    m_ShizukuHint.text = "Configura Shizuku per app del telefono, banner notifiche e controllo media.";
                }
            }
        }
    }
}
