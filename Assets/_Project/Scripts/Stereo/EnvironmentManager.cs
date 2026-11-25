using System;
using System.IO;
using System.Threading.Tasks;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Apple Vision Pro-style 360-degree Isolation / Environments mode with fluid spherical mist
    // transition animations:
    //   - Camera -> Environment: organic luminous mist expands outward from the user's gaze over
    //     live passthrough until fully immersed, then hides passthrough.
    //   - Environment -> Camera: restores live passthrough immediately beneath the dome and opens
    //     a smooth glowing portal from the user's gaze back into the real room.
    //   - Environment A -> Environment B: asynchronously loads or downloads the next Poly Haven
    //     panorama (when in Play Mode) and sweeps smoothly from the previous panorama into the new one.
    public class EnvironmentManager : MonoBehaviour
    {
        const string k_EnvIndexPrefKey = "CardboardXR.EnvironmentIndex";
        const string k_EnvIdPrefKey = "CardboardXR.EnvironmentId";
        const float k_DomeRadius = 60f;
        const float k_TransitionGlow = 0.42f;

        public const float k_EnterDurationSeconds = 0.85f;
        public const float k_ExitDurationSeconds = 0.70f;
        public const float k_CrossfadeDurationSeconds = 0.78f;

        static readonly int k_BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int k_FromMapId = Shader.PropertyToID("_FromMap");
        static readonly int k_UseProceduralId = Shader.PropertyToID("_UseProcedural");
        static readonly int k_FromProceduralId = Shader.PropertyToID("_FromProcedural");
        static readonly int k_ExposureId = Shader.PropertyToID("_Exposure");
        static readonly int k_ImmersionId = Shader.PropertyToID("_Immersion");
        static readonly int k_RevealDirectionId = Shader.PropertyToID("_RevealDirection");
        static readonly int k_RevealOriginDirId = Shader.PropertyToID("_RevealOriginDir");
        static readonly int k_CrossfadeId = Shader.PropertyToID("_Crossfade");
        static readonly int k_CrossfadeOriginDirId = Shader.PropertyToID("_CrossfadeOriginDir");
        static readonly int k_TransitionGlowId = Shader.PropertyToID("_TransitionGlow");

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] Shader m_DomeShader;
        [SerializeField] M3Button m_Toggle;
        [SerializeField] M3Button m_NextButton;

        static EnvironmentManager s_Instance;

        GameObject m_Dome;
        Material m_Material;
        Texture2D m_ActiveTexture;
        string m_ActiveAssetId;
        bool m_ActiveProcedural;
        Texture2D m_FromTexture;
        bool m_FromProcedural;
        int m_Index;
        bool m_Isolated;
        bool m_Initialized;

        float m_Immersion;
        float m_TargetImmersion;
        float m_RevealDirection = 1f;
        Vector3 m_RevealOriginDir = Vector3.forward;
        float m_Crossfade = 1f;
        bool m_CrossfadeActive;
        Vector3 m_CrossfadeOriginDir = Vector3.forward;
        bool m_WaitingForInitialLoad;
        bool m_PassthroughIsolatedApplied;

        int m_LoadVersion;
        Task<byte[]> m_PendingReadTask;
        int m_PendingIndex = -1;
        string m_PendingAssetId;
        int m_PendingVersion;
        bool m_PendingCrossfade;

        public static EnvironmentManager Instance => s_Instance;
        public static bool IsIsolated => s_Instance != null && s_Instance.m_Isolated;
        public static bool IsAnyTransitionActive => s_Instance != null && s_Instance.IsTransitioning;
        public static int EnvironmentCount => EnvironmentLibraryStore.Count;
        public static int CurrentIndex => s_Instance != null ? s_Instance.m_Index : 0;
        public static string CurrentId => EnvironmentLibraryStore.Get(CurrentIndex).Id;
        public static string CurrentName => EnvironmentLibraryStore.Get(CurrentIndex).Title;
        public static event Action<bool> IsolationChanged;
        public static event Action<string> EnvironmentChanged;

        public static string GetPresetTitle(int index) => EnvironmentLibraryStore.Get(index).Title;
        public static string GetPresetId(int index) => EnvironmentLibraryStore.Get(index).Id;
        public static string GetPresetThumbUrl(int index) => EnvironmentLibraryStore.Get(index).ThumbUrl;
        public static bool IsPresetProcedural(int index) => EnvironmentLibraryStore.Get(index).IsProceduralCinema;
        public static bool IsPresetDownloaded(int index) =>
            EnvironmentLibraryStore.IsPanoramaCached(EnvironmentLibraryStore.Get(index).Id);

        public float Immersion => m_Immersion;
        public float Crossfade => m_Crossfade;
        public bool IsTransitioning =>
            !Mathf.Approximately(m_Immersion, m_TargetImmersion) || m_CrossfadeActive || m_WaitingForInitialLoad;
        public Material DomeMaterial => m_Material;
        public GameObject DomeObject => m_Dome;

        public static float EaseTransition(float t)
        {
            var x = Mathf.Clamp01(t);
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }

        void Awake() => EnsureInitialized();

        public void EnsureInitialized()
        {
            SettingsPanelHub.EnvironmentsOpenChanged -= OnEnvironmentsOpenChanged;
            SettingsPanelHub.EnvironmentsOpenChanged += OnEnvironmentsOpenChanged;
            EnvironmentLibraryStore.Changed -= OnLibraryStoreChanged;
            EnvironmentLibraryStore.Changed += OnLibraryStoreChanged;

            if (m_Toggle != null)
            {
                if (m_Toggle.Button != null)
                {
                    var onClick = m_Toggle.Button.onClick;
                    for (var i = 0; i < onClick.GetPersistentEventCount(); i++)
                        onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                    onClick.RemoveListener(OpenPicker);
                    onClick.AddListener(OpenPicker);
                }
                m_Toggle.Selected = m_Isolated || SettingsPanelHub.IsEnvironmentsOpen;
            }
            if (m_NextButton != null)
            {
                if (m_NextButton.Button != null)
                {
                    var onClick = m_NextButton.Button.onClick;
                    for (var i = 0; i < onClick.GetPersistentEventCount(); i++)
                        onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                    onClick.RemoveListener(ExitEnvironment);
                    onClick.AddListener(ExitEnvironment);
                }
                if (m_NextButton.IconGraphic != null)
                    m_NextButton.IconGraphic.text = M3Icons.Glyph("view_in_ar");
                if (m_NextButton.Label != null)
                    m_NextButton.Label.text = "Esci";
            }
            if (m_Initialized)
                return;
            m_Initialized = true;
            s_Instance = this;

            var savedId = PlayerPrefs.GetString(k_EnvIdPrefKey, "");
            var idxById = !string.IsNullOrEmpty(savedId) ? EnvironmentLibraryStore.IndexOfId(savedId) : -1;
            m_Index = idxById >= 0
                ? idxById
                : Mathf.Clamp(PlayerPrefs.GetInt(k_EnvIndexPrefKey, 0), 0, Mathf.Max(0, EnvironmentLibraryStore.Count - 1));

            BuildDome();
            SetIsolated(false, force: true);
        }

        void OnDestroy()
        {
            SettingsPanelHub.EnvironmentsOpenChanged -= OnEnvironmentsOpenChanged;
            EnvironmentLibraryStore.Changed -= OnLibraryStoreChanged;
            if (m_Toggle != null && m_Toggle.Button != null)
                m_Toggle.Button.onClick.RemoveListener(OpenPicker);
            if (m_NextButton != null && m_NextButton.Button != null)
                m_NextButton.Button.onClick.RemoveListener(ExitEnvironment);
            if (s_Instance == this)
                s_Instance = null;
            CancelPendingLoad();
            ReleaseAllTextures();
            DestroyObjectSafe(m_Material);
            DestroyObjectSafe(m_Dome);
        }

        void OnEnvironmentsOpenChanged(bool open)
        {
            if (m_Toggle != null)
                m_Toggle.Selected = m_Isolated || open;
        }

        void OnLibraryStoreChanged()
        {
            var count = EnvironmentLibraryStore.Count;
            if (count <= 0)
            {
                m_Index = 0;
                return;
            }
            if (!string.IsNullOrEmpty(m_ActiveAssetId))
            {
                var found = EnvironmentLibraryStore.IndexOfId(m_ActiveAssetId);
                if (found >= 0)
                {
                    m_Index = found;
                    return;
                }
            }
            m_Index = Mathf.Clamp(m_Index, 0, count - 1);
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

        void BuildDome()
        {
            var shader = m_DomeShader != null ? m_DomeShader : Shader.Find("CardboardXR/EnvironmentDome");
            if (shader == null)
            {
                Debug.LogWarning("[Environment] CardboardXR/EnvironmentDome shader not found");
                return;
            }
            m_Material = new Material(shader) { name = "Environment 360 Dome" };
            m_Dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            m_Dome.name = "Environment 360 Dome";
            var col = m_Dome.GetComponent<Collider>();
            DestroyObjectSafe(col);
            m_Dome.transform.SetParent(transform, false);
            m_Dome.transform.localScale = Vector3.one * (k_DomeRadius * 2f);
            var renderer = m_Dome.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = m_Material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            m_Dome.SetActive(false);
        }

        public void OpenPicker()
        {
            if (SettingsPanelHub.HasEnvironmentsHandler)
                SettingsPanelHub.ToggleEnvironments();
            else
                Toggle();
        }

        public void Toggle() => SetIsolated(!m_Isolated);

        public void ExitEnvironment() => SetIsolated(false);

        public static void ToggleGlobal() => s_Instance?.Toggle();

        public static void ExitEnvironmentGlobal() => s_Instance?.ExitEnvironment();

        public static void SetIsolatedGlobal(bool isolated) => s_Instance?.SetIsolated(isolated);

        public static void SelectEnvironmentGlobal(int index) => s_Instance?.SelectEnvironment(index);

        public static void SelectEnvironmentByIdGlobal(string assetId) => s_Instance?.SelectEnvironmentById(assetId);

        public static void CycleEnvironmentGlobal() => s_Instance?.CycleEnvironment();

        public void SetIsolated(bool isolated, bool force = false)
        {
            EnsureInitialized();
            if (!force && isolated == m_Isolated)
                return;
            m_Isolated = isolated;

            if (m_Toggle != null)
                m_Toggle.Selected = isolated || SettingsPanelHub.IsEnvironmentsOpen;
            if (m_NextButton != null)
                m_NextButton.gameObject.SetActive(isolated);

            var currentEntry = EnvironmentLibraryStore.Get(m_Index);

            if (force)
            {
                CancelPendingLoad();
                m_Immersion = isolated ? 1f : 0f;
                m_TargetImmersion = m_Immersion;
                m_RevealDirection = isolated ? 1f : -1f;
                m_Crossfade = 1f;
                m_CrossfadeActive = false;
                m_WaitingForInitialLoad = false;

                if (isolated)
                {
                    ApplyPresetSync(m_Index, crossfade: false);
                    if (m_Dome != null)
                        m_Dome.SetActive(true);
                    if (m_Passthrough != null)
                        m_Passthrough.SetIsolated(true);
                    m_PassthroughIsolatedApplied = true;
                    VirtualEnvironment.SetAllHidden(true);
                }
                else
                {
                    if (m_Dome != null)
                        m_Dome.SetActive(false);
                    if (m_Passthrough != null)
                        m_Passthrough.SetIsolated(false);
                    m_PassthroughIsolatedApplied = false;
                    ReleaseAllTextures();
                }

                ApplyShaderTransitionUniforms();
            }
            else
            {
                m_RevealOriginDir = CurrentHeadForward();
                m_RevealDirection = isolated ? 1f : -1f;
                m_TargetImmersion = isolated ? 1f : 0f;

                if (isolated)
                {
                    if (m_Dome != null)
                        m_Dome.SetActive(true);
                    if (!IsPresetLoadedAsActive(m_Index))
                    {
                        m_WaitingForInitialLoad = m_Immersion <= 0.001f;
                        BeginPresetLoad(m_Index, crossfade: false);
                    }
                    else
                    {
                        CancelPendingLoad();
                        m_WaitingForInitialLoad = false;
                    }
                    ApplyShaderTransitionUniforms();
                }
                else
                {
                    CancelPendingLoad();
                    m_WaitingForInitialLoad = false;
                    // Restore live passthrough immediately beneath the still-opaque dome so no black frame shows.
                    if (m_Passthrough != null)
                        m_Passthrough.SetIsolated(false);
                    m_PassthroughIsolatedApplied = false;

                    if (m_Immersion <= 0.001f)
                    {
                        m_Immersion = 0f;
                        m_Crossfade = 1f;
                        m_CrossfadeActive = false;
                        if (m_Dome != null)
                            m_Dome.SetActive(false);
                        ReleaseAllTextures();
                    }
                    else if (m_Dome != null)
                    {
                        m_Dome.SetActive(true);
                    }
                    ApplyShaderTransitionUniforms();
                }
            }

            Debug.Log(isolated
                ? $"[Environment] Modalità Isolamento ON ({currentEntry.Title})"
                : "[Environment] Modalità Isolamento OFF (Passthrough AR)");
            IsolationChanged?.Invoke(isolated);
        }

        public void SelectEnvironmentById(string assetId)
        {
            EnsureInitialized();
            var idx = EnvironmentLibraryStore.IndexOfId(assetId);
            if (idx >= 0)
                SelectEnvironment(idx);
        }

        public void SelectEnvironment(int index)
        {
            EnsureInitialized();
            var count = Mathf.Max(1, EnvironmentLibraryStore.Count);
            var clamped = Mathf.Clamp(index, 0, count - 1);
            var entry = EnvironmentLibraryStore.Get(clamped);
            var changed = clamped != m_Index || !string.Equals(m_ActiveAssetId, entry.Id, StringComparison.Ordinal);
            m_Index = clamped;
            PlayerPrefs.SetInt(k_EnvIndexPrefKey, m_Index);
            PlayerPrefs.SetString(k_EnvIdPrefKey, entry.Id ?? "");
            PlayerPrefs.Save();

            if (!m_Isolated)
            {
                SetIsolated(true);
            }
            else if (changed || !IsPresetLoadedAsActive(m_Index))
            {
                m_CrossfadeOriginDir = CurrentHeadForward();
                BeginPresetLoad(m_Index, crossfade: true);
                ApplyShaderTransitionUniforms();
            }

            Debug.Log($"[Environment] Ambiente selezionato -> {entry.Title} ({m_Index + 1}/{count})");
            EnvironmentChanged?.Invoke(entry.Title);
        }

        public void CycleEnvironment()
        {
            var count = Mathf.Max(1, EnvironmentLibraryStore.Count);
            SelectEnvironment((m_Index + 1) % count);
        }

        void Update() => StepTransitions(Mathf.Min(Time.unscaledDeltaTime, 0.1f));

        public void StepTransitions(float dt)
        {
            PollPendingLoad();

            var safeDt = Mathf.Max(0f, dt);
            if (m_CrossfadeActive)
            {
                m_Crossfade = Mathf.MoveTowards(m_Crossfade, 1f, safeDt / k_CrossfadeDurationSeconds);
                if (m_Crossfade >= 0.9995f)
                {
                    m_Crossfade = 1f;
                    m_CrossfadeActive = false;
                    ReleaseFromTexture();
                }
            }

            if (!m_WaitingForInitialLoad && !Mathf.Approximately(m_Immersion, m_TargetImmersion))
            {
                var duration = m_TargetImmersion > m_Immersion ? k_EnterDurationSeconds : k_ExitDurationSeconds;
                m_Immersion = Mathf.MoveTowards(m_Immersion, m_TargetImmersion, safeDt / duration);
                if (m_TargetImmersion >= 1f && m_Immersion >= 0.9995f)
                {
                    m_Immersion = 1f;
                    if (m_Passthrough != null && !m_PassthroughIsolatedApplied)
                        m_Passthrough.SetIsolated(true);
                    m_PassthroughIsolatedApplied = true;
                    VirtualEnvironment.SetAllHidden(true);
                }
                else if (m_TargetImmersion <= 0f && m_Immersion <= 0.0005f)
                {
                    m_Immersion = 0f;
                    m_Crossfade = 1f;
                    m_CrossfadeActive = false;
                    if (m_Dome != null)
                        m_Dome.SetActive(false);
                    ReleaseAllTextures();
                }
            }

            if (m_Material != null && m_Dome != null && m_Dome.activeSelf)
                ApplyShaderTransitionUniforms();
        }

        Vector3 CurrentHeadForward()
        {
            var fwd = m_Fusion != null ? m_Fusion.transform.forward : Vector3.forward;
            return fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
        }

        bool IsPresetLoadedAsActive(int index)
        {
            var entry = EnvironmentLibraryStore.Get(index);
            if (entry.IsProceduralCinema || string.IsNullOrEmpty(entry.Id))
                return m_ActiveProcedural;
            return !m_ActiveProcedural &&
                   string.Equals(m_ActiveAssetId, entry.Id, StringComparison.Ordinal) &&
                   m_ActiveTexture != null;
        }

        void CancelPendingLoad()
        {
            m_LoadVersion++;
            m_PendingReadTask = null;
            m_PendingIndex = -1;
            m_PendingAssetId = null;
            m_PendingCrossfade = false;
        }

        void BeginPresetLoad(int index, bool crossfade)
        {
            m_LoadVersion++;
            m_PendingReadTask = null;

            var entry = EnvironmentLibraryStore.Get(index);
            if (entry.IsProceduralCinema || string.IsNullOrEmpty(entry.Id))
            {
                CommitLoadedPreset(index, entry.Id, null, isProcedural: true, crossfade: crossfade);
                return;
            }

            if (!m_ActiveProcedural &&
                string.Equals(m_ActiveAssetId, entry.Id, StringComparison.Ordinal) &&
                m_ActiveTexture != null)
            {
                m_WaitingForInitialLoad = false;
                return;
            }

            if (!Application.isPlaying)
            {
                ApplyPresetSync(index, crossfade);
                return;
            }

            var version = m_LoadVersion;
            m_PendingIndex = index;
            m_PendingAssetId = entry.Id;
            m_PendingVersion = version;
            m_PendingCrossfade = crossfade;

            if (EnvironmentLibraryStore.IsPanoramaCached(entry.Id))
            {
                var path = EnvironmentLibraryStore.GetPanoramaCachePath(entry.Id);
                m_PendingReadTask = Task.Run(() => TryReadFileBytes(path));
                return;
            }

            PolyHavenService.Instance.DownloadPanorama(entry.Id, null, (id, ok, err) =>
            {
                if (version != m_LoadVersion || !m_Isolated)
                    return;
                if (!ok || !EnvironmentLibraryStore.IsPanoramaCached(id))
                {
                    Debug.LogWarning($"[Environment] Download Poly Haven fallito per {id}: {err}");
                    m_WaitingForInitialLoad = false;
                    m_PendingIndex = -1;
                    m_PendingAssetId = null;
                    if (!m_ActiveProcedural && m_ActiveTexture == null)
                        SetIsolated(false, force: m_Immersion <= 0.001f);
                    return;
                }

                var path = EnvironmentLibraryStore.GetPanoramaCachePath(id);
                m_PendingReadTask = Task.Run(() => TryReadFileBytes(path));
            });
        }

        void PollPendingLoad()
        {
            if (m_PendingReadTask == null || !m_PendingReadTask.IsCompleted)
                return;

            var task = m_PendingReadTask;
            var index = m_PendingIndex;
            var assetId = m_PendingAssetId;
            var version = m_PendingVersion;
            var crossfade = m_PendingCrossfade;
            m_PendingReadTask = null;
            m_PendingIndex = -1;
            m_PendingAssetId = null;

            if (version != m_LoadVersion || !m_Isolated)
                return;

            var bytes = task.IsFaulted ? null : task.Result;
            var tex = DecodePanoramaTexture(bytes, assetId);
            if (tex == null)
            {
                Debug.LogWarning($"[Environment] Impossibile decodificare il panorama {assetId}");
                m_WaitingForInitialLoad = false;
                if (!m_ActiveProcedural && m_ActiveTexture == null)
                    SetIsolated(false, force: m_Immersion <= 0.001f);
                return;
            }

            CommitLoadedPreset(index, assetId, tex, isProcedural: false, crossfade: crossfade);
        }

        void ApplyPresetSync(int index, bool crossfade)
        {
            var entry = EnvironmentLibraryStore.Get(index);
            if (entry.IsProceduralCinema || string.IsNullOrEmpty(entry.Id))
            {
                CommitLoadedPreset(index, entry.Id, null, isProcedural: true, crossfade: crossfade);
                return;
            }

            if (!m_ActiveProcedural &&
                string.Equals(m_ActiveAssetId, entry.Id, StringComparison.Ordinal) &&
                m_ActiveTexture != null)
            {
                CommitLoadedPreset(index, entry.Id, m_ActiveTexture, isProcedural: false, crossfade: crossfade);
                return;
            }

            Texture2D tex = null;
            if (EnvironmentLibraryStore.IsPanoramaCached(entry.Id))
                tex = DecodePanoramaTexture(TryReadFileBytes(EnvironmentLibraryStore.GetPanoramaCachePath(entry.Id)), entry.Id);

            if (tex == null && !Application.isPlaying)
                tex = CreateEditModeFallbackTexture(entry.Id);

            CommitLoadedPreset(index, entry.Id, tex, isProcedural: tex == null, crossfade: crossfade);
        }

        static byte[] TryReadFileBytes(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static Texture2D DecodePanoramaTexture(byte[] bytes, string assetId)
        {
            if (bytes == null || bytes.Length < 16)
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"Env_{assetId}",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                return tex;
            DestroyObjectSafe(tex);
            return null;
        }

        static Texture2D CreateEditModeFallbackTexture(string assetId)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"EnvFallback_{assetId}",
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels32(new[]
            {
                new Color32(48, 84, 132, 255),
                new Color32(64, 108, 162, 255),
                new Color32(36, 60, 96, 255),
                new Color32(52, 88, 136, 255),
            });
            tex.Apply(false, true);
            return tex;
        }

        void CommitLoadedPreset(int index, string assetId, Texture2D tex, bool isProcedural, bool crossfade)
        {
            m_WaitingForInitialLoad = false;
            if (tex != null)
            {
                tex.wrapModeU = TextureWrapMode.Repeat;
                tex.wrapModeV = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
            }

            var doCrossfade = crossfade && m_Material != null && m_Dome != null &&
                              m_Dome.activeSelf && m_Immersion > 0.01f;
            var oldActive = m_ActiveTexture;
            var oldFrom = m_FromTexture;
            var oldProcedural = m_ActiveProcedural;

            if (doCrossfade)
            {
                m_FromTexture = oldActive;
                m_FromProcedural = oldProcedural;
                m_ActiveTexture = tex;
                m_ActiveAssetId = tex != null ? assetId : (isProcedural ? EnvironmentLibraryStore.ProceduralCinemaId : null);
                m_ActiveProcedural = isProcedural;
                m_Crossfade = 0f;
                m_CrossfadeActive = true;
                if (m_Material != null)
                {
                    m_Material.SetTexture(k_FromMapId, m_FromTexture);
                    m_Material.SetFloat(k_FromProceduralId, m_FromProcedural ? 1f : 0f);
                }
                UnloadIfUnused(oldFrom, m_ActiveTexture, m_FromTexture);
            }
            else
            {
                m_FromTexture = null;
                m_FromProcedural = false;
                m_ActiveTexture = tex;
                m_ActiveAssetId = tex != null ? assetId : (isProcedural ? EnvironmentLibraryStore.ProceduralCinemaId : null);
                m_ActiveProcedural = isProcedural;
                m_Crossfade = 1f;
                m_CrossfadeActive = false;
                if (m_Material != null)
                {
                    m_Material.SetTexture(k_FromMapId, null);
                    m_Material.SetFloat(k_FromProceduralId, 0f);
                }
                UnloadIfUnused(oldFrom, m_ActiveTexture, null);
                UnloadIfUnused(oldActive, m_ActiveTexture, null);
            }

            if (m_Material != null)
            {
                m_Material.SetTexture(k_BaseMapId, m_ActiveTexture);
                m_Material.SetFloat(k_UseProceduralId, m_ActiveProcedural ? 1f : 0f);
                m_Material.SetFloat(k_ExposureId, 1.0f);
            }
            ApplyShaderTransitionUniforms();
        }

        void ApplyShaderTransitionUniforms()
        {
            if (m_Material == null)
                return;
            m_Material.SetFloat(k_ImmersionId, EaseTransition(m_Immersion));
            m_Material.SetFloat(k_RevealDirectionId, m_RevealDirection);
            m_Material.SetVector(k_RevealOriginDirId,
                new Vector4(m_RevealOriginDir.x, m_RevealOriginDir.y, m_RevealOriginDir.z, 0f));
            m_Material.SetFloat(k_CrossfadeId, EaseTransition(m_Crossfade));
            m_Material.SetVector(k_CrossfadeOriginDirId,
                new Vector4(m_CrossfadeOriginDir.x, m_CrossfadeOriginDir.y, m_CrossfadeOriginDir.z, 0f));
            m_Material.SetFloat(k_TransitionGlowId, k_TransitionGlow);
        }

        void ReleaseFromTexture()
        {
            if (m_Material != null)
            {
                m_Material.SetTexture(k_FromMapId, null);
                m_Material.SetFloat(k_FromProceduralId, 0f);
            }
            var prev = m_FromTexture;
            m_FromTexture = null;
            m_FromProcedural = false;
            UnloadIfUnused(prev, m_ActiveTexture, null);
        }

        void ReleaseAllTextures()
        {
            if (m_Material != null)
            {
                m_Material.SetTexture(k_BaseMapId, null);
                m_Material.SetTexture(k_FromMapId, null);
                m_Material.SetFloat(k_FromProceduralId, 0f);
            }
            var active = m_ActiveTexture;
            var from = m_FromTexture;
            m_ActiveTexture = null;
            m_FromTexture = null;
            m_ActiveAssetId = null;
            m_ActiveProcedural = false;
            m_FromProcedural = false;
            UnloadIfUnused(active, null, null);
            UnloadIfUnused(from, active, null);
        }

        static void UnloadIfUnused(Texture2D tex, Texture2D keepA, Texture2D keepB)
        {
            if (tex != null && !ReferenceEquals(tex, keepA) && !ReferenceEquals(tex, keepB))
                DestroyObjectSafe(tex);
        }

        void LateUpdate()
        {
            if (m_Dome == null || !m_Dome.activeSelf || m_Fusion == null)
                return;
            // Follow head position throughout both immersion and reveal/exit animations (never rotation).
            m_Dome.transform.position = m_Fusion.transform.position;
        }
    }
}
