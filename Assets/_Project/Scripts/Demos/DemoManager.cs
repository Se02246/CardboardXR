using System.Collections;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CardboardXR.Demos
{
    // Moves between the lobby, exclusive full-room demos, and concurrent VisionOS-style windowed apps
    // (Web Cinema, Schermi Live, Impostazioni, Torcia) which can ALL stay open simultaneously in the
    // user's space. Each windowed app loads additively without fading to black or closing the others,
    // and can be closed individually via CloseApp(id). Lives in the Core scene.
    public class DemoManager : MonoBehaviour
    {
        static readonly HashSet<string> s_WindowedIds = new()
        {
            "browser",
            "phoneapps",
            "quadri",
            "oggetti",
            "streamcast",
            "settings",
        };

        [SerializeField] DemoCatalog m_Catalog;
        [SerializeField] CoreServices m_Core;
        [Tooltip("The view fades through black between exclusive scenes, rather than cutting.")]
        [SerializeField] ScreenFade m_Fade;

        string m_LoadedScene;
        DemoCatalog.Entry m_Current;
        readonly List<(DemoCatalog.Entry entry, string scenePath)> m_OpenWindows = new();
        CoreState m_BeforeDemo;
        GameObject m_DefaultPrefab;
        bool m_Busy;
        Coroutine m_Fading;

        // Fading out of a scene, quickly, then into the next more slowly, once it has started.
        const float k_FadeOutSeconds = 0.3f;
        const float k_FadeInSeconds = 0.5f;

        public DemoCatalog Catalog => m_Catalog;
        // The active exclusive demo, or the most recently opened windowed app, or null in the lobby.
        public DemoCatalog.Entry Current => m_Current;

        // A demo (or the lobby, null) finished loading.
        public event System.Action<DemoCatalog.Entry> Loaded;

        public static bool IsWindowedApp(DemoCatalog.Entry entry) =>
            entry != null && !string.IsNullOrEmpty(entry.Id) && s_WindowedIds.Contains(entry.Id);

        public bool IsRunning(DemoCatalog.Entry entry)
        {
            if (entry == null)
                return m_Current == null && m_OpenWindows.Count == 0;
            if (entry == m_Current)
                return true;
            for (var i = 0; i < m_OpenWindows.Count; i++)
            {
                if (m_OpenWindows[i].entry == entry)
                    return true;
            }
            return false;
        }

        void Awake()
        {
            if (m_Catalog == null)
                return;
            foreach (var entry in m_Catalog.Demos)
                SessionLog.KeepTag(entry.LogTag);
        }

        IEnumerator Start()
        {
            yield return null;
            if (m_Catalog == null)
            {
                Debug.LogError("[Demos] No demo catalog, so no lobby or demos");
                yield break;
            }
            m_DefaultPrefab = m_Core.Placer.ObjectPrefab;
            m_Core.Placer.ObjectPrefab = null;
            var index = LaunchOptions.GetInt("demo", -1);
            if (m_Catalog.TryGet(index, out var entry))
            {
                Debug.Log($"[Demos] Starting in demo {index} ({entry.Id}), from the launch option");
                yield return SwitchExclusive(entry);
            }
            else
            {
                if (index >= 0)
                    Debug.LogWarning($"[Demos] No demo {index}: the catalog has {m_Catalog.Demos.Count}");
                if (LaunchOptions.GetInt("dof", 6) == 6)
                {
                    var state = CoreState.Resolve(m_Core.State, m_Catalog.LobbyRequirements);
                    Debug.Log($"[Demos] Starting the lobby in {state}");
                    m_Core.Apply(state);
                }
                yield return SwitchExclusive(null);
            }
        }

        public void Enter(int index)
        {
            if (m_Catalog.TryGet(index, out var entry))
                Enter(entry);
        }

        public void Enter(DemoCatalog.Entry entry)
        {
            if (m_Busy || entry == null)
                return;
            if (IsWindowedApp(entry))
            {
                if (TryRefocusWindowed(entry))
                    return;
                StartCoroutine(OpenWindowed(entry));
                return;
            }
            if (entry == m_Current && m_OpenWindows.Count == 0)
            {
                RefocusLoadedScene();
                return;
            }
            StartCoroutine(SwitchExclusive(entry));
        }

        void RefocusLoadedScene()
        {
            if (string.IsNullOrEmpty(m_LoadedScene))
                return;
            var scene = SceneManager.GetSceneByPath(m_LoadedScene);
            if (!scene.isLoaded)
                return;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var demo in root.GetComponentsInChildren<Demo>(true))
                    demo.OnDemoRefocus(m_Core);
            }
        }

        // Closes a single concurrent windowed app by id, leaving all other open apps running.
        public void CloseApp(string id)
        {
            if (m_Busy || string.IsNullOrEmpty(id))
                return;
            for (var i = 0; i < m_OpenWindows.Count; i++)
            {
                if (m_OpenWindows[i].entry.Id == id)
                {
                    StartCoroutine(CloseWindowedAt(i));
                    return;
                }
            }
            if (m_Current != null && m_Current.Id == id)
                ExitToLobby();
        }

        // Closes all open apps and returns cleanly to the Lobby.
        public void ExitToLobby()
        {
            if (m_Busy || (m_Current == null && m_OpenWindows.Count == 0))
                return;
            StartCoroutine(SwitchExclusive(null));
        }

        bool TryRefocusWindowed(DemoCatalog.Entry entry)
        {
            for (var i = 0; i < m_OpenWindows.Count; i++)
            {
                if (m_OpenWindows[i].entry != entry)
                    continue;
                var scene = SceneManager.GetSceneByPath(m_OpenWindows[i].scenePath);
                foreach (var demo in Demos(scene))
                    demo.OnDemoRefocus(m_Core);
                m_Current = entry;
                Debug.Log($"[Demos] Refocused already-open windowed app {entry.Title}");
                return true;
            }
            return false;
        }

        // Opens a windowed app additively alongside any other already-open windowed apps with NO black fade.
        IEnumerator OpenWindowed(DemoCatalog.Entry next)
        {
            m_Busy = true;
            var started = Time.realtimeSinceStartup;
            try
            {
                if (m_Core.Settings != null)
                    m_Core.Settings.Close();

                // If an exclusive full-room demo is open, unload it cleanly first and restore Lobby underneath.
                if (m_Current != null && !IsWindowedApp(m_Current))
                {
                    if (m_Fade != null)
                    {
                        StopFade();
                        yield return m_Fade.FadeTo(1f, k_FadeOutSeconds);
                    }
                    yield return UnloadExclusive(restore: true);
                    yield return LoadLobbyBase();
                    if (m_Fade != null)
                        m_Fading = StartCoroutine(FadeIn());
                }

                // Ensure core meets the app's requirements without turning off what another open app needs.
                if (m_OpenWindows.Count == 0)
                    m_BeforeDemo = m_Core.State;
                var resolved = CoreState.Resolve(m_Core.State, next.Requirements);
                var combined = new CoreState(
                    m_Core.State.SixDof || resolved.SixDof,
                    m_Core.State.Passthrough || resolved.Passthrough,
                    m_Core.State.Hands || resolved.Hands);
                if (!combined.Equals(m_Core.State))
                    m_Core.Apply(combined);

                var loading = SceneManager.LoadSceneAsync(next.ScenePath, LoadSceneMode.Additive);
                if (loading == null)
                {
                    Debug.LogError($"[Demos] Couldn't load {next.ScenePath}: is it in the build?");
                    yield break;
                }
                yield return loading;

                var scene = SceneManager.GetSceneByPath(next.ScenePath);
                SceneManager.SetActiveScene(scene);
                m_OpenWindows.Add((next, next.ScenePath));
                m_Current = next;
                if (m_Core.Boundary != null)
                    m_Core.Boundary.ShowScanButton = false;
                if (m_Core.Passthrough != null)
                    VirtualEnvironment.SetAllHidden(m_Core.Passthrough.IsOn || EnvironmentManager.IsIsolated);
                foreach (var demo in Demos(scene))
                {
                    try
                    {
                        demo.OnDemoEnter(m_Core);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                Debug.Log($"[Demos] Opened windowed app {next.Title} ({m_OpenWindows.Count} open concurrently) in {Time.realtimeSinceStartup - started:F1} s");
            }
            finally
            {
                m_Busy = false;
            }
            Loaded?.Invoke(next);
        }

        IEnumerator CloseWindowedAt(int index)
        {
            m_Busy = true;
            DemoCatalog.Entry entry = null;
            try
            {
                if (index < 0 || index >= m_OpenWindows.Count)
                    yield break;
                var (item, path) = m_OpenWindows[index];
                entry = item;
                m_OpenWindows.RemoveAt(index);

                var scene = SceneManager.GetSceneByPath(path);
                foreach (var demo in Demos(scene))
                {
                    try
                    {
                        demo.OnDemoExit(m_Core);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                if (m_OpenWindows.Count > 0)
                {
                    m_Current = m_OpenWindows[m_OpenWindows.Count - 1].entry;
                    var prevScene = SceneManager.GetSceneByPath(m_OpenWindows[m_OpenWindows.Count - 1].scenePath);
                    if (prevScene.IsValid() && prevScene.isLoaded)
                        SceneManager.SetActiveScene(prevScene);
                }
                else
                {
                    m_Current = null;
                    if (!string.IsNullOrEmpty(m_LoadedScene))
                    {
                        var lobbyScene = SceneManager.GetSceneByPath(m_LoadedScene);
                        if (lobbyScene.IsValid() && lobbyScene.isLoaded)
                            SceneManager.SetActiveScene(lobbyScene);
                    }
                    else
                    {
                        SceneManager.SetActiveScene(gameObject.scene);
                    }
                }

                if (scene.IsValid() && scene.isLoaded)
                    yield return SceneManager.UnloadSceneAsync(scene);

                Debug.Log($"[Demos] Closed windowed app {entry.Title} ({m_OpenWindows.Count} remaining)");
            }
            finally
            {
                m_Busy = false;
            }
            Loaded?.Invoke(m_Current);
        }

        IEnumerator LoadLobbyBase()
        {
            if (m_LoadedScene == m_Catalog.LobbyScenePath)
                yield break;
            var loading = SceneManager.LoadSceneAsync(m_Catalog.LobbyScenePath, LoadSceneMode.Additive);
            if (loading == null)
                yield break;
            yield return loading;
            m_LoadedScene = m_Catalog.LobbyScenePath;
            var scene = SceneManager.GetSceneByPath(m_LoadedScene);
            if (scene.IsValid() && scene.isLoaded)
                SceneManager.SetActiveScene(scene);
        }

        // Unloads all open scenes and loads an exclusive demo, or the lobby for null.
        IEnumerator SwitchExclusive(DemoCatalog.Entry next)
        {
            m_Busy = true;
            var started = Time.realtimeSinceStartup;
            try
            {
                if (m_Fade != null)
                {
                    StopFade();
                    if (m_LoadedScene == null)
                        m_Fade.Alpha = 1f;
                    else
                        yield return m_Fade.FadeTo(1f, k_FadeOutSeconds);
                }
                var fromDemo = m_Current != null || m_OpenWindows.Count > 0;
                yield return UnloadAllWindowed();
                yield return UnloadExclusive(restore: next == null);

                if (next != null)
                {
                    if (!fromDemo)
                        m_BeforeDemo = m_Core.State;
                    var state = CoreState.Resolve(m_BeforeDemo, next.Requirements);
                    Debug.Log($"[Demos] Entering {next.Id}: {state} (was {m_BeforeDemo})");
                    m_Core.Placer.RemoveAll();
                    m_Core.Placer.ObjectPrefab = m_DefaultPrefab;
                    m_Core.Placer.SurfaceFilter = PlacementSurfaceFilter.HorizontalAny;
                    PinchRouter.SceneWantsHandPinch = next.Requirements.HandPinch;
                    m_Core.Apply(state, forceArCoreSixDof: next.Requirements.ScanSurfaces);
                }

                var path = next != null ? next.ScenePath : m_Catalog.LobbyScenePath;
                var loading = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
                if (loading == null)
                {
                    Debug.LogError($"[Demos] Couldn't load {path}: is it in the build?");
                    if (m_Fade != null)
                        m_Fade.Alpha = 0f;
                    yield break;
                }
                yield return loading;

                var scene = SceneManager.GetSceneByPath(path);
                SceneManager.SetActiveScene(scene);
                m_LoadedScene = path;
                m_Current = next;
                if (m_Core.Boundary != null)
                    m_Core.Boundary.ShowScanButton = next != null && next.Requirements.ScanSurfaces;
                PinchRouter.SceneWantsHandPinch = next != null && next.Requirements.HandPinch;
                if (m_Core.Passthrough != null && EnvironmentManager.IsIsolated)
                    VirtualEnvironment.SetAllHidden(true);
                foreach (var demo in Demos(scene))
                {
                    try
                    {
                        demo.OnDemoEnter(m_Core);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
                Debug.Log($"[Demos] {(next != null ? next.Title : "Lobby")} loaded in {Time.realtimeSinceStartup - started:F1} s");
            }
            finally
            {
                m_Busy = false;
            }
            Loaded?.Invoke(next);
            if (m_Fade != null)
                m_Fading = StartCoroutine(FadeIn());
        }

        IEnumerator UnloadAllWindowed()
        {
            if (m_OpenWindows.Count == 0)
                yield break;
            for (var i = m_OpenWindows.Count - 1; i >= 0; i--)
            {
                var (_, path) = m_OpenWindows[i];
                var scene = SceneManager.GetSceneByPath(path);
                foreach (var demo in Demos(scene))
                {
                    try
                    {
                        demo.OnDemoExit(m_Core);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
                if (scene.IsValid() && scene.isLoaded)
                    yield return SceneManager.UnloadSceneAsync(scene);
            }
            m_OpenWindows.Clear();
            m_Current = null;
        }

        IEnumerator FadeIn()
        {
            yield return null;
            yield return null;
            yield return m_Fade.FadeTo(0f, k_FadeInSeconds);
            m_Fading = null;
        }

        void StopFade()
        {
            if (m_Fading != null)
                StopCoroutine(m_Fading);
            m_Fading = null;
        }

        IEnumerator UnloadExclusive(bool restore)
        {
            if (m_Core.Settings != null)
                m_Core.Settings.Close();
            if (m_LoadedScene == null)
                yield break;

            var scene = SceneManager.GetSceneByPath(m_LoadedScene);
            foreach (var demo in Demos(scene))
                demo.OnDemoExit(m_Core);

            var leaving = m_Current;
            m_Current = null;
            m_LoadedScene = null;
            if (leaving != null)
            {
                if (!leaving.Requirements.KeepPlacedObjects)
                    m_Core.Placer.RemoveAll();
                m_Core.Placer.ObjectPrefab = null;
                m_Core.Placer.SurfaceFilter = PlacementSurfaceFilter.HorizontalAny;
                PinchRouter.Clear();
                if (restore)
                {
                    PinchRouter.SceneWantsHandPinch = false;
                    Debug.Log($"[Demos] Leaving {leaving.Id}: back to {m_BeforeDemo}");
                    m_Core.Apply(m_BeforeDemo);
                }
                else
                {
                    Debug.Log($"[Demos] Leaving {leaving.Id} for another app");
                }
            }

            SceneManager.SetActiveScene(gameObject.scene);
            if (scene.IsValid() && scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
            yield return Resources.UnloadUnusedAssets();
        }

        static Demo[] Demos(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return System.Array.Empty<Demo>();
            var found = new List<Demo>();
            foreach (var root in scene.GetRootGameObjects())
                found.AddRange(root.GetComponentsInChildren<Demo>());
            return found.ToArray();
        }
    }
}
