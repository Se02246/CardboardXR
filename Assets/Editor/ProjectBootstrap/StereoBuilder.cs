using CardboardXR.Demos;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using Unity.InferenceEngine;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

namespace ProjectBootstrap
{
    // Builds the app headlessly: the Core scene, which holds the stereo rig, head tracking, AR,
    // hands, gaze and the gaze panel, followed by the lobby and the demos (see DemoScenes), which load
    // on top of it. Core is generated from scratch on every build; the lobby and demos are not.
    public static class StereoBuilder
    {
        const string ScenesFolder = "Assets/_Project/Scenes";
        const string GeneratedFolder = "Assets/_Project/Stereo/Generated";
        const string CoreScene = ScenesFolder + "/Core.unity";
        const string AndroidApk = "Builds/CardboardXR.apk";
        // Xcode projects are folders, and Xcode builds and signs them from there.
        const string XcodeProject = "Builds/iOS";
        const string DistortionShaderPath = "Assets/_Project/Shaders/Distortion.shader";
        const string ReticleShaderPath = "Assets/_Project/Shaders/Reticle.shader";
        const string PlacementReticleShaderPath = "Assets/_Project/Shaders/PlacementReticle.shader";
        const string BoundaryShaderPath = "Assets/_Project/Shaders/Boundary.shader";
        const string PassthroughShaderPath = "Assets/_Project/Shaders/Passthrough.shader";
        const string ShadowCatcherShaderPath = "Assets/_Project/Shaders/ShadowCatcher.shader";
        const string EnvironmentDomeShaderPath = "Assets/_Project/Shaders/EnvironmentDome.shader";
        const string HandSilhouetteShaderPath = "Assets/_Project/Shaders/HandSilhouette.shader";
        const string LitShaderName = "Universal Render Pipeline/Simple Lit";
        // Shadows only matter near the user, on the surfaces in reach; a short distance keeps the
        // shadow map's texels small.
        const float k_ShadowDistance = 4f;
        // MediaPipe's full models. The lite ones (palm_detection_lite and hand_landmark_lite) cost
        // about a third as much, but tracked and pinched too poorly and saw hands that weren't there.
        const string HandDetectorPath = "Assets/_Project/Hands/Models/hand_detector.onnx";
        const string HandLandmarkerPath = "Assets/_Project/Hands/Models/hand_landmarks_detector.onnx";
        const string UnlitShaderName = "Universal Render Pipeline/Unlit";

        // Detected surfaces render on their own layer, so the AR debug view can draw only them.
        const int BoundaryLayer = 30;
        // The passthrough screen, which the AR camera must not render into its own texture.
        const int PassthroughLayer = 29;

        // Invoke with: -executeMethod ProjectBootstrap.StereoBuilder.BuildAndroid
        public static void BuildAndroid()
        {
            BuildAndExit(BuildScenes(), AndroidApk, BuildTarget.Android);
        }

        // The same scenes as an Xcode project for the iPhone, which Xcode then builds and signs.
        // Invoke with: -buildTarget iOS -executeMethod ProjectBootstrap.StereoBuilder.BuildIos
        public static void BuildIos()
        {
            BuildAndExit(BuildScenes(), XcodeProject, BuildTarget.iOS);
        }

        // Regenerates Core.unity and generated prefabs/materials in batchmode without an APK build.
        // Invoke with: -buildTarget Android -executeMethod ProjectBootstrap.StereoBuilder.RebuildScenes
        public static void RebuildScenes()
        {
            BuildScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[StereoBuilder] Core scene and generated assets rebuilt.");
            EditorApplication.Exit(0);
        }

        // A release build unless CARDBOARDXR_DEVELOPMENT=1: a development build is debuggable on
        // Android and lets the Unity Profiler connect over the local network. CARDBOARDXR_BUNDLE_ID
        // builds under another app id, since an iOS bundle id belongs to the Apple team that first
        // signed it. The first scene is the one the app starts in.
        static void BuildAndExit(string[] scenePaths, string outputPath, BuildTarget target)
        {
            var bundleId = System.Environment.GetEnvironmentVariable("CARDBOARDXR_BUNDLE_ID");
            if (!string.IsNullOrWhiteSpace(bundleId))
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target)),
                    bundleId.Trim());
                Debug.Log($"[Build] App id {bundleId.Trim()}");
            }
            var development = System.Environment.GetEnvironmentVariable("CARDBOARDXR_DEVELOPMENT") == "1";
            var fast = System.Environment.GetEnvironmentVariable("CARDBOARDXR_FAST") == "1";
            var version = PlayerSettings.bundleVersion;
            var versionCode = PlayerSettings.Android.bundleVersionCode;
            var namedTarget = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            var prevCodeGen = PlayerSettings.GetIl2CppCodeGeneration(namedTarget);
            if (!ApplyRelease(target))
            {
                EditorApplication.Exit(1);
                return;
            }
            if (fast)
                PlayerSettings.SetIl2CppCodeGeneration(namedTarget, Il2CppCodeGeneration.OptimizeSize);
            var buildOptions = BuildOptions.None;
            if (development || fast)
                buildOptions |= BuildOptions.Development;
            if (fast)
                buildOptions |= BuildOptions.CompressWithLz4;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenePaths,
                locationPathName = outputPath,
                target = target,
                options = buildOptions,
            });
            if (fast)
                PlayerSettings.SetIl2CppCodeGeneration(namedTarget, prevCodeGen);
            // Not left in ProjectSettings, so local builds go back to Unity's debug key.
            PlayerSettings.bundleVersion = version;
            PlayerSettings.Android.bundleVersionCode = versionCode;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.Android.keystoreName = "";
            PlayerSettings.Android.keyaliasName = "";

            var summary = report.summary;
            Debug.Log($"[Build] {(development ? "Development build" : "Build")} {summary.result}: {outputPath} " +
                      $"({summary.totalSize / 1e6:F1} MB, {summary.totalTime}, {summary.totalErrors} errors)");
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        // For published builds (Tools/release_android.sh). CARDBOARDXR_VERSION (1.2.3) sets the version
        // and, on Android, a version code that rises with it (10203), so updates install over older
        // releases. CARDBOARDXR_KEYSTORE signs the APK with that keystore instead of Unity's debug key,
        // which differs on every machine; its password comes in CARDBOARDXR_KEYSTORE_PASS (also the key's,
        // as in a PKCS12 keystore) and the key's alias in CARDBOARDXR_KEY_ALIAS, by default cardboardxr.
        // False if they're set wrong, rather than shipping a build signed with the wrong key.
        static bool ApplyRelease(BuildTarget target)
        {
            var version = System.Environment.GetEnvironmentVariable("CARDBOARDXR_VERSION");
            if (!string.IsNullOrWhiteSpace(version))
            {
                var parts = version.Trim().Split('.');
                if (parts.Length != 3 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
                    !int.TryParse(parts[2], out var patch) || minor > 99 || patch > 99)
                {
                    Debug.LogError($"[Build] CARDBOARDXR_VERSION {version} isn't major.minor.patch (minor and patch under 100)");
                    return false;
                }
                PlayerSettings.bundleVersion = version.Trim();
                PlayerSettings.Android.bundleVersionCode = major * 10000 + minor * 100 + patch;
                Debug.Log($"[Build] Version {PlayerSettings.bundleVersion} ({PlayerSettings.Android.bundleVersionCode})");
            }

            var keystore = System.Environment.GetEnvironmentVariable("CARDBOARDXR_KEYSTORE");
            if (target != BuildTarget.Android || string.IsNullOrWhiteSpace(keystore))
                return true;
            var password = System.Environment.GetEnvironmentVariable("CARDBOARDXR_KEYSTORE_PASS");
            if (!System.IO.File.Exists(keystore) || string.IsNullOrEmpty(password))
            {
                Debug.LogError($"[Build] Keystore {keystore} missing, or CARDBOARDXR_KEYSTORE_PASS unset");
                return false;
            }
            var alias = System.Environment.GetEnvironmentVariable("CARDBOARDXR_KEY_ALIAS");
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystore;
            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasName = string.IsNullOrWhiteSpace(alias) ? "cardboardxr" : alias.Trim();
            PlayerSettings.Android.keyaliasPass = password;
            Debug.Log($"[Build] Signing with {System.IO.Path.GetFileName(keystore)}, key {PlayerSettings.Android.keyaliasName}");
            return true;
        }

        // Core first, since it's the scene the app starts in, then the lobby and the demos.
        internal static string[] BuildScenes()
        {
            var fast = System.Environment.GetEnvironmentVariable("CARDBOARDXR_FAST") == "1";
            var rebuildScenes = System.Environment.GetEnvironmentVariable("CARDBOARDXR_REBUILD_SCENES") == "1";
            if (fast && !rebuildScenes && System.IO.File.Exists(CoreScene))
            {
                var existingCatalog = AssetDatabase.LoadAssetAtPath<DemoCatalog>(DemoScenes.CatalogPath);
                if (existingCatalog != null && !string.IsNullOrEmpty(existingCatalog.LobbyScenePath) &&
                    System.IO.File.Exists(existingCatalog.LobbyScenePath))
                {
                    var allExist = true;
                    var cached = new System.Collections.Generic.List<string> { CoreScene, existingCatalog.LobbyScenePath };
                    foreach (var demo in existingCatalog.Demos)
                    {
                        if (string.IsNullOrEmpty(demo.ScenePath) || !System.IO.File.Exists(demo.ScenePath))
                        {
                            allExist = false;
                            break;
                        }
                        cached.Add(demo.ScenePath);
                    }
                    if (allExist)
                    {
                        Debug.Log($"[StereoBuilder] Fast build: reusing {cached.Count} existing scenes (skipping scene rebuild)");
                        return cached.ToArray();
                    }
                }
            }

            MaterialAssets.Ensure();
            if (AssetDatabase.IsValidFolder("Assets/_Project/Resources/Environments"))
            {
                AssetDatabase.ImportAsset("Assets/_Project/Resources/Environments",
                    ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
            }
            if (!DemoScenes.EnsureAll())
            {
                Debug.LogError("[StereoBuilder] A demo's setup is wrong (see the [DemoScenes] errors above).");
                EditorApplication.Exit(1);
                return new[] { CoreScene };
            }
            var catalog = BuildCoreScene();
            if (catalog == null)
                return new[] { CoreScene };
            var scenes = new System.Collections.Generic.List<string> { CoreScene, catalog.LobbyScenePath };
            foreach (var demo in catalog.Demos)
                scenes.Add(demo.ScenePath);
            return scenes.ToArray();
        }

        static DemoCatalog BuildCoreScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            ConfigureShadows();

            // Load assets after NewScene: opening a scene in Single mode unloads assets loaded before it.
            var distortionShader = AssetDatabase.LoadAssetAtPath<Shader>(DistortionShaderPath);
            var reticleShader = AssetDatabase.LoadAssetAtPath<Shader>(ReticleShaderPath);
            var placementReticleShader = AssetDatabase.LoadAssetAtPath<Shader>(PlacementReticleShaderPath);
            var boundaryShader = AssetDatabase.LoadAssetAtPath<Shader>(BoundaryShaderPath);
            var passthroughShader = AssetDatabase.LoadAssetAtPath<Shader>(PassthroughShaderPath);
            var environmentDomeShader = AssetDatabase.LoadAssetAtPath<Shader>(EnvironmentDomeShaderPath);
            var handSilhouetteShader = AssetDatabase.LoadAssetAtPath<Shader>(HandSilhouetteShaderPath);
            var unlit = Shader.Find(UnlitShaderName);
            var catalog = AssetDatabase.LoadAssetAtPath<DemoCatalog>(DemoScenes.CatalogPath);
            if (catalog == null)
            {
                Debug.LogError($"[StereoBuilder] Missing the demo catalog at {DemoScenes.CatalogPath}.");
                EditorApplication.Exit(1);
                return null;
            }
            if (distortionShader == null || reticleShader == null || placementReticleShader == null || boundaryShader == null ||
                passthroughShader == null || environmentDomeShader == null || handSilhouetteShader == null || unlit == null)
            {
                Debug.LogError($"[StereoBuilder] Missing shader: {DistortionShaderPath}, {ReticleShaderPath}, " +
                               $"{PlacementReticleShaderPath}, {BoundaryShaderPath}, {PassthroughShaderPath}, {EnvironmentDomeShaderPath}, " +
                               $"{HandSilhouetteShaderPath} or {UnlitShaderName}.");
                EditorApplication.Exit(1);
                return null;
            }

            EnsureFolder(ScenesFolder);
            EnsureFolder(GeneratedFolder);

            var head = new GameObject("Head");
            var rig = head.AddComponent<StereoRig>();
            var serializedRig = new SerializedObject(rig);
            serializedRig.FindProperty("m_DistortionShader").objectReferenceValue = distortionShader;
            serializedRig.ApplyModifiedPropertiesWithoutUndo();
            var tracker = head.AddComponent<GyroHeadTracker>();
            head.AddComponent<FrameTimingLog>();
            head.AddComponent<PerformanceLog>();
            var (poseSource, origin, arCamera, arViewOnly) = BuildViewerSetup(rig);
            var fusion = head.AddComponent<HeadPoseFusion>();
            var passthrough = origin.AddComponent<PassthroughController>();
            var environment = origin.AddComponent<EnvironmentManager>();
            var torch = origin.AddComponent<TorchManager>();
            origin.AddComponent<SystemBridge>();
            var demosObject = new GameObject("Demos");
            var demos = demosObject.AddComponent<DemoManager>();
            var onboarding = demosObject.AddComponent<ShizukuOnboardingController>();
            var mediaWindow = demosObject.AddComponent<MediaControllerWindow>();
            var notifOverlay = demosObject.AddComponent<NotificationOverlayController>();
            var notifCenter = demosObject.AddComponent<NotificationCenterWindow>();
            var (toolbar, panel, clockSlot, clockText, batteryText, notifBadge, notifBadgeText) =
                BuildToolbar(environment, torch, demos, mediaWindow, notifCenter);
            var envToggle = toolbar[1];
            var envNext = toolbar[3];
            var torchToggle = toolbar[4];
            var serializedFusion = new SerializedObject(fusion);
            serializedFusion.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedFusion.FindProperty("m_ArPose").objectReferenceValue = poseSource;
            serializedFusion.ApplyModifiedPropertiesWithoutUndo();
            BuildGaze(head, fusion, reticleShader);
            BuildFade(head, demos);
            var boundary = BuildRoomBoundary(origin, fusion, boundaryShader);
            // Shown only inside apps that actually use surface scanning (e.g. Cube Sandbox).
            var scanButton = ToolbarItem(panel, "Scan", "radar", "Scan Room", boundary.ToggleScan);
            toolbar[2] = scanButton;
            var serializedBoundary = new SerializedObject(boundary);
            serializedBoundary.FindProperty("m_ScanButton").objectReferenceValue = scanButton;
            serializedBoundary.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serializedBoundary.ApplyModifiedPropertiesWithoutUndo();
            BuildAnchors(origin, fusion, unlit);
            BuildSpatialLocator(origin, fusion, boundary, arCamera, placementReticleShader);
            BuildHands(origin, fusion, poseSource, arCamera, passthrough, unlit, handSilhouetteShader);
            ArrangeToolbar(panel, clockSlot, toolbar);
            var settings = BuildSettings(origin, fusion, panel, clockText, batteryText, toolbar[6], toolbar[7], notifBadge, notifBadgeText);
            BuildLibrary(panel, demos, settings, toolbar[0], toolbar[5], catalog);
            BuildArDebugView(origin, rig, fusion, boundary, arCamera, arViewOnly);
            BuildPassthrough(passthrough, fusion, rig, arCamera, null, passthroughShader);
            BuildEnvironment(environment, fusion, passthrough, envToggle, envNext, environmentDomeShader);
            BuildTorch(torch, passthrough, torchToggle);
            BuildLighting(origin, fusion, passthrough, arCamera);
            BuildDemos(demosObject, demos, catalog, fusion, rig, passthrough, boundary, origin, settings,
                onboarding, mediaWindow, notifOverlay, notifCenter);

            EditorSceneManager.SaveScene(scene, CoreScene);
            return catalog;
        }

        // An AR session and camera, off in the scene: ArPoseSource runs them for 6DoF tracking, and
        // ViewerSetupController for QR scanning. Images are read on the CPU, so the AR camera itself
        // never renders.
        static (ArPoseSource poseSource, GameObject origin, Camera arCamera, Behaviour[] arViewOnly) BuildViewerSetup(StereoRig rig)
        {
            var sessionObject = new GameObject("AR Session");
            var session = sessionObject.AddComponent<ARSession>();
            session.enabled = false;
            var inputManager = sessionObject.AddComponent<ARInputManager>();
            inputManager.enabled = false;

            // ARPlaneManager needs an XR Origin. The origin needs its own camera-offset object:
            // without one it moves itself, and RoomBoundary drives its transform.
            var origin = new GameObject("XR Origin").AddComponent<XROrigin>();
            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(origin.transform, false);
            origin.CameraFloorOffsetObject = cameraOffset;
            // No added height: the AR camera sits at the device's session pose, like the planes.
            origin.CameraYOffset = 0f;

            var arCameraObject = new GameObject("AR Camera");
            arCameraObject.transform.SetParent(cameraOffset.transform, false);
            var arCamera = arCameraObject.AddComponent<Camera>();
            arCamera.enabled = false;
            origin.Camera = arCamera;
            var cameraManager = arCameraObject.AddComponent<ARCameraManager>();
            cameraManager.enabled = false;

            // For ArDebugView: the camera feed, the device's pose, and only the detected surfaces.
            arCamera.cullingMask = 1 << BoundaryLayer;
            arCamera.nearClipPlane = 0.1f;
            arCamera.farClipPlane = 20f;
            var background = arCameraObject.AddComponent<ARCameraBackground>();
            background.enabled = false;
            // Environment depth for the hand tracker, which turns this on where a depth sensor
            // provides it. Raw rather than temporally smoothed, which would lag a moving hand, and no
            // occlusion: passthrough draws the camera image itself, and mustn't change.
            var occlusion = arCameraObject.AddComponent<AROcclusionManager>();
            occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
            occlusion.environmentDepthTemporalSmoothingRequested = false;
            occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
            occlusion.requestedHumanDepthMode = HumanSegmentationDepthMode.Disabled;
            occlusion.requestedHumanStencilMode = HumanSegmentationStencilMode.Disabled;
            occlusion.enabled = false;
            var poseDriver = arCameraObject.AddComponent<TrackedPoseDriver>();
            poseDriver.positionInput = new InputActionProperty(
                new InputAction("Position", binding: "<HandheldARInputDevice>/devicePosition", expectedControlType: "Vector3"));
            poseDriver.rotationInput = new InputActionProperty(
                new InputAction("Rotation", binding: "<HandheldARInputDevice>/deviceRotation", expectedControlType: "Quaternion"));
            poseDriver.enabled = false;

            var poseSource = arCameraObject.AddComponent<ArPoseSource>();
            var serializedPose = new SerializedObject(poseSource);
            serializedPose.FindProperty("m_Session").objectReferenceValue = session;
            serializedPose.FindProperty("m_CameraManager").objectReferenceValue = cameraManager;
            serializedPose.FindProperty("m_InputManager").objectReferenceValue = inputManager;
            serializedPose.ApplyModifiedPropertiesWithoutUndo();
            // HeadPoseFusion turns it on in 6DoF.
            poseSource.enabled = false;
            var scanner = arCameraObject.AddComponent<ViewerQrScanner>();
            scanner.enabled = false;
            var serializedScanner = new SerializedObject(scanner);
            serializedScanner.FindProperty("m_CameraManager").objectReferenceValue = cameraManager;
            serializedScanner.ApplyModifiedPropertiesWithoutUndo();

            var setup = new GameObject("Viewer Setup").AddComponent<ViewerSetupController>();
            var serializedSetup = new SerializedObject(setup);
            serializedSetup.FindProperty("m_Rig").objectReferenceValue = rig;
            serializedSetup.FindProperty("m_Session").objectReferenceValue = session;
            serializedSetup.FindProperty("m_CameraManager").objectReferenceValue = cameraManager;
            serializedSetup.FindProperty("m_Scanner").objectReferenceValue = scanner;
            serializedSetup.ApplyModifiedPropertiesWithoutUndo();
            return (poseSource, origin.gameObject, arCamera, new Behaviour[] { background, poseDriver });
        }

        // What the lobby and demo scenes reach Core through, and what loads them.
        static void BuildDemos(GameObject demosObject, DemoManager demos, DemoCatalog catalog, HeadPoseFusion fusion,
            StereoRig rig, PassthroughController passthrough, RoomBoundary boundary, GameObject origin, SettingsSummoner settings,
            ShizukuOnboardingController onboarding, MediaControllerWindow mediaWindow,
            NotificationOverlayController notifOverlay, NotificationCenterWindow notifCenter)
        {
            var core = demosObject.AddComponent<CoreServices>();
            var serializedCore = new SerializedObject(core);
            serializedCore.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedCore.FindProperty("m_Rig").objectReferenceValue = rig;
            serializedCore.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serializedCore.FindProperty("m_Boundary").objectReferenceValue = boundary;
            serializedCore.FindProperty("m_Placer").objectReferenceValue = origin.GetComponent<AnchorPlacer>();
            serializedCore.FindProperty("m_Gaze").objectReferenceValue = fusion.GetComponent<GazePointer>();
            serializedCore.FindProperty("m_HandTracker").objectReferenceValue = origin.GetComponent<HandTracker>();
            serializedCore.FindProperty("m_PinchInput").objectReferenceValue = origin.GetComponent<PinchInput>();
            serializedCore.FindProperty("m_HandRig").objectReferenceValue = origin.GetComponent<HandInteractorRig>();
            serializedCore.FindProperty("m_Demos").objectReferenceValue = demos;
            serializedCore.FindProperty("m_Settings").objectReferenceValue = settings;
            serializedCore.ApplyModifiedPropertiesWithoutUndo();

            var serializedDemos = new SerializedObject(demos);
            serializedDemos.FindProperty("m_Catalog").objectReferenceValue = catalog;
            serializedDemos.FindProperty("m_Core").objectReferenceValue = core;
            serializedDemos.ApplyModifiedPropertiesWithoutUndo();

            var serializedOnboarding = new SerializedObject(onboarding);
            serializedOnboarding.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedOnboarding.FindProperty("m_Settings").objectReferenceValue = settings;
            serializedOnboarding.ApplyModifiedPropertiesWithoutUndo();

            var serializedMedia = new SerializedObject(mediaWindow);
            serializedMedia.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedMedia.FindProperty("m_Demos").objectReferenceValue = demos;
            serializedMedia.ApplyModifiedPropertiesWithoutUndo();

            var serializedOverlay = new SerializedObject(notifOverlay);
            serializedOverlay.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedOverlay.FindProperty("m_Demos").objectReferenceValue = demos;
            serializedOverlay.FindProperty("m_Gaze").objectReferenceValue = fusion.GetComponent<GazePointer>();
            serializedOverlay.ApplyModifiedPropertiesWithoutUndo();

            var serializedNotifCenter = new SerializedObject(notifCenter);
            serializedNotifCenter.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedNotifCenter.FindProperty("m_Demos").objectReferenceValue = demos;
            serializedNotifCenter.ApplyModifiedPropertiesWithoutUndo();

            // The lobby opens Shizuku onboarding on first launch, then opens the app library.
            var flow = demosObject.AddComponent<LobbyFlow>();
            var serializedFlow = new SerializedObject(flow);
            serializedFlow.FindProperty("m_Core").objectReferenceValue = core;
            serializedFlow.FindProperty("m_Library").objectReferenceValue = demosObject.GetComponent<AppLibrary>();
            serializedFlow.FindProperty("m_ShizukuOnboarding").objectReferenceValue = onboarding;
            serializedFlow.ApplyModifiedPropertiesWithoutUndo();
        }

        // Plane detection for 6DoF: RoomBoundary lines the virtual floor up with the real one and
        // shows a grid on real surfaces within reach. The planes' box colliders let the gaze and the
        // hand's ray place objects on them.
        static RoomBoundary BuildRoomBoundary(GameObject origin, HeadPoseFusion fusion, Shader boundaryShader)
        {
            var material = SaveAsset(new Material(boundaryShader) { name = "Boundary" }, $"{GeneratedFolder}/Boundary.mat");
            var planeObject = new GameObject("Boundary Plane", typeof(ARPlane), typeof(SurfaceMesh),
                typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider), typeof(PlaneCollider), typeof(PlacementSurface));
            planeObject.layer = BoundaryLayer;
            var planeRenderer = planeObject.GetComponent<MeshRenderer>();
            // Two passes over the one mesh: shadows darkening the camera image, then the marks.
            var catcher = SaveAsset(new Material(AssetDatabase.LoadAssetAtPath<Shader>(ShadowCatcherShaderPath)) { name = "Shadow Catcher" },
                $"{GeneratedFolder}/Shadow Catcher.mat");
            planeRenderer.sharedMaterials = new[] { catcher, material };
            planeRenderer.shadowCastingMode = ShadowCastingMode.Off;
            planeRenderer.receiveShadows = false;
            var prefabPath = $"{GeneratedFolder}/Boundary Plane.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(planeObject, prefabPath);
            Object.DestroyImmediate(planeObject);

            var planes = origin.AddComponent<ARPlaneManager>();
            planes.planePrefab = prefab;
            planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            // RoomBoundary turns it on in 6DoF.
            planes.enabled = false;

            var boundary = origin.AddComponent<RoomBoundary>();
            var serializedBoundary = new SerializedObject(boundary);
            serializedBoundary.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedBoundary.FindProperty("m_Planes").objectReferenceValue = planes;
            serializedBoundary.ApplyModifiedPropertiesWithoutUndo();
            return boundary;
        }

        // Tap-to-place on detected surfaces: a 10 cm cube, anchored with ARCore where the gaze meets
        // the surface. It lightens under the gaze, and tapping it removes it.
        static void BuildAnchors(GameObject origin, HeadPoseFusion fusion, Shader unlit)
        {
            // Lit, by the room's light in passthrough (PassthroughLighting), and casting shadows onto
            // the surfaces it's on.
            var material = CreateMaterial("Placed Cube", Shader.Find(LitShaderName), new Color(1f, 0.75f, 0.2f), null);
            material.SetFloat("_Smoothness", 0.3f);
            var cube = CreatePrimitive(PrimitiveType.Cube, "Placed Cube", null, material, keepCollider: true);
            cube.transform.localScale = Vector3.one * 0.1f;
            var cubeRenderer = cube.GetComponent<MeshRenderer>();
            cubeRenderer.shadowCastingMode = ShadowCastingMode.On;
            cubeRenderer.receiveShadows = true;
            cube.AddComponent<GazeHighlight>();
            cube.AddComponent<PlacedObject>();
            // Real physics while free: it falls, bounces off other cubes and lands on detected surfaces;
            // held, XRI carries it as a kinematic body that still knocks free cubes aside, and opening
            // the fingers throws it. CubePhysics freezes it instead if the hand is lost, and anchors it
            // where it comes to rest.
            var body = cube.AddComponent<Rigidbody>();
            body.isKinematic = false;
            body.useGravity = true;
            body.mass = 0.3f;
            body.angularDamping = 0.3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            // Thin surface colliders and quick throws: no passing through.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            var bounce = SaveAsset(new PhysicsMaterial("Cube") { bounciness = 0.2f, dynamicFriction = 0.5f, staticFriction = 0.6f },
                $"{GeneratedFolder}/Cube.physicMaterial");
            cube.GetComponent<BoxCollider>().sharedMaterial = bounce;
            var grab = cube.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Kinematic;
            grab.trackRotation = false;
            grab.useDynamicAttach = true;
            grab.throwOnDetach = true;
            // Stronger than the hand's measured speed, over a short window: tracking at ~20-30 Hz smooths a
            // flick, and throws at 1x and 0.15 s carried cubes only 6-49 cm.
            grab.throwSmoothingDuration = 0.1f;
            grab.throwVelocityScale = 1.8f;
            grab.throwAngularVelocityScale = 0.5f;
            cube.AddComponent<CubePhysics>();
            // XRI carries it; this turns it with the hand.
            cube.AddComponent<RotateWithHand>();
            // Turns it cyan while the hand could take it, since passthrough can't show how near it is.
            cube.AddComponent<GrabCue>();
            var prefabPath = $"{GeneratedFolder}/Placed Cube.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            var prefab = PrefabUtility.SaveAsPrefabAsset(cube, prefabPath);
            Object.DestroyImmediate(cube);

            var anchors = origin.AddComponent<ARAnchorManager>();
            // AnchorPlacer turns it on in 6DoF.
            anchors.enabled = false;
            var placer = origin.AddComponent<AnchorPlacer>();
            var serialized = new SerializedObject(placer);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Anchors").objectReferenceValue = anchors;
            serialized.FindProperty("m_ObjectPrefab").objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Hybrid Depth + ARRaycast + Plane surface locator and 3D placement reticle for Sandbox and Portal.
        static void BuildSpatialLocator(GameObject origin, HeadPoseFusion fusion, RoomBoundary boundary,
            Camera arCamera, Shader placementReticleShader)
        {
            var reticleMaterial = SaveAsset(new Material(placementReticleShader) { name = "Placement Reticle" },
                $"{GeneratedFolder}/Placement Reticle.mat");

            var raycasts = origin.AddComponent<ARRaycastManager>();
            raycasts.enabled = false;

            var locator = origin.AddComponent<SpatialSurfaceLocator>();
            var serialized = new SerializedObject(locator);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Boundary").objectReferenceValue = boundary;
            serialized.FindProperty("m_Placer").objectReferenceValue = origin.GetComponent<AnchorPlacer>();
            serialized.FindProperty("m_Raycasts").objectReferenceValue = raycasts;
            serialized.FindProperty("m_Planes").objectReferenceValue = origin.GetComponent<ARPlaneManager>();
            serialized.FindProperty("m_Occlusion").objectReferenceValue = arCamera.GetComponent<AROcclusionManager>();
            serialized.FindProperty("m_ReticleMaterial").objectReferenceValue = reticleMaterial;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Camera-based hand tracking in 6DoF, shown as dots on the joints. In passthrough the camera
        // image moves to the tracked hand's distance.
        static void BuildHands(GameObject origin, HeadPoseFusion fusion, ArPoseSource poseSource, Camera arCamera,
            PassthroughController passthrough, Shader unlit, Shader handSilhouetteShader)
        {
            var detector = AssetDatabase.LoadAssetAtPath<ModelAsset>(HandDetectorPath);
            var landmarker = AssetDatabase.LoadAssetAtPath<ModelAsset>(HandLandmarkerPath);
            if (detector == null || landmarker == null)
                Debug.LogError($"[StereoBuilder] Missing hand model: {HandDetectorPath} or {HandLandmarkerPath}.");

            var tracker = origin.AddComponent<HandTracker>();
            var serializedTracker = new SerializedObject(tracker);
            serializedTracker.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedTracker.FindProperty("m_PoseSource").objectReferenceValue = poseSource;
            serializedTracker.FindProperty("m_CameraManager").objectReferenceValue = arCamera.GetComponent<ARCameraManager>();
            serializedTracker.FindProperty("m_Occlusion").objectReferenceValue = arCamera.GetComponent<AROcclusionManager>();
            serializedTracker.FindProperty("m_Room").objectReferenceValue = origin.GetComponent<RoomBoundary>();
            serializedTracker.FindProperty("m_Detector").objectReferenceValue = detector;
            serializedTracker.FindProperty("m_Landmarker").objectReferenceValue = landmarker;
            serializedTracker.ApplyModifiedPropertiesWithoutUndo();

            // The tracked hand as an XR Hands hand, with its pinch and pointing ray.
            var gaze = Object.FindAnyObjectByType<GazePointer>();
            var trackingSpace = origin.GetComponent<XROrigin>().CameraFloorOffsetObject.transform;
            var feed = origin.AddComponent<HandSubsystemFeed>();
            var serializedFeed = new SerializedObject(feed);
            serializedFeed.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedFeed.FindProperty("m_TrackingSpace").objectReferenceValue = trackingSpace;
            serializedFeed.FindProperty("m_Eyes").objectReferenceValue = gaze.transform;
            serializedFeed.ApplyModifiedPropertiesWithoutUndo();

            // The gaze aims and a pinch clicks; the hand's own ray picks things up.
            var (rig, interactor) = BuildHandInteraction(origin, trackingSpace, tracker, feed, gaze);

            // A dot on whatever the hand could take, so it's clear before pinching.
            var rayView = new GameObject("Hand Ray").AddComponent<HandRayView>();
            var serializedRay = new SerializedObject(rayView);
            serializedRay.FindProperty("m_Rig").objectReferenceValue = rig;
            serializedRay.FindProperty("m_Interactor").objectReferenceValue = interactor;
            serializedRay.FindProperty("m_Material").objectReferenceValue = CreateMaterial("Hand Ray", unlit, Color.white, null);
            serializedRay.ApplyModifiedPropertiesWithoutUndo();

            // The "pinchStudy" launch option's prompted run for tuning pinch detection.
            var study = origin.AddComponent<PinchStudy>();
            var serializedStudy = new SerializedObject(study);
            serializedStudy.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedStudy.FindProperty("m_HandInput").objectReferenceValue = rig;
            serializedStudy.FindProperty("m_Head").objectReferenceValue = fusion.transform;
            serializedStudy.ApplyModifiedPropertiesWithoutUndo();

            // Pinch-to-click in 3DoF / Ultrawide from the camera feed.
            var pinchSensor = origin.AddComponent<PinchSensor>();
            var serializedPinch = new SerializedObject(pinchSensor);
            serializedPinch.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedPinch.FindProperty("m_Gaze").objectReferenceValue = gaze;
            serializedPinch.FindProperty("m_Detector").objectReferenceValue = detector;
            serializedPinch.FindProperty("m_Landmarker").objectReferenceValue = landmarker;
            serializedPinch.ApplyModifiedPropertiesWithoutUndo();
            pinchSensor.enabled = false;

            var silhouetteMat = SaveAsset(new Material(handSilhouetteShader) { name = "Hand Silhouette" },
                $"{GeneratedFolder}/Hand Silhouette.mat");
            var view = new GameObject("Hand Joints").AddComponent<HandJointsView>();
            var serializedView = new SerializedObject(view);
            serializedView.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedView.FindProperty("m_Feed").objectReferenceValue = feed;
            serializedView.FindProperty("m_Sensor").objectReferenceValue = pinchSensor;
            serializedView.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedView.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serializedView.FindProperty("m_Material").objectReferenceValue = silhouetteMat;
            serializedView.ApplyModifiedPropertiesWithoutUndo();
            var pinchInput = origin.AddComponent<PinchInput>();
            var serializedInput = new SerializedObject(pinchInput);
            serializedInput.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedInput.FindProperty("m_Sensor").objectReferenceValue = pinchSensor;
            serializedInput.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedInput.FindProperty("m_Gaze").objectReferenceValue = gaze;
            serializedInput.FindProperty("m_HandHint").objectReferenceValue = BuildHandHint(fusion.transform);
            serializedInput.ApplyModifiedPropertiesWithoutUndo();

            var focus = origin.AddComponent<PassthroughHandFocus>();
            var serializedFocus = new SerializedObject(focus);
            serializedFocus.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedFocus.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serializedFocus.ApplyModifiedPropertiesWithoutUndo();
        }

        // Picking things up with the tracked hand through the XR Interaction Toolkit: an interactor
        // whose sphere and ray both work from a point between the fingertips, which HandInteractorRig
        // keeps there and aims along the line from the eyes, with the pinch as its select. Clicking
        // doesn't go through XRI at all — the gaze pointer does that, as it does for a screen tap — so
        // the interactor leaves UI alone, and the two ways of reaching things never overlap: the hand
        // only ever picks things up, the gaze only ever clicks.
        // The attach controller's extras for controllers (smoothing, speed-scaled far movement,
        // momentum) are off: the tracked hand is already smoothed.
        // There is no hand ray and no poke interactor. The hand reaches the app about 140 ms late and
        // has to stay inside the camera's 68 by 42 degrees, so aiming with it was worse than aiming
        // with the head; a fingertip poke needs to be within a few centimeters of what it presses,
        // which leaves almost nowhere both reachable and visible. Both proved impractical.
        static (HandInteractorRig rig, NearFarInteractor interactor) BuildHandInteraction(GameObject origin,
            Transform trackingSpace, HandTracker tracker, HandSubsystemFeed feed, GazePointer gaze)
        {
            new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();

            // Everything the hand reaches with works from one point between its fingertips, aimed out
            // along the line from the eyes: a sphere for what's beside them, a ray for what isn't.
            var handPoint = new GameObject("Hand Point").transform;
            handPoint.SetParent(trackingSpace, false);

            var interactorObject = new GameObject("Hand Interactor");
            interactorObject.transform.SetParent(trackingSpace, false);
            var near = interactorObject.AddComponent<SphereInteractionCaster>();
            near.castOrigin = handPoint;
            near.enableStabilization = false;
            near.castRadius = 0.08f;
            var far = interactorObject.AddComponent<CurveInteractionCaster>();
            far.castOrigin = handPoint;
            far.enableStabilization = false;
            far.castDistance = 20f;
            // Detected surfaces are left out of the ray: nothing on them can be picked up, and a table
            // between the hand and a cube would otherwise stop the ray short of it. The gaze has the
            // same rule (GazePointer.RealSurfaceMask), for the same reason.
            far.raycastMask = Physics.DefaultRaycastLayers & ~(1 << BoundaryLayer);
            var attach = interactorObject.AddComponent<InteractionAttachController>();
            var serializedAttach = new SerializedObject(attach);
            // What's held follows the hand that took it, near or far.
            serializedAttach.FindProperty("m_TransformToFollow").objectReferenceValue = handPoint;
            // Never.
            serializedAttach.FindProperty("m_MotionStabilizationMode").enumValueIndex = 0;
            serializedAttach.FindProperty("m_UseDistanceBasedVelocityScaling").boolValue = false;
            serializedAttach.FindProperty("m_UseMomentum").boolValue = false;
            serializedAttach.ApplyModifiedPropertiesWithoutUndo();
            var interactor = interactorObject.AddComponent<NearFarInteractor>();
            // Something out of arm's reach is taken along the ray, and stays out there while it's held.
            interactor.farAttachMode = InteractorFarAttachMode.Far;
            // The gaze pointer presses UI, for a pinch as for a screen tap, so XRI leaves it alone.
            interactor.enableUIInteraction = false;
            interactorObject.SetActive(false);

            var rig = origin.AddComponent<HandInteractorRig>();
            var serializedRig = new SerializedObject(rig);
            serializedRig.FindProperty("m_Tracker").objectReferenceValue = tracker;
            serializedRig.FindProperty("m_Feed").objectReferenceValue = feed;
            serializedRig.FindProperty("m_Gaze").objectReferenceValue = gaze;
            serializedRig.FindProperty("m_Interactor").objectReferenceValue = interactor;
            serializedRig.FindProperty("m_HandPoint").objectReferenceValue = handPoint;
            serializedRig.ApplyModifiedPropertiesWithoutUndo();

            // A pinch selects, but only one the rig decided is taking hold of something.
            interactor.selectInput.inputSourceMode = XRInputButtonReader.InputSourceMode.ObjectReference;
            interactor.selectInput.SetObjectReference(rig);
            EditorUtility.SetDirty(interactor);
            return (rig, interactor);
        }


        // The "arView" launch option's debug view: the camera feed with the detected surfaces' grids.
        static void BuildArDebugView(GameObject origin, StereoRig rig, HeadPoseFusion fusion, RoomBoundary boundary,
            Camera arCamera, Behaviour[] arViewOnly)
        {
            var view = origin.AddComponent<ArDebugView>();
            var serializedView = new SerializedObject(view);
            serializedView.FindProperty("m_Rig").objectReferenceValue = rig;
            serializedView.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serializedView.FindProperty("m_Boundary").objectReferenceValue = boundary;
            serializedView.FindProperty("m_ArCamera").objectReferenceValue = arCamera;
            var behaviours = serializedView.FindProperty("m_ArViewOnly");
            behaviours.arraySize = arViewOnly.Length;
            for (var i = 0; i < arViewOnly.Length; i++)
                behaviours.GetArrayElementAtIndex(i).objectReferenceValue = arViewOnly[i];
            serializedView.ApplyModifiedPropertiesWithoutUndo();
        }

        // Camera passthrough: the whole camera image, drawn into a texture, shown on a screen quad in
        // front of the AR camera that both eyes see. Its shader draws it first, ignoring depth and
        // writing none, so virtual things always draw over it, however near or far they are.
        static void BuildPassthrough(PassthroughController passthrough, HeadPoseFusion fusion, StereoRig rig,
            Camera arCamera, M3Button toggle, Shader passthroughShader)
        {
            var material = SaveAsset(new Material(passthroughShader) { name = "Passthrough" }, $"{GeneratedFolder}/Passthrough.mat");
            var screen = CreatePrimitive(PrimitiveType.Quad, "Passthrough Screen", arCamera.transform, material);
            screen.layer = PassthroughLayer;
            var screenRenderer = screen.GetComponent<MeshRenderer>();
            screenRenderer.shadowCastingMode = ShadowCastingMode.Off;
            screenRenderer.receiveShadows = false;
            screenRenderer.enabled = false;

            var serialized = new SerializedObject(passthrough);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Rig").objectReferenceValue = rig;
            serialized.FindProperty("m_ArCamera").objectReferenceValue = arCamera;
            serialized.FindProperty("m_Screen").objectReferenceValue = screenRenderer;
            serialized.FindProperty("m_Toggle").objectReferenceValue = toggle;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // 360-degree Isolation / Environments mode (Poly Haven dynamic HDRIs + Dark Cinema).
        static void BuildEnvironment(EnvironmentManager environment, HeadPoseFusion fusion,
            PassthroughController passthrough, M3Button toggle, M3Button nextButton, Shader domeShader)
        {
            var serialized = new SerializedObject(environment);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serialized.FindProperty("m_DomeShader").objectReferenceValue = domeShader;
            serialized.FindProperty("m_Toggle").objectReferenceValue = toggle;
            serialized.FindProperty("m_NextButton").objectReferenceValue = nextButton;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Quick-toggle rear LED flashlight in the main toolbar.
        static void BuildTorch(TorchManager torch, PassthroughController passthrough, M3Button toggle)
        {
            var serialized = new SerializedObject(torch);
            serialized.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serialized.FindProperty("m_Toggle").objectReferenceValue = toggle;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Why a pinch isn't registering, when it isn't: an M3 plain tooltip under the reticle, held with
        // the head and nearer than any panel (PinchInput shows it). Hidden to start.
        static GameObject BuildHandHint(Transform head)
        {
            const float distance = 1f;
            const float below = 7f;
            var size = new Vector2(320f, 40f);
            var hint = M3.Canvas("Hand Hint", head, size, distance, M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            var direction = Quaternion.Euler(below, 0f, 0f) * Vector3.forward;
            hint.localPosition = direction * distance;
            hint.localRotation = Quaternion.LookRotation(direction, Vector3.up);
            M3.Icon("Icon", hint, "pinch", 20f, new Vector2(-size.x / 2f + 16f + 10f, 0f), M3Theme.OnSurface);
            M3.Text("Label", hint, "Raise your hand into view to pinch", M3Theme.Type.BodyMedium, new Vector2(16f, 0f),
                new Vector2(size.x - 56f, 24f), M3Theme.OnSurface);
            hint.gameObject.SetActive(false);
            return hint.gameObject;
        }

        // The one light, which PassthroughLighting points and colors like the room's in passthrough,
        // casting the shadows the surfaces catch. Unlit materials ignore it.
        static void BuildLighting(GameObject origin, HeadPoseFusion fusion, PassthroughController passthrough, Camera arCamera)
        {
            var light = new GameObject("Main Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            light.shadowStrength = 1f;
            light.renderMode = LightRenderMode.ForcePixel;
            var lighting = origin.AddComponent<PassthroughLighting>();
            var serialized = new SerializedObject(lighting);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Passthrough").objectReferenceValue = passthrough;
            serialized.FindProperty("m_CameraManager").objectReferenceValue = arCamera.GetComponent<ARCameraManager>();
            serialized.FindProperty("m_Light").objectReferenceValue = light;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Soft main-light shadows over k_ShadowDistance, received by transparent objects too, in every
        // URP asset and renderer the project uses.
        static void ConfigureShadows()
        {
            var assets = new System.Collections.Generic.HashSet<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            if (GraphicsSettings.defaultRenderPipeline is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset defaultAsset)
                assets.Add(defaultAsset);
            for (var i = 0; i < QualitySettings.count; i++)
            {
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset asset)
                    assets.Add(asset);
            }
            foreach (var asset in assets)
            {
                var serialized = new SerializedObject(asset);
                var changed = false;
                changed |= Set(serialized.FindProperty("m_MainLightShadowsSupported"), true);
                changed |= Set(serialized.FindProperty("m_SoftShadowsSupported"), true);
                var distance = serialized.FindProperty("m_ShadowDistance");
                if (!Mathf.Approximately(distance.floatValue, k_ShadowDistance))
                {
                    distance.floatValue = k_ShadowDistance;
                    changed = true;
                }
                if (changed)
                {
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.SaveAssetIfDirty(asset);
                    Debug.Log($"[StereoBuilder] Soft shadows within {k_ShadowDistance} m on {asset.name}");
                }
                // The shadow catcher is transparent, and the mobile renderer had transparent objects
                // not receive shadows, which strips the shadow keywords from it: no shadows on phones,
                // though the editor (the PC renderer) showed them.
                foreach (var rendererData in asset.rendererDataList)
                {
                    if (rendererData == null)
                        continue;
                    var serializedRenderer = new SerializedObject(rendererData);
                    if (Set(serializedRenderer.FindProperty("m_ShadowTransparentReceive"), true))
                    {
                        serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
                        AssetDatabase.SaveAssetIfDirty(rendererData);
                        Debug.Log($"[StereoBuilder] Transparent objects receive shadows in {rendererData.name}");
                    }
                }
            }
        }

        static bool Set(SerializedProperty property, bool value)
        {
            if (property == null || property.boolValue == value)
                return false;
            property.boolValue = value;
            return true;
        }

        // The black veil DemoManager fades the view through between scenes: a quad held with the head
        // just past the eyes' near clip (0.05 m), big enough to cover their whole view.
        static void BuildFade(GameObject head, DemoManager demos)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/Fade.shader");
            var material = SaveAsset(new Material(shader) { name = "Screen Fade" }, $"{GeneratedFolder}/Screen Fade.mat");
            var quad = CreatePrimitive(PrimitiveType.Quad, "Screen Fade", head.transform, material);
            quad.transform.localPosition = new Vector3(0f, 0f, 0.08f);
            quad.transform.localScale = new Vector3(1.5f, 1.5f, 1f);
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = UiRenderQueue.k_ScreenFadeSortingOrder;
            var fade = quad.AddComponent<ScreenFade>();
            var serialized = new SerializedObject(demos);
            serialized.FindProperty("m_Fade").objectReferenceValue = fade;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // The reticle straight ahead of the head, and the pointer that clicks what it's on when the
        // screen is tapped. Holding the tap recenters.
        static void BuildGaze(GameObject head, HeadPoseFusion fusion, Shader reticleShader)
        {
            var material = SaveAsset(new Material(reticleShader) { name = "Reticle" }, $"{GeneratedFolder}/Reticle.mat");
            var reticleObject = CreatePrimitive(PrimitiveType.Quad, "Gaze Reticle", head.transform, material);
            var renderer = reticleObject.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = UiRenderQueue.k_ReticleSortingOrder;
            var reticle = reticleObject.AddComponent<GazeReticle>();

            var pointer = head.AddComponent<GazePointer>();
            var serializedPointer = new SerializedObject(pointer);
            serializedPointer.FindProperty("m_Reticle").objectReferenceValue = reticle;
            // Detected surfaces don't hide the gaze panel.
            serializedPointer.FindProperty("m_RealSurfaceMask").intValue = 1 << BoundaryLayer;
            serializedPointer.ApplyModifiedPropertiesWithoutUndo();
            UnityEventTools.AddPersistentListener(pointer.OnHold, fusion.Recenter);
        }

        // Core's controls, as a Material floating toolbar: icon-and-label items on a pill-shaped surface,
        // in three groups split by dividers: Apps (toggles the app library above it), Scan Room (shown
        // only inside apps that need surface scanning), Ambienti (360-degree Isolation mode + Cambia),
        // and Torcia (quick LED flashlight toggle).
        static (M3Button[] items, RectTransform panel, RectTransform clockSlot, TMP_Text clockText,
            TMP_Text batteryText, GameObject notifBadge, TMP_Text notifBadgeText) BuildToolbar(EnvironmentManager environment,
            TorchManager torch, DemoManager demos, MediaControllerWindow mediaWindow, NotificationCenterWindow notifCenter)
        {
            // Low, out of the way, until SettingsSummoner opens it in front of the user.
            const float pitch = -30f;
            var panel = M3.Canvas("Gaze Panel", null, new Vector2(620f, k_ToolbarHeight), M3Theme.k_OverlayDistance,
                M3Theme.SurfaceContainerHigh, M3Theme.k_ShapeFull);
            panel.gameObject.AddComponent<GazeCanvas>();
            // In front of the origin, where the head starts, tilted to face it.
            var direction = Quaternion.Euler(-pitch, 0f, 0f) * Vector3.forward;
            panel.SetPositionAndRotation(direction * M3Theme.k_OverlayDistance, Quaternion.LookRotation(direction, Vector3.up));

            var clockSlot = M3.Group("Clock", panel, new Vector2(0f, k_ToolbarItemY), new Vector2(k_ToolbarItemWidth, k_ToolbarItemHeight));
            var clockText = M3.Text("ClockText", clockSlot, "--:--", M3Theme.Type.TitleMedium,
                new Vector2(0f, 9f), new Vector2(k_ToolbarItemWidth, 24f), M3Theme.OnSurface, TextAlignmentOptions.Center);
            clockText.fontSize = 18f;
            clockText.textWrappingMode = TextWrappingModes.NoWrap;
            var batteryText = M3.Text("BatteryText", clockSlot, "", M3Theme.Type.LabelLarge,
                new Vector2(0f, -13f), new Vector2(k_ToolbarItemWidth, 18f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Center);
            batteryText.fontSize = 12f;
            batteryText.textWrappingMode = TextWrappingModes.NoWrap;

            var items = new M3Button[8];
            var library = demos.gameObject.AddComponent<AppLibrary>();
            items[0] = ToolbarItem(panel, "Apps", "apps", "Apps", library.Toggle);
            items[1] = ToolbarItem(panel, "Environment", "landscape", "Ambienti", environment.OpenPicker);
            items[3] = ToolbarItem(panel, "ExitEnvironment", "view_in_ar", "Esci", environment.ExitEnvironment);
            items[3].gameObject.SetActive(false);
            items[4] = ToolbarItem(panel, "Torch", "flashlight_on", "Torcia", torch.Toggle);
            items[5] = ToolbarItem(panel, "Reorder", "swap_horiz", "Riordina", library.ToggleReorderMode);
            items[6] = ToolbarItem(panel, "Media", "music_note", "Media", mediaWindow.Toggle);
            items[7] = ToolbarItem(panel, "Notifications", "notifications", "Notifiche", notifCenter.Toggle);

            var badgeImg = M3.Surface("Badge", (RectTransform)items[7].transform, new Vector2(15f, 19f),
                new Vector2(18f, 18f), new Color(0.92f, 0.24f, 0.28f, 1f), M3Theme.k_ShapeFull,
                raycastTarget: false, style: VisionSurfaceStyle.Glossy);
            var badgeText = M3.Text("Count", badgeImg.rectTransform, "0", M3Theme.Type.LabelLarge,
                Vector2.zero, new Vector2(18f, 18f), Color.white, TextAlignmentOptions.Center);
            badgeText.fontSize = 10.5f;
            badgeImg.gameObject.SetActive(false);

            return (items, panel, clockSlot, clockText, batteryText, badgeImg.gameObject, badgeText);
        }

        const float k_ToolbarItemWidth = 68f;
        const float k_ToolbarItemHeight = 56f;
        const float k_ToolbarPadding = 8f;
        const float k_ToolbarDividerGap = 10f;
        const float k_ToolbarDividerHeight = 30f;
        const float k_ToolbarItemY = 0f;
        // Compact pill height (72 dp), matching the look-up quick bar.
        const float k_ToolbarHeight = k_ToolbarItemHeight + 2f * k_ToolbarPadding;

        static M3Button ToolbarItem(RectTransform panel, string name, string icon, string label, UnityAction onClick) =>
            M3.ToolbarItem(name, panel, icon, label, new Vector2(0f, k_ToolbarItemY), k_ToolbarItemWidth, onClick,
                height: k_ToolbarItemHeight, indicatorWidth: 46f, indicatorHeight: 28f, iconSize: 20f, labelFontSize: 11.5f);

        // The toolbar as the app's settings panel, summoned anywhere (SettingsSummoner): a round gear
        // button that appears between thumb and index finger while a palm faces the user, and a round
        // X under the panel to close it.
        static SettingsSummoner BuildSettings(GameObject origin, HeadPoseFusion fusion, RectTransform panel,
            TMP_Text clockText, TMP_Text batteryText, M3Button mediaBtn, M3Button notifBtn, GameObject notifBadge, TMP_Text notifBadgeText)
        {
            var summoner = origin.AddComponent<SettingsSummoner>();
            // Shown by hand, not looked at, so it has no GazeCanvas. At arm's length, sized for there.
            var button = M3.Canvas("Settings Button", null, new Vector2(48f, 48f), 0.4f);
            M3.IconButton("Apps", button, "apps", M3Button.Variant.Filled, Vector2.zero, null, 48f);
            // Orbiter distance under the panel.
            var close = M3.IconButton("Close", panel, "close", M3Button.Variant.Tonal,
                new Vector2(0f, -(k_ToolbarHeight / 2f + 12f + 22f)), summoner.Close, 44f);
            close.Button.navigation = new Navigation { mode = Navigation.Mode.None };

            var serialized = new SerializedObject(summoner);
            serialized.FindProperty("m_Fusion").objectReferenceValue = fusion;
            serialized.FindProperty("m_Tracker").objectReferenceValue = origin.GetComponent<HandTracker>();
            serialized.FindProperty("m_Sensor").objectReferenceValue = origin.GetComponent<PinchSensor>();
            serialized.FindProperty("m_Panel").objectReferenceValue = panel;
            serialized.FindProperty("m_Button").objectReferenceValue = button;
            serialized.FindProperty("m_ToolbarClockText").objectReferenceValue = clockText;
            serialized.FindProperty("m_ToolbarBatteryText").objectReferenceValue = batteryText;
            serialized.FindProperty("m_ToolbarMediaBtn").objectReferenceValue = mediaBtn;
            serialized.FindProperty("m_ToolbarNotifBtn").objectReferenceValue = notifBtn;
            serialized.FindProperty("m_ToolbarNotifBadge").objectReferenceValue = notifBadge;
            serialized.FindProperty("m_ToolbarNotifBadgeText").objectReferenceValue = notifBadgeText;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return summoner;
        }

        // The app library: visionOS Home View with circular glossy app icons floating cleanly in
        // space above the settings toolbar (transparent backing in floating mode; frosted glass sheet in palm mode).
        static void BuildLibrary(RectTransform panel, DemoManager demos, SettingsSummoner settings, M3Button appsButton,
            M3Button reorderButton, DemoCatalog catalog)
        {
            var size = AppLibrary.Size(catalog.Demos.Count + 1);
            var root = M3.Surface("App Library", panel, Vector2.zero, size, Color.clear,
                M3Theme.k_ShapePanel, raycastTarget: true, style: VisionSurfaceStyle.Frosted).rectTransform;
            root.gameObject.AddComponent<M3Fade>();
            var grid = M3.Group("Grid", root, Vector2.zero, size);
            var tagline = M3.Text("Tagline", root, "", M3Theme.Type.BodyMedium, new Vector2(0f, -size.y / 2f + 16f + 14f),
                new Vector2(size.x - 36f, 28f), M3Theme.OnSurfaceVariant);
            tagline.gameObject.SetActive(false);
            root.gameObject.SetActive(false);

            var serializedSettings = new SerializedObject(settings);
            serializedSettings.FindProperty("m_Above").objectReferenceValue = root;
            serializedSettings.ApplyModifiedPropertiesWithoutUndo();

            var library = demos.GetComponent<AppLibrary>();
            var serialized = new SerializedObject(library);
            serialized.FindProperty("m_Demos").objectReferenceValue = demos;
            serialized.FindProperty("m_Settings").objectReferenceValue = settings;
            serialized.FindProperty("m_AppsButton").objectReferenceValue = appsButton;
            serialized.FindProperty("m_ReorderButton").objectReferenceValue = reorderButton;
            serialized.FindProperty("m_Root").objectReferenceValue = root;
            serialized.FindProperty("m_Grid").objectReferenceValue = grid;
            serialized.FindProperty("m_Tagline").objectReferenceValue = tagline;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // Orders the items into their groups, with a divider before each group after the first.
        static void ArrangeToolbar(RectTransform panel, RectTransform clockSlot, M3Button[] items)
        {
            // Clock | Apps, Riordina | Scan Room (conditional) | Ambienti, Cambia (conditional), Torcia, Media, Notifiche.
            var rects = new[]
            {
                clockSlot,
                (RectTransform)items[0].transform,
                (RectTransform)items[5].transform,
                (RectTransform)items[2].transform,
                (RectTransform)items[1].transform,
                (RectTransform)items[3].transform,
                (RectTransform)items[4].transform,
                (RectTransform)items[6].transform,
                (RectTransform)items[7].transform,
            };
            var groups = new[] { 0, 1, 1, 2, 3, 3, 3, 3, 3 };
            var dividers = new Image[4];
            for (var g = 1; g < dividers.Length; g++)
            {
                dividers[g] = M3.Surface($"Divider {g}", panel, Vector2.zero, new Vector2(1f, k_ToolbarDividerHeight),
                    M3Theme.OutlineVariant, 0f, style: VisionSurfaceStyle.Pure);
            }
            panel.gameObject.AddComponent<M3Toolbar>().Initialize(rects, groups, dividers, new RectTransform[0], k_ToolbarItemWidth,
                k_ToolbarPadding, k_ToolbarItemY, k_ToolbarDividerGap);
        }

        internal static GameObject CreatePrimitive(PrimitiveType type, string name, Transform parent, Material material, bool keepCollider = false)
        {
            var primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            if (!keepCollider)
                Object.DestroyImmediate(primitive.GetComponent<Collider>());
            primitive.GetComponent<MeshRenderer>().sharedMaterial = material;
            return primitive;
        }

        internal static Material CreateMaterial(string name, Shader shader, Color color, Texture texture, float tiling = 1f,
            string folder = GeneratedFolder)
        {
            var material = new Material(shader) { name = name, color = color };
            if (texture != null)
            {
                material.mainTexture = texture;
                material.mainTextureScale = Vector2.one * tiling;
            }
            // Double-sided, so the room's walls show from inside regardless of facing.
            material.SetFloat("_Cull", 0f);
            return SaveAsset(material, $"{folder}/{name}.mat");
        }

        internal static T SaveAsset<T>(T asset, string path) where T : Object
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
