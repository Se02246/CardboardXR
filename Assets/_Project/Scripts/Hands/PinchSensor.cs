using System;
using System.Diagnostics;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace CardboardXR.Hands
{
    // A pinch as a click, for 3DoF, without hand tracking's cost. Where HandTracker places the hand
    // in 3D (which needs the AR session running, depth, and the landmarker on every image), this only
    // asks whether the thumb and index fingertips are touching, which the camera image alone answers:
    // their gap over the palm's width there, about 0.23 pinched and 1.8-2.1 open in the pinch study.
    // So it opens the rear camera directly at a low resolution and rate (no ARKit or ARCore), runs the
    // same MediaPipe models on those images (the palm detector only while no hand is followed), and a
    // pinch clicks whatever the gaze is on, as a screen tap does. A click wants ~100 ms, not the
    // dragging that made a slower landmarker feel laggy. A hand has to be seen in k_SteadyResults
    // results in a row before its pinch counts, so one passing through view doesn't click. It can't
    // run alongside the AR session, which holds the camera, so it stays off in 6DoF. Enable it to
    // start. Logs [Pinch] every five seconds, with what it costs.
    public class PinchSensor : MonoBehaviour
    {
        const int k_LandmarkerSize = 224;
        static readonly (string name, int index) k_BoxesOutput = ("Identity", 0);
        static readonly (string name, int index) k_ScoresOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_LandmarksOutput = ("Identity", 0);
        static readonly (string name, int index) k_PresenceOutput = ("Identity_1", 1);
        static readonly (string name, int index) k_HandednessOutput = ("Identity_2", 2);
        static readonly (string name, int index) k_MetricOutput = ("Identity_3", 3);
        // The image's fingertip gap over the palm's width decides the pinch (about 0.23 pinched and 1.8-2.1 open in the pinch study).
        public const float k_CloseRatio = 0.38f;
        public const float k_OpenRatio = 0.65f;
        public const float k_FastOpenRatio = 0.90f;
        public const float k_SustainedFastOpenRatio = 1.2f;
        public const float k_ArmOpenRatio = 0.65f;
        public const float k_DepthVetoRatio = 0.80f;
        // A hand must have been seen in more than this many results in a row before a pinch counts.
        public const int k_SteadyResults = 2;
        public const int k_CloseDebounceResults = 1;
        const int k_ConfirmHandFrames = 3;
        const float k_MinPalmSizeNorm = 0.052f;
        const float k_HoldHandPresence = 0.60f;
        const int k_OpenDebounceResults = 2;
        const float k_TrackInterval = 0.016f;
        const float k_PinchLossGraceSeconds = 0.15f;
        const float k_DragLossGraceSeconds = 0.30f;
        const float k_PredictionGain = 0.85f;
        const float k_MaxPredictionSeconds = 0.075f;
        const float k_MaxPredictOffsetFrac = 0.14f;
        const float k_PredictDeadbandMinPxPerSec = 14f;
        const float k_PredictDeadbandFullPxPerSec = 38f;
        const double k_CameraCaptureOffsetSec = 0.018;
        const float k_StatusSeconds = 5f;
        // No frame for this long, in seconds, and the camera is opened again: on Android it can come
        // up silent when the AR session has only just let go of it.
        const float k_NoFrameSeconds = 3f;

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] GazePointer m_Gaze;
        [SerializeField] ModelAsset m_Detector;
        [SerializeField] ModelAsset m_Landmarker;
        [Tooltip("The camera's requested size and rate. Lower costs less; the models see at most 640 wide anyway.")]
        [SerializeField] Vector2Int m_CameraSize = new(640, 480);
        [SerializeField] int m_CameraFps = 30;
        [Tooltip("While no hand is followed, the palm detector runs at most this often, in seconds.")]
        [SerializeField] float m_SearchInterval = 0.20f;
        [SerializeField, Range(0f, 1f)] float m_MinPalmScore = 0.62f;
        [SerializeField, Range(0f, 1f)] float m_MinHandPresence = 0.72f;

        WebCamTexture m_Camera;
        bool m_UsingUltrawideFeed;
        int m_LastUltrawideFrameId = -1;
        float m_NextUltrawideSampleAt;
        Worker m_DetectorWorker;
        Worker m_LandmarkerWorker;
        int m_BoxesIndex, m_ScoresIndex, m_LandmarksIndex, m_PresenceIndex, m_HandednessIndex, m_MetricIndex;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;
        float[] m_DetectorPixels;
        float[] m_LandmarkerPixels;
        float[] m_Boxes;
        float[] m_Scores;
        readonly float[] m_LandmarkValues = new float[HandGeometry.JointCount * 3];
        readonly float[] m_MetricValues = new float[HandGeometry.JointCount * 3];
        readonly Vector2[] m_ImageLandmarks = new Vector2[HandGeometry.JointCount];
        readonly Vector2[] m_SmoothedLandmarks = new Vector2[HandGeometry.JointCount];
        readonly Vector3[] m_MetricLandmarks = new Vector3[HandGeometry.JointCount];
        readonly OneEuroFilter[] m_LandmarkFilters = CreateLandmarkFilters();
        readonly OneEuroFilter m_DistanceFilter = new(1.2f, 3.0f);
        Vector2[] m_Anchors;
        Color32[] m_Pixels;
        // The frame the models are working on, and the newest one read since, which they swap in when
        // they're done (see Update and Step): reading the camera never waits for the models, nor
        // they for it. On a Pixel 9 Pro, taking turns left 2-5 frames a second analyzed.
        NativeArray<byte> m_Image;
        NativeArray<byte> m_NextImage;
        int m_Width, m_Height;
        int m_NextWidth, m_NextHeight;
        bool m_HasNextImage;
        bool m_ReadbackInFlight;
        bool m_Running;
        bool m_LoopActive;
        bool m_LoggedCamera;
        // Frames are read back from the GPU without stalling the main thread where the camera's pixel
        // format allows it (the Pixel's does); otherwise GetPixels32, which does stall (~4 ms on the
        // Pixel, 0.3-0.9 ms on the iPhone). The iPhone's camera texture is B8G8R8A8_SRGB, which can't
        // be read back: Unity logs an error per frame but doesn't flag the request, and hands blank
        // frames to the models, so pinches silently stopped working. Hence asking first.
        bool m_AsyncReadback = true;
        bool m_CheckedFormat;
        float m_LastFrameTime;

        readonly PinchRule m_Rule = new();
        readonly HandTurnGate m_Turn = new();
        HandRegion? m_Region;
        int m_ConfirmedFrames;
        float m_NextSearch;
        float m_NextTrack;
        float m_SmoothedMetric = 1.5f;
        bool m_HasSmoothedMetric;
        int m_OpenStreak;
        float m_LostSince = -1f;
        bool m_PinchLostGraceActive;
        Vector2 m_LastPalmCenterPx;
        Vector2 m_PalmVelocityPx;
        double m_ResultTimeSec;
        double m_NextImageCaptureTimeSec;
        double m_ImageCaptureTimeSec;
        double m_ResultCaptureTimeSec;
        bool m_HasVelocity;

        // Statistics for the status line.
        int m_Reads, m_Frames, m_Detections, m_Landmarks, m_Losses, m_Pinches;
        double m_ReadMs, m_DetectMs, m_LandmarkMs;
        float m_MinRatio = float.PositiveInfinity, m_MaxRatio;
        float m_StatusStart;

        public bool IsPinched => m_Rule.IsPinched;
        public bool SeesHand => m_Region != null && m_ConfirmedFrames >= k_ConfirmHandFrames;
        public float CurrentPinchMetric => m_HasSmoothedMetric ? m_SmoothedMetric : 1.5f;
        public float EstimatedHandDistance { get; private set; } = 0.42f;
        public float HorizontalFovDeg => m_UsingUltrawideFeed ? PassthroughController.UltrawideHorizontalFovDeg : 64f;
        public float VerticalFovDeg => m_UsingUltrawideFeed ? PassthroughController.UltrawideVerticalFovDeg : 50f;
        public Vector2 PinchPositionNormalized { get; private set; }

        // The latest result's landmarks in the camera image, in pixels (x right, y down, upright on the
        // screen), the image's size, and the chance it's a right hand: for which way the palm faces
        // (PalmGesture) and where the hand is.
        public System.Collections.Generic.IReadOnlyList<Vector2> ImageLandmarks => m_ImageLandmarks;
        public System.Collections.Generic.IReadOnlyList<Vector2> SmoothedImageLandmarks => m_SmoothedLandmarks;
        public System.Collections.Generic.IReadOnlyList<Vector3> MetricLandmarks => m_MetricLandmarks;
        public Vector2 ImageSize => new(m_Width, m_Height);
        public float RightHandScore { get; private set; } = 0.5f;

        static OneEuroFilter[] CreateLandmarkFilters()
        {
            var filters = new OneEuroFilter[HandGeometry.JointCount];
            for (var i = 0; i < filters.Length; i++)
                filters[i] = new OneEuroFilter(3.0f, 40f, 2.5f);
            return filters;
        }

        static bool PreferUltrawideFeed
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return PassthroughController.IsUltrawideActive;
#endif
            }
        }

        void OnEnable()
        {
            if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
            {
                Debug.LogWarning("[Pinch] Not starting in 6DoF: the AR session has the camera. Hand tracking covers pinches there");
                enabled = false;
                return;
            }
            if (PreferUltrawideFeed)
                PassthroughController.SetBackgroundUltrawide(true);
            m_Running = true;
            m_StatusStart = Time.unscaledTime;
            if (!m_LoopActive)
                RunLoop();
        }

        void OnDisable()
        {
            m_Running = false;
            PassthroughController.SetBackgroundUltrawide(false);
            // Now, not when the loop next wakes: the AR session may be about to open the camera.
            if (m_Camera != null)
                m_Camera.Stop();
            if (m_Rule.IsPinched)
            {
                m_Rule.Reset();
                PinchRouter.End();
            }
            m_Region = null;
            m_ConfirmedFrames = 0;
            m_HasSmoothedMetric = false;
            m_HasVelocity = false;
            m_PalmVelocityPx = Vector2.zero;
            m_OpenStreak = 0;
            m_LostSince = -1f;
            m_PinchLostGraceActive = false;
            foreach (var f in m_LandmarkFilters)
                f.Reset();
            m_DistanceFilter.Reset();
        }

        async void RunLoop()
        {
            m_LoopActive = true;
            try
            {
                m_UsingUltrawideFeed = PreferUltrawideFeed;
                if (m_UsingUltrawideFeed)
                    PassthroughController.SetBackgroundUltrawide(true);
                else if (!StartCamera())
                    return;
                Setup();
                Debug.Log(m_UsingUltrawideFeed
                    ? "[Pinch] On: sharing 110 deg Ultrawide camera feed (dual-scale search + 3D/2D hybrid pinch)"
                    : $"[Pinch] On: rear camera asked for {m_CameraSize.x}x{m_CameraSize.y} at {m_CameraFps} fps; " +
                      $"a pinch closes below {k_CloseRatio:F2} and opens above {k_OpenRatio:F2}");
                while (m_Running)
                {
                    // Also leaves if something turned 6DoF on, which wants the camera.
                    if (m_Fusion != null && m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof)
                    {
                        Debug.Log("[Pinch] Off: 6DoF wants the camera");
                        enabled = false;
                        break;
                    }
                    // Seamlessly switch between shared Ultrawide feed and WebCamTexture if mode changes.
                    var wantUltrawide = PreferUltrawideFeed;
                    if (wantUltrawide != m_UsingUltrawideFeed)
                    {
                        m_UsingUltrawideFeed = wantUltrawide;
                        m_LoggedCamera = false;
                        m_LastUltrawideFrameId = -1;
                        if (m_UsingUltrawideFeed)
                        {
                            PassthroughController.SetBackgroundUltrawide(true);
                            if (m_Camera != null)
                            {
                                m_Camera.Stop();
                                Destroy(m_Camera);
                                m_Camera = null;
                            }
                            Debug.Log("[Pinch] Switched to shared 110 deg Ultrawide feed");
                        }
                        else if (!StartCamera())
                        {
                            break;
                        }
                    }
                    await Step();
                    if (m_Running)
                        await Awaitable.NextFrameAsync();
                    LogStatus();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                Teardown();
                m_LoopActive = false;
                Debug.Log("[Pinch] Off");
            }
        }

        bool StartCamera()
        {
            string name = null;
            foreach (var device in WebCamTexture.devices)
            {
                if (!device.isFrontFacing)
                {
                    name = device.name;
                    break;
                }
            }
            if (name == null)
            {
                Debug.LogWarning("[Pinch] No rear camera");
                enabled = false;
                return false;
            }
            m_Camera = new WebCamTexture(name, m_CameraSize.x, m_CameraSize.y, m_CameraFps);
            m_Camera.Play();
            m_LastFrameTime = Time.unscaledTime;
            m_CheckedFormat = false;
            return true;
        }

        void Setup()
        {
            m_Anchors = PalmDecoder.CreateAnchors();
            var detector = ModelLoader.Load(m_Detector);
            var landmarker = ModelLoader.Load(m_Landmarker);
            m_BoxesIndex = OutputIndex(detector, k_BoxesOutput);
            m_ScoresIndex = OutputIndex(detector, k_ScoresOutput);
            m_LandmarksIndex = OutputIndex(landmarker, k_LandmarksOutput);
            m_PresenceIndex = OutputIndex(landmarker, k_PresenceOutput);
            m_HandednessIndex = OutputIndex(landmarker, k_HandednessOutput);
            m_MetricIndex = OutputIndex(landmarker, k_MetricOutput);
            m_DetectorWorker = new Worker(detector, BackendType.CPU);
            m_LandmarkerWorker = new Worker(landmarker, BackendType.CPU);
            m_DetectorInput = new Tensor<float>(new TensorShape(1, PalmDecoder.InputSize, PalmDecoder.InputSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerSize, k_LandmarkerSize, 3));
            m_DetectorPixels = new float[PalmDecoder.InputSize * PalmDecoder.InputSize * 3];
            m_LandmarkerPixels = new float[k_LandmarkerSize * k_LandmarkerSize * 3];
            m_Boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            m_Scores = new float[PalmDecoder.AnchorCount];
        }

        void Teardown()
        {
            if (m_Camera != null)
            {
                m_Camera.Stop();
                Destroy(m_Camera);
                m_Camera = null;
            }
            m_DetectorWorker?.Dispose();
            m_LandmarkerWorker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            m_DetectorWorker = m_LandmarkerWorker = null;
            m_DetectorInput = m_LandmarkerInput = null;
            if (m_Image.IsCreated)
                m_Image.Dispose();
            if (m_NextImage.IsCreated)
                m_NextImage.Dispose();
            m_HasNextImage = false;
            m_ReadbackInFlight = false;
        }

        static int OutputIndex(Model model, (string name, int index) output)
        {
            for (var i = 0; i < model.outputs.Count; i++)
            {
                if (model.outputs[i].name == output.name)
                    return i;
            }
            return output.index;
        }

        // Runs the newest camera frame through the models. False if there was no new frame, or it
        // isn't time to look for a hand yet.
        async Awaitable<bool> Step()
        {
            if (m_UsingUltrawideFeed)
                TryConsumeUltrawideTexture(PassthroughController.UltrawideTexture);
            if (!m_HasNextImage)
                return false;
            if (m_Region == null && Time.unscaledTime < m_NextSearch)
                return false;
            if (m_Region != null && Time.unscaledTime < m_NextTrack)
                return false;

            // The newest frame becomes the one worked on; the old buffer takes the next read.
            (m_Image, m_NextImage) = (m_NextImage, m_Image);
            m_Width = m_NextWidth;
            m_Height = m_NextHeight;
            m_ImageCaptureTimeSec = m_NextImageCaptureTimeSec;
            m_HasNextImage = false;
            m_Frames++;
            var stopwatch = Stopwatch.StartNew();

            if (m_Region == null)
            {
                stopwatch.Restart();
                var letterbox = Letterbox.Fit(m_Width, m_Height, PalmDecoder.InputSize);
                ImageSampler.SampleLetterbox(m_Image, m_Width, m_Height, letterbox, m_DetectorPixels);
                m_DetectorInput.Upload(m_DetectorPixels);
                m_DetectorWorker.Schedule(m_DetectorInput);
                using (var scores = await (m_DetectorWorker.PeekOutput(m_ScoresIndex) as Tensor<float>).ReadbackAndCloneAsync())
                    CopyTo(scores, m_Scores);
                using (var boxes = (m_DetectorWorker.PeekOutput(m_BoxesIndex) as Tensor<float>).ReadbackAndClone())
                    CopyTo(boxes, m_Boxes);
                m_DetectMs += stopwatch.Elapsed.TotalMilliseconds;
                m_Detections++;
                if (!m_Running)
                    return true;
                if (!PalmDecoder.TryDecodeNearestUserPalm(m_Boxes, m_Scores, m_Anchors, m_MinPalmScore, k_MinPalmSizeNorm, out var palm))
                {
                    m_NextSearch = Time.unscaledTime + m_SearchInterval;
                    TickLostGrace();
                    return true;
                }
                m_Region = HandRegion.FromPalm(palm, letterbox);
                m_ConfirmedFrames = 0;
            }

            stopwatch.Restart();
            var region = m_Region.Value;
            ImageSampler.SampleRegion(m_Image, m_Width, m_Height, region, k_LandmarkerSize, m_LandmarkerPixels);
            m_LandmarkerInput.Upload(m_LandmarkerPixels);
            m_LandmarkerWorker.Schedule(m_LandmarkerInput);
            m_NextTrack = Time.unscaledTime + k_TrackInterval;
            using (var landmarks = await (m_LandmarkerWorker.PeekOutput(m_LandmarksIndex) as Tensor<float>).ReadbackAndCloneAsync())
                CopyTo(landmarks, m_LandmarkValues);
            float presence, handedness;
            using (var output = (m_LandmarkerWorker.PeekOutput(m_PresenceIndex) as Tensor<float>).ReadbackAndClone())
                presence = output[0];
            using (var output = (m_LandmarkerWorker.PeekOutput(m_HandednessIndex) as Tensor<float>).ReadbackAndClone())
                handedness = output[0];
            using (var metric = (m_LandmarkerWorker.PeekOutput(m_MetricIndex) as Tensor<float>).ReadbackAndClone())
                CopyTo(metric, m_MetricValues);
            // The model may give a probability or a logit.
            RightHandScore = handedness < 0f || handedness > 1f ? PalmDecoder.Sigmoid(handedness) : handedness;
            m_LandmarkMs += stopwatch.Elapsed.TotalMilliseconds;
            m_Landmarks++;
            if (!m_Running)
                return true;

            if (presence < 0f || presence > 1f)
                presence = PalmDecoder.Sigmoid(presence);
            var acquiring = m_ConfirmedFrames < k_ConfirmHandFrames;
            if (presence < (acquiring ? m_MinHandPresence : k_HoldHandPresence))
            {
                Lose();
                return true;
            }

            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var crop = new Vector2(m_LandmarkValues[i * 3], m_LandmarkValues[i * 3 + 1]) / k_LandmarkerSize;
                m_ImageLandmarks[i] = region.CropToImage(crop);
            }
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var across = HandRegion.Rotate(new Vector2(m_MetricValues[i * 3], m_MetricValues[i * 3 + 1]), region.rotation);
                m_MetricLandmarks[i] = new Vector3(across.x, across.y, m_MetricValues[i * 3 + 2]);
            }

            var hFov = HorizontalFovDeg;
            var focalPx = (m_Width * 0.5f) / Mathf.Tan(hFov * 0.5f * Mathf.Deg2Rad);
            var spanPxRaw = HandGeometry.PalmSpan(m_ImageLandmarks);
            var unclampedDist = spanPxRaw > 1f ? 0.058f * focalPx / spanPxRaw : 10f;
            if (!HandGeometry.IsPlausibleHand(m_ImageLandmarks, m_MetricLandmarks, m_Width, m_Height) ||
                !HandGeometry.IsLikelyOwnHand(m_ImageLandmarks, unclampedDist, m_Height, acquiring))
            {
                Lose();
                return true;
            }

            m_ConfirmedFrames = Mathf.Min(m_ConfirmedFrames + 1, k_ConfirmHandFrames);

            var nowSec = Time.unscaledTimeAsDouble;
            var sampleTimeSec = m_ImageCaptureTimeSec > 0 ? m_ImageCaptureTimeSec : nowSec;
            var invW = 1f / Mathf.Max(1, m_Width);
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var filtered = m_LandmarkFilters[i].Filter(m_ImageLandmarks[i] * invW, sampleTimeSec);
                m_SmoothedLandmarks[i] = new Vector2(filtered.x, filtered.y) * m_Width;
            }
            m_Region = HandRegion.FromLandmarks(m_ImageLandmarks);

            if (m_ConfirmedFrames < k_ConfirmHandFrames)
            {
                if (m_Rule.IsPinched)
                {
                    var earlyMetric = ComputeImagePinchRatio(m_ImageLandmarks);
                    if (ShouldReleasePinch(earlyMetric, ref m_OpenStreak))
                    {
                        m_PinchLostGraceActive = false;
                        m_LostSince = -1f;
                        m_SmoothedMetric = earlyMetric;
                        m_HasSmoothedMetric = true;
                        if (m_Rule.Update(true, earlyMetric) == PinchRule.Change.Opened)
                            PinchRouter.End();
                    }
                }
                return true;
            }
            m_LostSince = -1f;
            m_PinchLostGraceActive = false;

            var palmCenterPx = (m_SmoothedLandmarks[0] + m_SmoothedLandmarks[5] + m_SmoothedLandmarks[9] +
                                m_SmoothedLandmarks[13] + m_SmoothedLandmarks[17]) * 0.2f;
            var velDt = sampleTimeSec - m_ResultCaptureTimeSec;
            if (m_HasVelocity && velDt > 0.004 && velDt <= 0.2)
                m_PalmVelocityPx = Vector2.Lerp(m_PalmVelocityPx, (palmCenterPx - m_LastPalmCenterPx) / (float)velDt, 0.6f);
            else
                m_PalmVelocityPx = Vector2.zero;
            m_LastPalmCenterPx = palmCenterPx;
            m_ResultTimeSec = nowSec;
            m_ResultCaptureTimeSec = sampleTimeSec;
            m_HasVelocity = true;

            var rawMetric = ComputePinchMetric(m_ImageLandmarks, m_MetricLandmarks);
            var ratio3D = ComputeShapePinchRatio(m_MetricLandmarks);
            m_SmoothedMetric = !m_HasSmoothedMetric || rawMetric >= m_SmoothedMetric
                ? rawMetric
                : Mathf.Lerp(m_SmoothedMetric, rawMetric, 0.78f);
            m_HasSmoothedMetric = true;

            var metricRatio = m_SmoothedMetric;
            m_MinRatio = Mathf.Min(m_MinRatio, metricRatio);
            m_MaxRatio = Mathf.Max(m_MaxRatio, metricRatio);

            // Estimate physical hand distance & true angular pinch midpoint for smooth scrolling and 2D contour.
            if (m_Width > 1 && m_Height > 1)
            {
                var vFov = VerticalFovDeg;
                var spanPx = Mathf.Max(12f, HandGeometry.PalmSpan(m_SmoothedLandmarks));
                var rawDist = Mathf.Clamp(0.058f * focalPx / spanPx, 0.24f, 0.78f);
                EstimatedHandDistance = m_DistanceFilter.Filter(new Vector3(rawDist, 0f, 0f), sampleTimeSec).x;

                var tipMid = (m_SmoothedLandmarks[HandGeometry.ThumbTip] + m_SmoothedLandmarks[HandGeometry.IndexTip]) * 0.5f;
                var hScale = 2f * Mathf.Tan(hFov * 0.5f * Mathf.Deg2Rad);
                var vScale = 2f * Mathf.Tan(vFov * 0.5f * Mathf.Deg2Rad);
                PinchPositionNormalized = new Vector2(
                    (tipMid.x / m_Width - 0.5f) * hScale,
                    (0.5f - tipMid.y / m_Height) * vScale);
            }

            // No pinch starts while the hand turns over (HandTurnGate).
            var turning = m_Turn.Update(PalmGesture.Facing(m_ImageLandmarks[PalmGesture.k_Wrist],
                m_ImageLandmarks[PalmGesture.k_IndexKnuckle], m_ImageLandmarks[PalmGesture.k_PinkyKnuckle], RightHandScore >= 0.5f),
                Time.unscaledTime);
            if (turning && !m_Rule.IsPinched && metricRatio < k_CloseRatio)
            {
                Debug.Log($"[Pinch] Fingertips {metricRatio:F2} palm widths apart while the hand turned over; not a pinch");
                metricRatio = k_CloseRatio;
            }

            // Release immediately on clear separation and debounce only borderline releases.
            var ruleInput = metricRatio;
            if (m_Rule.IsPinched)
            {
                var releaseMetric = ComputeImagePinchRatio(m_ImageLandmarks);
                var release = ShouldReleasePinch(releaseMetric, ref m_OpenStreak, VisualPinchScroller.IsDragging);
                ruleInput = release ? Mathf.Max(releaseMetric, k_OpenRatio + 0.05f) : Mathf.Min(metricRatio, k_OpenRatio);
            }
            else
            {
                m_OpenStreak = 0;
            }

            var change = m_Rule.Update(true, ruleInput);
            if (change == PinchRule.Change.Closed)
            {
                m_Pinches++;
                Debug.Log($"[Pinch] Pinched (metric={metricRatio:F2}, 3D={ratio3D:F2})");
                if (TryGetPinchRay(out var pinchRay))
                    VisualPinchScroller.SyncPinchRay(pinchRay);
                PinchRouter.Begin(PinchSource.Camera, m_Gaze, false);
            }
            else if (change == PinchRule.Change.Opened)
            {
                PinchRouter.End();
            }
            return true;
        }

        // Image gap decides the pinch, with a 3D depth-separation veto so overlapping-in-2D fingers
        // that are far apart in depth don't count as pinched.
        public static float ComputeImagePinchRatio(Vector2[] imageLandmarks)
        {
            var palmRef2D = Mathf.Max(
                HandGeometry.PalmSpan(imageLandmarks),
                Vector2.Distance(imageLandmarks[0], imageLandmarks[9]) * 0.62f);
            return Vector2.Distance(imageLandmarks[HandGeometry.ThumbTip], imageLandmarks[HandGeometry.IndexTip]) /
                   Mathf.Max(1f, palmRef2D);
        }

        public static float ComputeShapePinchRatio(Vector3[] metricLandmarks)
        {
            var palmSize3D = HandGeometry.PalmSize(metricLandmarks);
            if (palmSize3D <= 0.025f)
                return 0f;
            return Vector3.Distance(metricLandmarks[HandGeometry.ThumbTip], metricLandmarks[HandGeometry.IndexTip]) / palmSize3D;
        }

        public static float ComputePinchMetric(Vector2[] imageLandmarks, Vector3[] metricLandmarks)
        {
            var r2 = ComputeImagePinchRatio(imageLandmarks);
            var r3 = ComputeShapePinchRatio(metricLandmarks);
            return r3 >= k_DepthVetoRatio ? Mathf.Max(r2, k_OpenRatio) : r2;
        }

        public static bool ShouldReleasePinch(float rawMetric, ref int openStreak, bool sustained = false)
        {
            var fastRatio = sustained ? k_SustainedFastOpenRatio : k_FastOpenRatio;
            if (rawMetric >= fastRatio)
            {
                openStreak = 0;
                return true;
            }
            if (rawMetric > k_OpenRatio)
            {
                if (++openStreak >= k_OpenDebounceResults)
                {
                    openStreak = 0;
                    return true;
                }
                return false;
            }
            openStreak = 0;
            return false;
        }

        public static Vector2 PredictOffset(Vector2 velocityPx, float ageSeconds, float maxOffsetPx)
        {
            if (ageSeconds <= 0f || maxOffsetPx <= 0f)
                return Vector2.zero;
            var speed = velocityPx.magnitude;
            if (speed <= k_PredictDeadbandMinPxPerSec)
                return Vector2.zero;
            var scale = Mathf.Clamp01((speed - k_PredictDeadbandMinPxPerSec) /
                                      (k_PredictDeadbandFullPxPerSec - k_PredictDeadbandMinPxPerSec));
            var lead = Mathf.Clamp(ageSeconds, 0f, k_MaxPredictionSeconds) * k_PredictionGain;
            return Vector2.ClampMagnitude(velocityPx * (scale * lead), maxOffsetPx);
        }

        public bool FillPredictedImageLandmarks(Vector2[] dest)
        {
            if (dest == null || dest.Length < HandGeometry.JointCount || m_Width <= 1 || m_Height <= 1)
                return false;
            var ageRef = m_ResultCaptureTimeSec > 0 ? m_ResultCaptureTimeSec : m_ResultTimeSec;
            var offset = m_HasVelocity
                ? PredictOffset(m_PalmVelocityPx, (float)(Time.unscaledTimeAsDouble - ageRef), m_Width * k_MaxPredictOffsetFrac)
                : Vector2.zero;
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var p = m_SmoothedLandmarks[i] + offset;
                dest[i] = new Vector2(Mathf.Clamp(p.x, 0f, m_Width), Mathf.Clamp(p.y, 0f, m_Height));
            }
            return true;
        }

        public bool TryGetPinchRay(out Ray ray)
        {
            ray = default;
            if (!SeesHand || m_Fusion == null || m_Width <= 1 || m_Height <= 1)
                return false;
            var ageRef = m_ResultCaptureTimeSec > 0 ? m_ResultCaptureTimeSec : m_ResultTimeSec;
            var offset = m_HasVelocity
                ? PredictOffset(m_PalmVelocityPx, (float)(Time.unscaledTimeAsDouble - ageRef), m_Width * k_MaxPredictOffsetFrac)
                : Vector2.zero;
            var tipMid = ((m_SmoothedLandmarks[HandGeometry.ThumbTip] + m_SmoothedLandmarks[HandGeometry.IndexTip]) * 0.5f) + offset;
            tipMid.x = Mathf.Clamp(tipMid.x, 0f, m_Width);
            tipMid.y = Mathf.Clamp(tipMid.y, 0f, m_Height);
            var frac = new Vector2(tipMid.x / m_Width, tipMid.y / m_Height);
            var localDir = PalmGesture.Ray(frac, HorizontalFovDeg, VerticalFovDeg);
            var head = m_Fusion.transform;
            ray = new Ray(head.position, head.rotation * localDir);
            return true;
        }

        void TickLostGrace()
        {
            var grace = VisualPinchScroller.IsActive ? k_DragLossGraceSeconds : k_PinchLossGraceSeconds;
            if (m_PinchLostGraceActive && Time.unscaledTime - m_LostSince >= grace)
            {
                m_PinchLostGraceActive = false;
                m_LostSince = -1f;
                if (m_Rule.Update(false, 0f) == PinchRule.Change.Opened)
                    PinchRouter.End();
            }
        }

        void Lose()
        {
            if (m_Region != null && m_ConfirmedFrames >= k_ConfirmHandFrames)
                m_Losses++;
            m_Region = null;
            m_ConfirmedFrames = 0;
            m_HasVelocity = false;
            m_PalmVelocityPx = Vector2.zero;
            m_Turn.Reset();
            m_OpenStreak = 0;
            m_HasSmoothedMetric = false;
            foreach (var f in m_LandmarkFilters)
                f.Reset();
            m_DistanceFilter.Reset();

            if (m_Rule.IsPinched)
            {
                if (m_LostSince < 0f)
                    m_LostSince = Time.unscaledTime;
                m_PinchLostGraceActive = true;
            }
            else
            {
                m_PinchLostGraceActive = false;
                m_LostSince = -1f;
                m_Rule.Update(false, 0f);
            }
            m_NextSearch = Time.unscaledTime + (m_Rule.IsPinched ? 0f : m_SearchInterval);
        }

        // Reads each new camera frame into m_NextImage as RGBA with the top row first, as the
        // samplers expect, turned upright on the screen, while the models work on m_Image. At most
        // one read back from the GPU at a time; a newer frame replaces one not yet taken.
        void Update()
        {
            if (!m_Running)
                return;

            TickLostGrace();

            // In Ultrawide 0.5x mode, Step() pulls the latest frame from PassthroughController's
            // CPU-backed RGBA32 Texture2D right when starting inference.
            if (m_UsingUltrawideFeed)
                return;

            if (m_Camera == null)
                return;
            // Stopped by OnDisable and enabled again before the loop noticed, or silent.
            if (!m_Camera.isPlaying || Time.unscaledTime - m_LastFrameTime > k_NoFrameSeconds)
            {
                if (m_Camera.isPlaying)
                {
                    Debug.Log($"[Pinch] No camera frames for {k_NoFrameSeconds:F0} s: opening the camera again");
                    m_Camera.Stop();
                }
                m_Camera.Play();
                m_LastFrameTime = Time.unscaledTime;
                return;
            }
            if (!m_Camera.didUpdateThisFrame || m_Camera.width < 32)
                return;
            m_LastFrameTime = Time.unscaledTime;

            if (!m_CheckedFormat)
            {
                m_CheckedFormat = true;
                var format = m_Camera.graphicsFormat;
                m_AsyncReadback = SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels);
                Debug.Log($"[Pinch] Camera texture {format}: " +
                          (m_AsyncReadback ? "read back without waiting" : "can't be read back, so read with GetPixels32"));
            }

            var stopwatch = Stopwatch.StartNew();
            if (m_AsyncReadback)
            {
                if (m_ReadbackInFlight)
                    return;
                m_ReadbackInFlight = true;
                AsyncGPUReadback.Request(m_Camera, 0, TextureFormat.RGBA32, OnReadback);
            }
            else
            {
                var width = m_Camera.width;
                var height = m_Camera.height;
                if (m_Pixels == null || m_Pixels.Length != width * height)
                    m_Pixels = new Color32[width * height];
                m_Camera.GetPixels32(m_Pixels);
                using (var pixels = new NativeArray<Color32>(m_Pixels, Allocator.Temp))
                    TakeFrame(pixels, width, height, "read with GetPixels32");
            }
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        void TryConsumeUltrawideTexture(Texture2D tex)
        {
            var frameId = PassthroughController.UltrawideFrameId;
            var now = Time.unscaledTime;
            var nextDue = m_Region != null ? m_NextTrack : m_NextUltrawideSampleAt;
            if (tex == null || frameId == m_LastUltrawideFrameId || now < nextDue)
                return;
            m_LastUltrawideFrameId = frameId;
            if (m_Region == null)
                m_NextUltrawideSampleAt = now + 0.16f;
            var raw = tex.GetRawTextureData<Color32>();
            if (raw.Length == tex.width * tex.height)
                TakeFrame(raw, tex.width, tex.height, "shared ultrawide CPU buffer", turned: false);
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            m_ReadbackInFlight = false;
            if (!m_Running || m_Camera == null)
                return;
            if (request.hasError)
            {
                Debug.LogWarning("[Pinch] The GPU couldn't read the camera frame back; reading it the slower way from now on");
                m_AsyncReadback = false;
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            TakeFrame(request.GetData<Color32>(), request.width, request.height, "read back without waiting");
            m_ReadMs += stopwatch.Elapsed.TotalMilliseconds;
        }

        public static int ComputeDownsampleStride(int width) => width > 1280 ? 2 : 1;

        void TakeFrame(NativeArray<Color32> source, int width, int height, string how, bool? turned = null)
        {
            var stride = ComputeDownsampleStride(width);
            var outW = Mathf.Max(1, width / stride);
            var outH = Mathf.Max(1, height / stride);
            if (!m_NextImage.IsCreated || m_NextImage.Length != outW * outH * 4)
            {
                if (m_NextImage.IsCreated)
                    m_NextImage.Dispose();
                m_NextImage = new NativeArray<byte>(outW * outH * 4, Allocator.Persistent);
            }
            CopyRows(source, m_NextImage, width, height, stride, outW, outH, turned);
            m_NextWidth = outW;
            m_NextHeight = outH;
            m_NextImageCaptureTimeSec = Time.unscaledTimeAsDouble - k_CameraCaptureOffsetSec;
            m_HasNextImage = true;
            m_Reads++;
            LogCamera(outW, outH, how);
        }

        // Color32 is laid out as RGBA bytes, which is what the samplers read, so rows copy cleanly.
        // Upside down on the screen when the camera sensor's rotation is 180.
        void CopyRows(NativeArray<Color32> source, NativeArray<byte> target, int srcW, int srcH,
            int stride, int dstW, int dstH, bool? turnedOverride = null)
        {
            var turned = turnedOverride ?? (m_Camera != null && Mathf.RoundToInt(m_Camera.videoRotationAngle) % 360 == 180);
            var flipRows = !turned;
            var pixels = target.Reinterpret<Color32>(1);
            var dstSpan = pixels.AsSpan();
            if (stride == 1 && dstW == srcW && dstH == srcH)
            {
                for (var y = 0; y < srcH; y++)
                {
                    var row = (flipRows ? srcH - 1 - y : y) * srcW;
                    NativeArray<Color32>.Copy(source, row, pixels, y * srcW, srcW);
                    if (!turned)
                        continue;
                    for (int left = y * srcW, right = left + srcW - 1; left < right; left++, right--)
                        (dstSpan[left], dstSpan[right]) = (dstSpan[right], dstSpan[left]);
                }
                return;
            }

            var srcSpan = source.AsReadOnlySpan();
            var maxSrcY = srcH - 1;
            var maxSrcX = srcW - 1;
            for (var y = 0; y < dstH; y++)
            {
                var sy = (flipRows ? (dstH - 1 - y) : y) * stride;
                if (sy > maxSrcY) sy = maxSrcY;
                var srcRow = sy * srcW;
                var dstRow = y * dstW;
                if (turned)
                {
                    for (var x = 0; x < dstW; x++)
                    {
                        var sx = (dstW - 1 - x) * stride;
                        if (sx > maxSrcX) sx = maxSrcX;
                        dstSpan[dstRow + x] = srcSpan[srcRow + sx];
                    }
                }
                else
                {
                    for (var x = 0; x < dstW; x++)
                    {
                        var sx = x * stride;
                        if (sx > maxSrcX) sx = maxSrcX;
                        dstSpan[dstRow + x] = srcSpan[srcRow + sx];
                    }
                }
            }
        }

        void LogCamera(int width, int height, string how)
        {
            if (m_LoggedCamera)
                return;
            m_LoggedCamera = true;
            if (m_UsingUltrawideFeed)
                Debug.Log($"[Pinch] Shared ultrawide gives {width}x{height} ({PassthroughController.UltrawideHorizontalFovDeg:F0}x{PassthroughController.UltrawideVerticalFovDeg:F0} deg); frames {how}");
            else if (m_Camera != null)
                Debug.Log($"[Pinch] Camera gives {width}x{height}, turned {m_Camera.videoRotationAngle:F0} deg, " +
                          $"mirrored {m_Camera.videoVerticallyMirrored}; frames {how}");
        }

        static void CopyTo(Tensor<float> tensor, float[] values)
        {
            var count = Mathf.Min(values.Length, tensor.shape.length);
            for (var i = 0; i < count; i++)
                values[i] = tensor[i];
        }

        // What it cost, main thread and models, so it can be judged against a video playing.
        void LogStatus()
        {
            var seconds = Time.unscaledTime - m_StatusStart;
            if (seconds < k_StatusSeconds)
                return;
            Debug.Log($"[Pinch] {m_Reads / seconds:F1} frames/s read ({m_ReadMs / Mathf.Max(1, m_Reads):F1} ms each on the main thread), " +
                      $"{m_Frames / seconds:F1} analyzed, " +
                      $"detector {m_Detections} runs at {m_DetectMs / Mathf.Max(1, m_Detections):F0} ms, " +
                      $"landmarker {m_Landmarks} runs at {m_LandmarkMs / Mathf.Max(1, m_Landmarks):F0} ms (including waits); " +
                      $"hand {(SeesHand ? "in view" : "not in view")}, {m_Losses} lost, {m_Pinches} pinches, " +
                      (float.IsPositiveInfinity(m_MinRatio) ? "no fingertips seen" : $"fingertip gap {m_MinRatio:F2}-{m_MaxRatio:F2} palm widths"));
            m_Reads = m_Frames = m_Detections = m_Landmarks = m_Losses = m_Pinches = 0;
            m_ReadMs = m_DetectMs = m_LandmarkMs = 0;
            m_MinRatio = float.PositiveInfinity;
            m_MaxRatio = 0f;
            m_StatusStart = Time.unscaledTime;
        }
    }

    // When the fingertip gap makes a pinch: only after the fingers have been seen open (>= k_ArmOpenRatio),
    // steady for more than k_SteadyResults, and touching (< k_CloseRatio) for k_CloseDebounceResults results in a row.
    public class PinchRule
    {
        public enum Change
        {
            None,
            Closed,
            Opened,
        }

        int m_Seen;
        bool m_SeenOpen;
        int m_CloseStreak;

        public bool IsPinched { get; private set; }

        // One result: whether a hand was seen, and if so its fingertip gap in palm widths.
        public Change Update(bool handSeen, float ratio)
        {
            if (!handSeen)
            {
                m_Seen = 0;
                m_SeenOpen = false;
                m_CloseStreak = 0;
                var was = IsPinched;
                IsPinched = false;
                return was ? Change.Opened : Change.None;
            }
            m_Seen++;
            if (!IsPinched)
            {
                if (ratio >= PinchSensor.k_ArmOpenRatio)
                {
                    m_SeenOpen = true;
                    m_CloseStreak = 0;
                }
                else if (m_SeenOpen && m_Seen > PinchSensor.k_SteadyResults && ratio < PinchSensor.k_CloseRatio)
                {
                    if (++m_CloseStreak >= PinchSensor.k_CloseDebounceResults)
                    {
                        IsPinched = true;
                        m_CloseStreak = 0;
                        return Change.Closed;
                    }
                }
                else if (ratio >= PinchSensor.k_CloseRatio)
                {
                    m_CloseStreak = 0;
                }
                return Change.None;
            }
            if (ratio > PinchSensor.k_OpenRatio)
            {
                IsPinched = false;
                m_CloseStreak = 0;
                return Change.Opened;
            }
            return Change.None;
        }

        public void Reset()
        {
            m_Seen = 0;
            m_SeenOpen = false;
            m_CloseStreak = 0;
            IsPinched = false;
        }
    }
}
