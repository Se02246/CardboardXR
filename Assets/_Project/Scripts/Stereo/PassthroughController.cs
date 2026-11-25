using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using CardboardXR.UI;
using UnityEngine.XR.ARFoundation;

namespace CardboardXR.Stereo
{
    // Camera passthrough in the viewer. Each new ARCore camera image is drawn with ARCore's own
    // background material into a texture, with its display transform widened so the texture holds
    // the whole camera image instead of the screen-shaped crop ARCore shows. A screen quad in front
    // of the AR camera shows it to both eyes at the angle the camera saw it, so the room appears at
    // true scale. The AR camera's pose driver moves it only with each new ARCore pose, which comes
    // with each new image, so the picture stays fixed to the room as the head turns and only its
    // edges trail. The camera sees less than a Cardboard eye, so the image is a window with a dark
    // border. Passthrough needs 6DoF, since ARCore must run, and hides the virtual environment (every
    // VirtualEnvironment, in whichever scene).
    // Switching to 3DoF turns it off until 6DoF is back. The "passthrough" launch option (1) starts
    // with it on. While it's on, the eyes move onto the camera (HeadPoseFusion.EyesAtCamera), so the
    // image is seen from where it was taken. The image is flat, so things in it only line up in both
    // eyes at its own distance; FocusDistance moves it to what the user is looking at, such as a
    // tracked hand. Anything else drawn keeps its own depth and fights that flat image, which is seen
    // as doubling, so while passthrough is on the rig's eyes move together (StereoRig.MonoEyes) and
    // the whole view becomes one flat, self-consistent window. In the single phone view the view also
    // takes the AR camera's pose itself, as an ordinary AR app does: the image then sits still on the
    // screen, at the price of the image's delay, which only matters in a viewer.
    public class PassthroughController : MonoBehaviour
    {
        static readonly int k_DisplayTransformId = Shader.PropertyToID("_UnityDisplayTransform");
        static readonly int k_PadScaleId = Shader.PropertyToID("_PadScale");
        static readonly int k_MidBlurMapId = Shader.PropertyToID("_MidBlurMap");
        static readonly int k_AmbientMapId = Shader.PropertyToID("_AmbientMap");
        static readonly int k_HasAmbientId = Shader.PropertyToID("_HasAmbient");
        static readonly int k_BlurStepId = Shader.PropertyToID("_BlurStep");
        static readonly int k_VignetteStrengthId = Shader.PropertyToID("_VignetteStrength");
        const string k_UltrawidePrefKey = "CardboardXR.UltrawideV2";
        const string k_MovingCameraPrefKey = "CardboardXR.MovingCamera";
        // Wait briefly after leaving 6DoF so ARCore releases camera 0 before Camera2 opens it.
        const float k_UltrawideHandoffSeconds = 0.45f;
        // Ambient blur padding around the exact 1:1 Main Camera frustum so soft live room colors
        // surround the frame instead of harsh black borders.
        const float k_MainAmbientPadScale = 3.2f;
        const float k_UltrawideAmbientPadScale = 2.0f;
        // Rotational reprojection band: 1:1 world-lock up to LinearMaxDeg, then C1-smooth asymptotic
        // clamping up to HardMaxDeg; if the camera feed stalls, fade smoothly back to current head pose.
        const float k_ReprojLinearMaxDeg = 16f;
        const float k_ReprojHardMaxDeg = 26f;
        const float k_CameraStaleStartSeconds = 0.25f;
        const float k_CameraStaleFadeSeconds = 0.25f;
        // The nearest the image comes, in meters.
        const float k_MinDistance = 0.2f;
        // How quickly the image follows a change of focus: most of the way in this many seconds.
        const float k_FocusSeconds = 0.25f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [Tooltip("The stereo rig: its eyes move together while passthrough is on, so nothing drawn has " +
                 "a depth the one flat image can't match.")]
        [SerializeField] StereoRig m_Rig;
        [Tooltip("The AR camera. Its ARCameraManager supplies the images and its TrackedPoseDriver places the screen.")]
        [SerializeField] Camera m_ArCamera;
        [Tooltip("A unit quad, a child of the AR camera, that shows the camera image.")]
        [SerializeField] Renderer m_Screen;
        [Tooltip("The panel's toggle, shown selected while passthrough is on.")]
        [SerializeField] M3Button m_Toggle;
        [Tooltip("How far in front of the camera the image is shown without a focus. It's one flat image, so everything in it appears at this depth.")]
        [SerializeField] float m_Distance = 2f;
        [Tooltip("Height of the passthrough texture in pixels; its width follows the camera image's shape.")]
        [SerializeField] int m_TextureHeight = 720;

        ARCameraManager m_CameraManager;
        TrackedPoseDriver m_PoseDriver;
        Material m_CameraMaterial;
        Material m_BlurMaterial;
        Mesh m_Quad;
        CommandBuffer m_Commands;
        CommandBuffer m_BlurCommands;
        RenderTexture m_Texture;
        RenderTexture m_BlurMid;
        RenderTexture m_MidBlurTexture;
        RenderTexture m_BlurTemp;
        RenderTexture m_AmbientTexture;
        float m_NextAmbientBlurAt;
        long m_LastCameraTimestampNs;
        float m_LastCameraArrivalTime;
        bool m_ArFrameSubscribed;
        bool m_On;
        bool m_Isolated;
        // 3DoF turned passthrough off, so it comes back with 6DoF.
        bool m_ResumeWithSixDof;
        // ArDebugView drives the AR camera itself.
        bool m_ArViewActive;
        bool m_TorchCameraOverride;
        bool m_TorchPrevUseUltrawide;
        bool m_TorchPrevSixDof;
        bool m_TorchPrevBgUltrawide;
        // The image's frustum at unit distance, in the AR camera's x and y, once an image arrived.
        Vector2 m_FrustumCenter;
        Vector2 m_FrustumSize;
        bool m_HasImage;
        float? m_FocusDistance;
        float m_ShownDistance;
        bool m_UseUltrawide;
        bool m_MovingCamera = true;
        bool m_BackgroundUltrawide;
        bool m_UltrawideRunning;
        bool m_UltrawidePending;
        float m_UltrawideStartAt;
        Texture2D m_UltrawideTexture;
        static PassthroughController s_Instance;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Ultrawide;
        AndroidJavaObject m_UltrawideBuf0Obj;
        AndroidJavaObject m_UltrawideBuf1Obj;
        System.IntPtr m_UltrawideBuf0Ptr;
        System.IntPtr m_UltrawideBuf1Ptr;
        int m_CachedUwWidth;
        int m_CachedUwHeight;
        float m_CachedUwHFov = 108f;
        float m_CachedUwVFov = 72f;
#endif

        public bool IsOn => m_On;
        public bool UseUltrawide => m_UseUltrawide;
        public bool MovingCamera => m_MovingCamera;
        // True while Camera2 holds the rear camera for ultrawide passthrough; PinchSensor shares its live Texture2D.
        public static bool IsUltrawideActive { get; private set; }
        public static Texture2D UltrawideTexture { get; private set; }
        public static int UltrawideFrameId { get; private set; }
        public static event System.Action<Texture2D> UltrawideFrameReady;
        public static float UltrawideHorizontalFovDeg { get; private set; } = 108f;
        public static float UltrawideVerticalFovDeg { get; private set; } = 72f;

        // Where the image should be, in meters from the camera, or null for m_Distance. It eases there.
        public float? FocusDistance
        {
            get => m_FocusDistance;
            set
            {
                if (value.HasValue != m_FocusDistance.HasValue && m_On)
                    Debug.Log(value.HasValue ? $"[Passthrough] Focus at {value.Value:F2} m" : $"[Passthrough] Focus back at {m_Distance:F1} m");
                m_FocusDistance = value;
            }
        }

        void Awake()
        {
            s_Instance = this;
            m_CameraManager = m_ArCamera.GetComponent<ARCameraManager>();
            m_PoseDriver = m_ArCamera.GetComponent<TrackedPoseDriver>();
            m_ShownDistance = m_Distance;
            m_UseUltrawide = PlayerPrefs.GetInt(k_UltrawidePrefKey, 1) != 0;
            m_MovingCamera = PlayerPrefs.GetInt(k_MovingCameraPrefKey, 1) != 0;
        }

        void OnEnable()
        {
            Application.onBeforeRender += SyncPassthroughPoseBeforeRender;
        }

        void OnDisable()
        {
            Application.onBeforeRender -= SyncPassthroughPoseBeforeRender;
        }

        // Rotationally reprojects the passthrough quad to match where the head pointed when the
        // camera image was captured, using a C1-smooth asymptotic clamp past 16 deg (never exceeding
        // 26 deg) and smoothly fading to current head pose if camera frames stop arriving.
        public static Quaternion ComputeReprojectedRotation(Quaternion headRot, Quaternion captureRot,
            bool hasCaptureRot, float secondsSinceFrame)
        {
            if (!hasCaptureRot)
                return headRot;

            var freshness = secondsSinceFrame <= k_CameraStaleStartSeconds
                ? 1f
                : 1f - Mathf.Clamp01((secondsSinceFrame - k_CameraStaleStartSeconds) / k_CameraStaleFadeSeconds);
            if (freshness <= 0.0001f)
                return headRot;

            var angle = Quaternion.Angle(headRot, captureRot);
            if (angle <= 0.001f)
                return captureRot;

            float targetAngle;
            if (angle <= k_ReprojLinearMaxDeg)
            {
                targetAngle = angle;
            }
            else
            {
                var span = k_ReprojHardMaxDeg - k_ReprojLinearMaxDeg;
                var excess = angle - k_ReprojLinearMaxDeg;
                targetAngle = k_ReprojLinearMaxDeg + span * (excess / (excess + span));
            }

            return Quaternion.RotateTowards(headRot, captureRot, targetAngle * freshness);
        }

        void SetArFrameSubscribed(bool subscribed)
        {
            if (m_ArFrameSubscribed == subscribed || m_CameraManager == null)
                return;
            m_ArFrameSubscribed = subscribed;
            if (subscribed)
            {
                m_CameraManager.frameReceived += OnCameraFrame;
            }
            else
            {
                m_CameraManager.frameReceived -= OnCameraFrame;
                m_LastCameraTimestampNs = 0;
                m_LastCameraArrivalTime = 0f;
            }
        }

        public static bool ShouldReprojectCamera(bool sixDof, bool isMono, bool movingCamera) =>
            sixDof && !isMono && movingCamera;

        public void SetMovingCamera(bool enabled)
        {
            if (m_MovingCamera == enabled)
                return;
            m_MovingCamera = enabled;
            PlayerPrefs.SetInt(k_MovingCameraPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log(enabled
                ? "[Passthrough] Moving camera (anti-nausea reprojection) enabled"
                : "[Passthrough] Camera fixed to screen (anti-nausea reprojection disabled)");
            ApplyPassthroughPose();
        }

        [BeforeRenderOrder(100)]
        void SyncPassthroughPoseBeforeRender() => ApplyPassthroughPose();

        void ApplyPassthroughPose()
        {
            if (!m_On || m_Isolated || m_Fusion == null || m_ArCamera == null)
                return;

            var headPos = m_Fusion.transform.position;
            var headRot = m_Fusion.transform.rotation;
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            var isMono = m_Rig != null && m_Rig.IsMono;
            if (!ShouldReprojectCamera(sixDof, isMono, m_MovingCamera))
            {
                m_ArCamera.transform.SetPositionAndRotation(headPos, headRot);
                return;
            }

            var captureRot = headRot;
            var hasCapture = m_LastCameraTimestampNs != 0 &&
                             m_Fusion.TryGetWorldRotationAt(m_LastCameraTimestampNs, out captureRot);
            var age = m_LastCameraArrivalTime > 0f ? Mathf.Max(0f, Time.unscaledTime - m_LastCameraArrivalTime) : 0f;
            var reprojectedRot = ComputeReprojectedRotation(headRot, captureRot, hasCapture, age);
            m_ArCamera.transform.SetPositionAndRotation(headPos, reprojectedRot);
        }

        // Lets 3DoF hand/pinch tracking keep the 110 deg Ultrawide feed active even while passthrough
        // visuals are off (e.g. inside 360 Video or 3DoF VR scenes).
        public static void SetBackgroundUltrawide(bool wanted)
        {
            if (s_Instance != null)
                s_Instance.m_BackgroundUltrawide = wanted;
        }

        // After the other components' OnEnable, so the mode set here sticks.
        void Start()
        {
            m_ArViewActive = LaunchOptions.GetInt("arView", 0) != 0;
            if (LaunchOptions.GetInt("passthrough", 0) != 0)
                SetPassthrough(true);
        }

        void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
            StopUltrawide();
            SetArFrameSubscribed(false);
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            ReleaseRenderTexture(ref m_BlurMid);
            ReleaseRenderTexture(ref m_MidBlurTexture);
            ReleaseRenderTexture(ref m_BlurTemp);
            ReleaseRenderTexture(ref m_AmbientTexture);
            if (m_UltrawideTexture != null)
                Destroy(m_UltrawideTexture);
            if (m_CameraMaterial != null)
                Destroy(m_CameraMaterial);
            if (m_BlurMaterial != null)
                Destroy(m_BlurMaterial);
            if (m_Quad != null)
                Destroy(m_Quad);
            m_Commands?.Release();
            m_BlurCommands?.Release();
        }

        static void ReleaseRenderTexture(ref RenderTexture rt)
        {
            if (rt == null)
                return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        // Switches between the main ARCore 6DoF camera (false) and the 0.5x ultrawide Camera2 feed (true).
        public void SetUltrawide(bool ultrawide)
        {
            if (m_UseUltrawide == ultrawide)
                return;
            m_UseUltrawide = ultrawide;
            PlayerPrefs.SetInt(k_UltrawidePrefKey, ultrawide ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[Passthrough] Camera mode set to {(ultrawide ? "Ultrawide 0.5x (3DoF)" : "Main (6DoF ARCore)")}");
            if (m_TorchCameraOverride)
            {
                m_TorchPrevUseUltrawide = ultrawide;
                m_TorchPrevSixDof = !ultrawide;
                return;
            }
            if (m_On)
            {
                SetPassthrough(false);
                SetPassthrough(true);
            }
        }

        // Temporarily hands rear camera from ARCore to Camera2 when Flashlight is toggled on
        // in 6DoF (ARCore holds camera 0 exclusively on non-rooted devices).
        public void SetTorchCameraOverride(bool active)
        {
            if (active == m_TorchCameraOverride)
                return;

            if (active)
            {
                m_TorchPrevUseUltrawide = m_UseUltrawide;
                m_TorchPrevSixDof = m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
                m_TorchPrevBgUltrawide = m_BackgroundUltrawide;
                m_TorchCameraOverride = true;
                m_UseUltrawide = true;
                if (m_On)
                {
                    SetPassthrough(false);
                    SetPassthrough(true);
                }
                else
                {
                    m_Fusion?.SetMode(HeadPoseFusion.Mode.ThreeDof);
                    m_BackgroundUltrawide = true;
                }
                Debug.Log("[Passthrough] Torch override active: switched to Camera2 session for rear flash");
            }
            else
            {
                m_TorchCameraOverride = false;
                var wasOn = m_On;
                if (wasOn)
                    SetPassthrough(false);
                StopUltrawide();
                m_BackgroundUltrawide = m_TorchPrevBgUltrawide;
                m_UseUltrawide = m_TorchPrevUseUltrawide;
                if (m_TorchPrevSixDof)
                    m_Fusion?.SetMode(HeadPoseFusion.Mode.SixDof);
                if (wasOn)
                    SetPassthrough(true);
                Debug.Log("[Passthrough] Torch override ended: restored previous camera mode");
            }
        }

        public void Toggle() => Choose(!m_On);

        // Hides ONLY the passthrough camera screen while keeping ARCore 6DoF tracking and hand
        // detection 100% alive in the background (for 360-degree Isolation / Environments mode).
        public void SetIsolated(bool isolated)
        {
            if (m_Isolated == isolated)
                return;
            m_Isolated = isolated;
            if (m_Screen != null)
                m_Screen.enabled = m_On && m_HasImage && !m_Isolated;
            if (m_Rig != null)
                m_Rig.MonoEyes = m_On && !m_Isolated;
            if (m_Fusion != null)
                m_Fusion.EyesAtCamera = m_On && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof && !m_Isolated;
            VirtualEnvironment.SetAllHidden(m_On || m_Isolated);
        }

        // Turns it on or off as the user's own choice, so 3DoF turning it off doesn't bring it back
        // with 6DoF later.
        public void Choose(bool on)
        {
            m_ResumeWithSixDof = false;
            SetPassthrough(on);
        }

        public void SetPassthrough(bool on)
        {
            if (m_ArViewActive || on == m_On)
                return;

            m_On = on;
            if (m_PoseDriver != null)
                m_PoseDriver.enabled = false;
            if (on)
            {
                if (m_UseUltrawide)
                {
                    SetArFrameSubscribed(false);
                    m_HasImage = false;
                    m_Fusion.SetMode(HeadPoseFusion.Mode.ThreeDof);
                    m_UltrawidePending = true;
                    m_UltrawideStartAt = Time.unscaledTime + k_UltrawideHandoffSeconds;
                    IsUltrawideActive = true;
                }
                else
                {
                    StopUltrawide();
                    m_Fusion.SetMode(HeadPoseFusion.Mode.SixDof);
                    SetArFrameSubscribed(true);
                }
            }
            else
            {
                SetArFrameSubscribed(false);
                if (!m_UseUltrawide || !m_BackgroundUltrawide || m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
                    StopUltrawide();
                if (m_Fusion != null)
                    m_Fusion.FollowPose = null;
            }

            m_Fusion.EyesAtCamera = on && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof && !m_Isolated;
            if (m_Rig != null)
                m_Rig.MonoEyes = on && !m_Isolated;
            // Shown once the first image has arrived (unless hidden by 360 Isolation mode).
            m_Screen.enabled = on && m_HasImage && !m_Isolated;
            VirtualEnvironment.SetAllHidden(on || m_Isolated);
            if (m_Toggle != null)
                m_Toggle.Selected = on;
            Debug.Log(on ? (m_UseUltrawide ? "[Passthrough] On (Ultrawide 0.5x)" : "[Passthrough] On") : "[Passthrough] Off");
        }

        void StartUltrawide()
        {
            m_UltrawidePending = false;
            m_UltrawideRunning = true;
            IsUltrawideActive = true;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (m_Ultrawide == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    m_Ultrawide = new AndroidJavaObject("com.brenpoly.cardboardxr.camera.UltrawideCamera", activity);
                }
                m_Ultrawide.Call("start");
                Debug.Log("[Passthrough] Ultrawide Camera2 started");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Passthrough] Ultrawide start failed: {e.Message}");
                StopUltrawide();
            }
#endif
        }

        void StopUltrawide()
        {
            m_UltrawidePending = false;
            m_UltrawideRunning = false;
            IsUltrawideActive = false;
            UltrawideTexture = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_UltrawideBuf0Ptr = System.IntPtr.Zero;
            m_UltrawideBuf1Ptr = System.IntPtr.Zero;
            m_CachedUwWidth = 0;
            m_CachedUwHeight = 0;
            if (m_UltrawideBuf0Obj != null)
            {
                try { m_UltrawideBuf0Obj.Dispose(); } catch (System.Exception) { }
                m_UltrawideBuf0Obj = null;
            }
            if (m_UltrawideBuf1Obj != null)
            {
                try { m_UltrawideBuf1Obj.Dispose(); } catch (System.Exception) { }
                m_UltrawideBuf1Obj = null;
            }
            if (m_Ultrawide != null)
            {
                try { m_Ultrawide.Call("stop"); }
                catch (System.Exception) { }
            }
#endif
            if (m_Texture != null && m_Screen != null)
                m_Screen.material.mainTexture = m_Texture;
        }

        void UpdateUltrawideFrame(bool updateScreen = true)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!m_UltrawideRunning || m_Ultrawide == null)
                return;
            if (m_CachedUwWidth <= 0 || m_CachedUwHeight <= 0)
            {
                m_CachedUwWidth = m_Ultrawide.Call<int>("getWidth");
                m_CachedUwHeight = m_Ultrawide.Call<int>("getHeight");
                if (m_CachedUwWidth <= 0 || m_CachedUwHeight <= 0)
                    return;
                m_CachedUwHFov = m_Ultrawide.Call<float>("getHorizontalFovDeg");
                m_CachedUwVFov = m_Ultrawide.Call<float>("getVerticalFovDeg");
            }
            var w = m_CachedUwWidth;
            var h = m_CachedUwHeight;
            if (m_UltrawideBuf0Ptr == System.IntPtr.Zero && m_UltrawideBuf1Ptr == System.IntPtr.Zero)
            {
                m_UltrawideBuf0Obj = m_Ultrawide.Call<AndroidJavaObject>("getBuffer", 0);
                m_UltrawideBuf1Obj = m_Ultrawide.Call<AndroidJavaObject>("getBuffer", 1);
                unsafe
                {
                    if (m_UltrawideBuf0Obj != null)
                        m_UltrawideBuf0Ptr = (System.IntPtr)AndroidJNI.GetDirectBufferAddress(m_UltrawideBuf0Obj.GetRawObject());
                    if (m_UltrawideBuf1Obj != null)
                        m_UltrawideBuf1Ptr = (System.IntPtr)AndroidJNI.GetDirectBufferAddress(m_UltrawideBuf1Obj.GetRawObject());
                }
            }
            if (m_UltrawideBuf0Ptr == System.IntPtr.Zero && m_UltrawideBuf1Ptr == System.IntPtr.Zero)
                return;
            var bufIdx = m_Ultrawide.Call<int>("acquireFrontBufferIndex");
            if (bufIdx < 0)
                return;
            var ptr = bufIdx == 1 ? m_UltrawideBuf1Ptr : m_UltrawideBuf0Ptr;
            if (ptr == System.IntPtr.Zero)
                return;
            if (m_UltrawideTexture == null || m_UltrawideTexture.width != w || m_UltrawideTexture.height != h)
            {
                if (m_UltrawideTexture != null)
                    Destroy(m_UltrawideTexture);
                m_UltrawideTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "Passthrough Ultrawide",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            m_UltrawideTexture.LoadRawTextureData(ptr, w * h * 4);
            m_UltrawideTexture.Apply(false);
            UltrawideTexture = m_UltrawideTexture;
            UltrawideFrameId++;

            var hFov = m_CachedUwHFov;
            var vFov = m_CachedUwVFov;
            UltrawideHorizontalFovDeg = hFov;
            UltrawideVerticalFovDeg = vFov;
            UltrawideFrameReady?.Invoke(m_UltrawideTexture);

            if (!updateScreen)
                return;

            m_Screen.material.mainTexture = m_UltrawideTexture;
            if (!m_Isolated)
                UpdateAmbientBlur(m_UltrawideTexture);

            m_FrustumCenter = Vector2.zero;
            m_FrustumSize = new Vector2(
                2f * Mathf.Tan(hFov * 0.5f * Mathf.Deg2Rad),
                2f * Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad));
            if (!m_HasImage)
            {
                m_HasImage = true;
                m_Screen.enabled = m_On && !m_Isolated;
                Debug.Log($"[Passthrough] Ultrawide frame {w}x{h}, FOV {hFov:F0} x {vFov:F0} deg");
            }
#endif
        }

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            var source = m_CameraManager.cameraMaterial;
            if (source == null || args.textures == null || args.textures.Count == 0 ||
                !args.displayMatrix.HasValue || !args.projectionMatrix.HasValue)
                return;
            // Always the whole camera image, in both views. Mono once cropped it to ARCore's
            // screen-shaped rectangle so it filled the screen, as an ordinary AR app does, but seeing
            // everything the camera saw was better even at the price of a border around it.
            if (!TryWidenDisplayTransform(args.displayMatrix.Value, out var widened, out var min, out var max))
                return;

            if (m_CameraMaterial == null)
            {
                m_CameraMaterial = new Material(source) { name = "Passthrough Camera" };
                EnableRenderPipelineKeyword();
            }
            for (var i = 0; i < args.textures.Count; i++)
                m_CameraMaterial.SetTexture(args.propertyNameIds[i], args.textures[i]);
            m_CameraMaterial.SetMatrix(k_DisplayTransformId, widened);

            var size = max - min;
            EnsureTexture(Screen.width * size.x / (Screen.height * size.y));
            DrawCameraImage();

            if (args.timestampNs is long timestamp && timestamp != 0)
            {
                if (timestamp != m_LastCameraTimestampNs)
                {
                    m_LastCameraTimestampNs = timestamp;
                    m_LastCameraArrivalTime = Time.unscaledTime;
                }
            }
            else
            {
                m_LastCameraArrivalTime = Time.unscaledTime;
            }

            // Screen UV maps to NDC as 2uv - 1, which ARCore's projection maps to camera space at unit
            // distance as (ndc + p02) / p00 across and (ndc + p12) / p11 up.
            var projection = args.projectionMatrix.Value;
            m_FrustumSize = new Vector2(2f * size.x / projection.m00, 2f * size.y / projection.m11);
            m_FrustumCenter = new Vector2(
                (min.x + max.x - 1f + projection.m02) / projection.m00,
                (min.y + max.y - 1f + projection.m12) / projection.m11);
            if (m_HasImage)
                return;

            m_HasImage = true;
            m_Screen.enabled = m_On && !m_Isolated;
            Debug.Log($"[Passthrough] Camera image {m_Texture.width}x{m_Texture.height}, " +
                      $"{2f * Mathf.Atan(m_FrustumSize.x / 2f) * Mathf.Rad2Deg:F0} x " +
                      $"{2f * Mathf.Atan(m_FrustumSize.y / 2f) * Mathf.Rad2Deg:F0} deg");
            LogCameraMaterial(args);
        }

        // ARCore's background shader maps screen UV to camera-texture UV as
        // tex = u * (m00, m01) + v * (m10, m11) + (m20, m21), and only screen UV 0 to 1 is drawn: a
        // crop of the camera image shaped like the screen. This finds the screen-UV rectangle the
        // whole texture covers, from min to max, and widens the transform so that screen UV 0 to 1
        // covers all of it.
        public static bool TryWidenDisplayTransform(Matrix4x4 display, out Matrix4x4 widened, out Vector2 min, out Vector2 max)
        {
            widened = display;
            min = max = Vector2.zero;
            var c0 = new Vector2(display.m00, display.m01);
            var c1 = new Vector2(display.m10, display.m11);
            var c2 = new Vector2(display.m20, display.m21);
            var determinant = c0.x * c1.y - c1.x * c0.y;
            if (Mathf.Abs(determinant) < 1e-6f)
                return false;

            Vector2 ScreenUv(Vector2 texture)
            {
                var t = texture - c2;
                return new Vector2((c1.y * t.x - c1.x * t.y) / determinant, (c0.x * t.y - c0.y * t.x) / determinant);
            }

            var a = ScreenUv(new Vector2(0f, 0f));
            var b = ScreenUv(new Vector2(1f, 0f));
            var c = ScreenUv(new Vector2(0f, 1f));
            var d = ScreenUv(new Vector2(1f, 1f));
            min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            var size = max - min;
            widened.m00 = c0.x * size.x;
            widened.m01 = c0.y * size.x;
            widened.m10 = c1.x * size.y;
            widened.m11 = c1.y * size.y;
            var offset = min.x * c0 + min.y * c1 + c2;
            widened.m20 = offset.x;
            widened.m21 = offset.y;
            return true;
        }

        void EnsureTexture(float aspect)
        {
            var height = Mathf.Max(16, m_TextureHeight);
            var width = Mathf.Max(16, Mathf.RoundToInt(height * aspect));
            if (m_Texture != null && m_Texture.width == width && m_Texture.height == height)
                return;
            if (m_Texture != null)
            {
                m_Texture.Release();
                Destroy(m_Texture);
            }
            // With a depth buffer, because both providers' background shaders declare ZWrite On and
            // write SV_Depth. Metal drops the draw when the target has no depth to write to, which
            // left the texture on the clear colour; GLES on Android tolerated it, so this only ever
            // showed up on the iPhone, as a black passthrough image with nothing logged.
            m_Texture = new RenderTexture(width, height, 24) { name = "Passthrough" };
            m_Screen.material.mainTexture = m_Texture;
        }

        // Reads one pixel back from the middle of the passthrough texture, once. A black quad in the
        // viewer has two quite different causes, and the frame rate and logs look identical for both:
        // either the camera image never reaches this texture, or it does and the screen that shows it
        // is what fails. This says which half of the pipeline to look at.
        Color SampleTextureCentre()
        {
            var previous = RenderTexture.active;
            var probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = m_Texture;
                probe.ReadPixels(new Rect(m_Texture.width / 2f, m_Texture.height / 2f, 1f, 1f), 0, 0);
                probe.Apply();
                return probe.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(probe);
            }
        }

        // ARKit ships one background shader for both render pipelines and chooses between them with a
        // keyword. Without it the shader takes its built-in-pipeline branch, which draws black in a
        // URP project: the image arrives, the draw succeeds, and nothing reports a failure. AR
        // Foundation sets this on its own background renderer feature, which we bypass by drawing the
        // camera image ourselves. ARCore needs nothing here, because it ships a shader per pipeline,
        // so this asks the shader whether it has the keyword rather than assuming a platform.
        void EnableRenderPipelineKeyword()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            // Looked up rather than constructed: constructing one the shader lacks, as ARCore's does,
            // logs an error on Android.
            var keyword = m_CameraMaterial.shader.keywordSpace.FindKeyword("ARKIT_BACKGROUND_URP");
            if (keyword.isValid)
                m_CameraMaterial.EnableKeyword(keyword);
        }

        // What the provider actually handed us, logged once. The two platforms differ here more than
        // anywhere else: ARCore gives one RGB texture, ARKit two (luma and chroma) that its own shader
        // converts, and a variant of that shader that didn't survive into the build draws black
        // without failing. Shader, keywords and textures together say which of those is the case.
        void LogCameraMaterial(ARCameraFrameEventArgs args)
        {
            var shader = m_CameraMaterial.shader;
            var keywords = m_CameraMaterial.shaderKeywords;
            var textures = new string[args.textures.Count];
            for (var i = 0; i < args.textures.Count; i++)
            {
                var texture = args.textures[i];
                textures[i] = texture == null
                    ? "null"
                    : $"{texture.width}x{texture.height} {texture.graphicsFormat}";
            }

            Debug.Log($"[Passthrough] Shader {shader.name}, supported {shader.isSupported}, " +
                      $"passes {m_CameraMaterial.passCount}, " +
                      $"keywords {(keywords.Length == 0 ? "none" : string.Join(" ", keywords))}, " +
                      $"{args.textures.Count} textures: {string.Join(", ", textures)}");
            Debug.Log($"[Passthrough] Texture centre reads {SampleTextureCentre()}, " +
                      $"display transform {m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(0)} " +
                      $"{m_CameraMaterial.GetMatrix(k_DisplayTransformId).GetRow(1)}");
        }

        // Draws the camera image into the texture the way ARCameraBackground draws the screen: a unit
        // quad through an orthographic projection.
        void DrawCameraImage()
        {
            if (m_Quad == null)
                m_Quad = CreateUnitQuad();
            m_Commands ??= new CommandBuffer { name = "Passthrough Camera Image" };
            m_Commands.Clear();
            m_Commands.SetRenderTarget(m_Texture);
            m_Commands.ClearRenderTarget(false, true, Color.black);
            m_Commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -0.1f, 9.9f));
            m_Commands.DrawMesh(m_Quad, Matrix4x4.identity, m_CameraMaterial);
            Graphics.ExecuteCommandBuffer(m_Commands);
            UpdateAmbientBlur(m_Texture);
        }

        // Generates a true, streak-free multi-pass separable Gaussian blur of the live camera frame
        // into compact 192x108 (soft bokeh) and 48x27 (velvety Ambilight) textures at ~30 Hz to keep
        // 60 fps ARCore passthrough completely stutter-free.
        void UpdateAmbientBlur(Texture source)
        {
            if (source == null || m_Screen == null || m_Screen.material == null ||
                (m_Rig != null && m_Rig.IsFullscreenBackdropActive))
                return;
            var now = Time.unscaledTime;
            if (m_AmbientTexture != null && now < m_NextAmbientBlurAt)
                return;
            m_NextAmbientBlurAt = now + (1f / 30f);

            if (m_BlurMaterial == null)
            {
                var shader = m_Screen.material.shader;
                if (shader == null || m_Screen.material.passCount < 3)
                    return;
                m_BlurMaterial = new Material(shader) { name = "Passthrough Ambient Blur" };
            }
            if (m_BlurMid == null)
            {
                m_BlurMid = new RenderTexture(192, 108, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Passthrough Blur Mid Temp",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_BlurMid.Create();
            }
            if (m_MidBlurTexture == null)
            {
                m_MidBlurTexture = new RenderTexture(192, 108, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Passthrough Mid Blur",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_MidBlurTexture.Create();
            }
            if (m_BlurTemp == null)
            {
                m_BlurTemp = new RenderTexture(48, 27, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Passthrough Blur Temp",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_BlurTemp.Create();
            }
            if (m_AmbientTexture == null)
            {
                m_AmbientTexture = new RenderTexture(48, 27, 0, RenderTextureFormat.ARGB32)
                {
                    name = "Passthrough Ambient",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                m_AmbientTexture.Create();
            }

            m_BlurCommands ??= new CommandBuffer { name = "Passthrough Ambient Blur" };
            m_BlurCommands.Clear();
            // Stage 1: Soft optical bokeh blur at 192x108 (smooth bridge from sharp 1:1 edge).
            m_BlurCommands.SetGlobalFloat(k_BlurStepId, 1.8f / 192f);
            m_BlurCommands.Blit(source, m_BlurMid, m_BlurMaterial, 1);
            m_BlurCommands.SetGlobalFloat(k_BlurStepId, 1.8f / 108f);
            m_BlurCommands.Blit(m_BlurMid, m_MidBlurTexture, m_BlurMaterial, 2);
            // Stage 2: Wide velvety Gaussian blur at 48x27 for creamy, streak-free Ambilight glow.
            m_BlurCommands.SetGlobalFloat(k_BlurStepId, 2.8f / 48f);
            m_BlurCommands.Blit(m_MidBlurTexture, m_BlurTemp, m_BlurMaterial, 1);
            m_BlurCommands.SetGlobalFloat(k_BlurStepId, 2.8f / 27f);
            m_BlurCommands.Blit(m_BlurTemp, m_AmbientTexture, m_BlurMaterial, 2);
            Graphics.ExecuteCommandBuffer(m_BlurCommands);

            var mat = m_Screen.material;
            mat.SetTexture(k_MidBlurMapId, m_MidBlurTexture);
            mat.SetTexture(k_AmbientMapId, m_AmbientTexture);
            mat.SetFloat(k_HasAmbientId, 1f);
        }

        static Mesh CreateUnitQuad()
        {
            var mesh = new Mesh { name = "Passthrough Quad" };
            mesh.SetVertices(new[]
            {
                new Vector3(0f, 0f, 0.1f), new Vector3(0f, 1f, 0.1f), new Vector3(1f, 1f, 0.1f), new Vector3(1f, 0f, 0.1f),
            });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return mesh;
        }

        void LateUpdate()
        {
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (m_Fusion.FollowPose != null)
                m_Fusion.FollowPose = null;
            if (!m_On)
            {
                if (m_BackgroundUltrawide && !sixDof)
                {
                    if (!m_UltrawideRunning && !m_UltrawidePending)
                    {
                        m_UltrawidePending = true;
                        m_UltrawideStartAt = Time.unscaledTime + k_UltrawideHandoffSeconds;
                        IsUltrawideActive = true;
                    }
                    if (m_UltrawidePending && Time.unscaledTime >= m_UltrawideStartAt)
                        StartUltrawide();
                    UpdateUltrawideFrame(updateScreen: false);
                }
                else if (m_UltrawideRunning || m_UltrawidePending)
                {
                    StopUltrawide();
                }

                if (m_ResumeWithSixDof && sixDof)
                {
                    m_ResumeWithSixDof = false;
                    SetPassthrough(true);
                }
                return;
            }

            if (m_PoseDriver != null && m_PoseDriver.enabled)
                m_PoseDriver.enabled = false;

            if (m_UseUltrawide)
            {
                if (sixDof)
                {
                    // A 6DoF scene (e.g. Air Paint / Cube Sandbox) wants ARCore: release Camera2 and use ARCore passthrough.
                    if (m_UltrawideRunning || m_UltrawidePending || IsUltrawideActive)
                        StopUltrawide();
                    SetArFrameSubscribed(true);
                    if (m_Texture != null && m_Screen != null && m_Screen.material.mainTexture != m_Texture)
                        m_Screen.material.mainTexture = m_Texture;
                    m_Fusion.EyesAtCamera = !m_Isolated;
                }
                else
                {
                    SetArFrameSubscribed(false);
                    m_Fusion.EyesAtCamera = false;
                    if (!m_UltrawideRunning && !m_UltrawidePending)
                    {
                        m_UltrawidePending = true;
                        m_UltrawideStartAt = Time.unscaledTime + k_UltrawideHandoffSeconds;
                        IsUltrawideActive = true;
                    }
                    if (m_UltrawidePending && Time.unscaledTime >= m_UltrawideStartAt)
                        StartUltrawide();
                    UpdateUltrawideFrame();
                }
            }
            else if (!sixDof)
            {
                // Switching to 3DoF turns ARCore, and so the camera, off.
                SetPassthrough(false);
                m_ResumeWithSixDof = true;
                return;
            }
            else
            {
                SetArFrameSubscribed(true);
                m_Fusion.EyesAtCamera = !m_Isolated;
            }

            ApplyPassthroughPose();
            if (!m_HasImage)
                return;

            // Eased in proportion, so moving from 2 m to 0.3 m takes as long as 0.3 m to 0.045 m would.
            var target = Mathf.Clamp(m_FocusDistance ?? m_Distance, k_MinDistance, m_Distance);
            var step = 1f - Mathf.Exp(-Time.unscaledDeltaTime / k_FocusSeconds);
            m_ShownDistance = Mathf.Exp(Mathf.Lerp(Mathf.Log(m_ShownDistance), Mathf.Log(target), step));
            var d = m_ShownDistance;
            var screen = m_Screen.transform;

            // Keep the exact 1:1 camera frustum at the center, while extending the quad outward with
            // _PadScale so Passthrough.shader paints a soft blurred live Ambilight border + vignette around it.
            var isMainCam = !m_UseUltrawide || sixDof;
            var padScale = isMainCam ? k_MainAmbientPadScale : k_UltrawideAmbientPadScale;
            if (m_Screen.material != null)
            {
                m_Screen.material.SetFloat(k_PadScaleId, padScale);
                m_Screen.material.SetFloat(k_VignetteStrengthId, isMainCam ? 0.92f : 0.28f);
            }

            screen.SetLocalPositionAndRotation(new Vector3(m_FrustumCenter.x * d, m_FrustumCenter.y * d, d), Quaternion.identity);
            screen.localScale = new Vector3(m_FrustumSize.x * d * padScale, m_FrustumSize.y * d * padScale, 1f);
        }
    }
}
