using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class EnvironmentTransitionTests
    {
        const string k_EnvPrefKey = "CardboardXR.EnvironmentIndex";
        const string k_EnvIdPrefKey = "CardboardXR.EnvironmentId";
        const string k_EnvStorePrefKey = "CardboardXR.PolyHavenEnvironments";

        readonly List<GameObject> m_Created = new();
        bool m_HadPref;
        int m_SavedPref;
        bool m_HadIdPref;
        string m_SavedIdPref;
        bool m_HadStorePref;
        string m_SavedStorePref;

        [SetUp]
        public void SetUp()
        {
            m_HadPref = PlayerPrefs.HasKey(k_EnvPrefKey);
            m_SavedPref = m_HadPref ? PlayerPrefs.GetInt(k_EnvPrefKey, 0) : 0;
            m_HadIdPref = PlayerPrefs.HasKey(k_EnvIdPrefKey);
            m_SavedIdPref = m_HadIdPref ? PlayerPrefs.GetString(k_EnvIdPrefKey, "") : "";
            m_HadStorePref = PlayerPrefs.HasKey(k_EnvStorePrefKey);
            m_SavedStorePref = m_HadStorePref ? PlayerPrefs.GetString(k_EnvStorePrefKey, "") : "";

            PlayerPrefs.DeleteKey(k_EnvIdPrefKey);
            PlayerPrefs.SetInt(k_EnvPrefKey, 0);
            EnvironmentLibraryStore.ResetToDefaults();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in m_Created)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }
            m_Created.Clear();

            if (m_HadPref)
                PlayerPrefs.SetInt(k_EnvPrefKey, m_SavedPref);
            else
                PlayerPrefs.DeleteKey(k_EnvPrefKey);

            if (m_HadIdPref)
                PlayerPrefs.SetString(k_EnvIdPrefKey, m_SavedIdPref);
            else
                PlayerPrefs.DeleteKey(k_EnvIdPrefKey);

            if (m_HadStorePref)
                PlayerPrefs.SetString(k_EnvStorePrefKey, m_SavedStorePref);
            else
                PlayerPrefs.DeleteKey(k_EnvStorePrefKey);

            EnvironmentLibraryStore.ResetToDefaults();
        }

        EnvironmentManager CreateManager()
        {
            var go = new GameObject("EnvManagerHost");
            m_Created.Add(go);
            var env = go.AddComponent<EnvironmentManager>();
            env.EnsureInitialized();
            return env;
        }

        [Test]
        public void EaseTransition_IsMonotonicAndExactAtEndpoints()
        {
            Assert.AreEqual(0f, EnvironmentManager.EaseTransition(-0.2f), 1e-6f);
            Assert.AreEqual(0f, EnvironmentManager.EaseTransition(0f), 1e-6f);
            Assert.AreEqual(1f, EnvironmentManager.EaseTransition(1f), 1e-6f);
            Assert.AreEqual(1f, EnvironmentManager.EaseTransition(1.5f), 1e-6f);
            Assert.AreEqual(0.5f, EnvironmentManager.EaseTransition(0.5f), 1e-5f);

            var prev = -1f;
            for (var i = 0; i <= 20; i++)
            {
                var v = EnvironmentManager.EaseTransition(i / 20f);
                Assert.Greater(v, prev);
                prev = v;
            }
        }

        [Test]
        public void SetIsolated_AnimatesImmersionSmoothlyEnterAndExit()
        {
            var env = CreateManager();
            Assert.IsNotNull(env.DomeObject);
            Assert.IsNotNull(env.DomeMaterial);
            Assert.IsFalse(env.DomeObject.activeSelf);
            Assert.AreEqual(0f, env.Immersion, 1e-5f);

            // Camera -> Environment reveals smoothly from 0 to 1.
            env.SetIsolated(true);
            Assert.IsTrue(EnvironmentManager.IsIsolated);
            Assert.IsTrue(env.DomeObject.activeSelf);
            Assert.IsTrue(env.IsTransitioning);
            Assert.AreEqual(1f, env.DomeMaterial.GetFloat("_RevealDirection"), 1e-5f);

            env.StepTransitions(0.40f);
            Assert.Greater(env.Immersion, 0.1f);
            Assert.Less(env.Immersion, 0.9f);
            Assert.IsTrue(env.DomeObject.activeSelf);
            var midShaderImmersion = env.DomeMaterial.GetFloat("_Immersion");
            Assert.Greater(midShaderImmersion, 0.05f);
            Assert.Less(midShaderImmersion, 0.95f);

            env.StepTransitions(0.60f);
            Assert.AreEqual(1f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
            Assert.AreEqual(1f, env.DomeMaterial.GetFloat("_Immersion"), 1e-5f);

            // Environment -> Camera opens portal smoothly before deactivating the dome.
            env.SetIsolated(false);
            Assert.IsFalse(EnvironmentManager.IsIsolated);
            Assert.IsTrue(env.DomeObject.activeSelf);
            Assert.IsTrue(env.IsTransitioning);
            Assert.AreEqual(-1f, env.DomeMaterial.GetFloat("_RevealDirection"), 1e-5f);

            env.StepTransitions(0.35f);
            Assert.Greater(env.Immersion, 0.05f);
            Assert.Less(env.Immersion, 0.95f);
            Assert.IsTrue(env.DomeObject.activeSelf);

            env.StepTransitions(0.50f);
            Assert.AreEqual(0f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
            Assert.IsFalse(env.DomeObject.activeSelf);
        }

        [Test]
        public void SetIsolated_ReversingMidTransitionContinuesWithoutJump()
        {
            var env = CreateManager();

            // Reverse during Camera -> Environment enter.
            env.SetIsolated(true);
            env.StepTransitions(0.42f);
            var midEnter = env.Immersion;
            Assert.Greater(midEnter, 0.2f);
            Assert.Less(midEnter, 0.8f);

            env.SetIsolated(false);
            Assert.AreEqual(midEnter, env.Immersion, 1e-5f);
            env.StepTransitions(0.15f);
            Assert.Less(env.Immersion, midEnter);
            Assert.IsTrue(env.DomeObject.activeSelf);
            env.StepTransitions(0.80f);
            Assert.AreEqual(0f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.DomeObject.activeSelf);

            // Reverse during Environment -> Camera exit.
            env.SetIsolated(true, force: true);
            env.SetIsolated(false);
            env.StepTransitions(0.30f);
            var midExit = env.Immersion;
            Assert.Greater(midExit, 0.2f);
            Assert.Less(midExit, 0.8f);

            env.SetIsolated(true);
            Assert.AreEqual(midExit, env.Immersion, 1e-5f);
            env.StepTransitions(0.20f);
            Assert.Greater(env.Immersion, midExit);
            env.StepTransitions(1.0f);
            Assert.AreEqual(1f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
        }

        [Test]
        public void CycleEnvironment_WhileIsolatedRunsSmoothCrossfadeAndCleansUp()
        {
            var env = CreateManager();
            env.SetIsolated(true, force: true);
            Assert.AreEqual(1f, env.Crossfade, 1e-5f);

            env.CycleEnvironment();
            Assert.AreEqual(0f, env.Crossfade, 1e-5f);
            Assert.IsTrue(env.IsTransitioning);
            Assert.IsNotNull(env.DomeMaterial.GetTexture("_FromMap"));
            Assert.IsNotNull(env.DomeMaterial.GetTexture("_BaseMap"));

            env.StepTransitions(0.39f);
            Assert.Greater(env.Crossfade, 0.1f);
            Assert.Less(env.Crossfade, 0.9f);
            var midCf = env.DomeMaterial.GetFloat("_Crossfade");
            Assert.Greater(midCf, 0.05f);
            Assert.Less(midCf, 0.95f);

            env.StepTransitions(0.50f);
            Assert.AreEqual(1f, env.Crossfade, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
            Assert.AreEqual(1f, env.DomeMaterial.GetFloat("_Crossfade"), 1e-5f);
            Assert.IsNull(env.DomeMaterial.GetTexture("_FromMap"));
        }

        [Test]
        public void CycleEnvironment_WhileNotIsolatedEntersWithImmersionAnimation()
        {
            var env = CreateManager();
            Assert.IsFalse(EnvironmentManager.IsIsolated);

            env.CycleEnvironment();
            Assert.IsTrue(EnvironmentManager.IsIsolated);
            Assert.IsTrue(env.DomeObject.activeSelf);
            Assert.AreEqual(0f, env.Immersion, 1e-5f);
            Assert.AreEqual(1f, env.Crossfade, 1e-5f);
            Assert.IsTrue(env.IsTransitioning);

            env.StepTransitions(1.0f);
            Assert.AreEqual(1f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
        }

        [Test]
        public void SetIsolated_ForceSnapsImmediatelyWithoutTransition()
        {
            var env = CreateManager();

            env.SetIsolated(true, force: true);
            Assert.AreEqual(1f, env.Immersion, 1e-5f);
            Assert.AreEqual(1f, env.Crossfade, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
            Assert.IsTrue(env.DomeObject.activeSelf);
            Assert.AreEqual(1f, env.DomeMaterial.GetFloat("_Immersion"), 1e-5f);

            env.SetIsolated(false, force: true);
            Assert.AreEqual(0f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
            Assert.IsFalse(env.DomeObject.activeSelf);
            Assert.AreEqual(0f, env.DomeMaterial.GetFloat("_Immersion"), 1e-5f);
        }

        [Test]
        public void SelectEnvironment_ActivatesSpecificPresetAndCrossfadesWhenSwitching()
        {
            var env = CreateManager();
            Assert.IsFalse(EnvironmentManager.IsIsolated);

            env.SelectEnvironment(4);
            Assert.AreEqual(4, EnvironmentManager.CurrentIndex);
            Assert.IsTrue(EnvironmentManager.IsIsolated);
            Assert.IsTrue(env.IsTransitioning);

            env.StepTransitions(1.0f);
            Assert.AreEqual(1f, env.Immersion, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);

            env.SelectEnvironment(7);
            Assert.AreEqual(7, EnvironmentManager.CurrentIndex);
            Assert.AreEqual(0f, env.Crossfade, 1e-5f);
            Assert.IsTrue(env.IsTransitioning);

            env.StepTransitions(1.0f);
            Assert.AreEqual(1f, env.Crossfade, 1e-5f);
            Assert.IsFalse(env.IsTransitioning);
        }

        [Test]
        public void EnvironmentPresets_AreValidPolyHavenStartersAndIncludeProceduralCinema()
        {
            Assert.GreaterOrEqual(EnvironmentManager.EnvironmentCount, 8);
            var hasCinema = false;
            for (var i = 0; i < EnvironmentManager.EnvironmentCount; i++)
            {
                var id = EnvironmentManager.GetPresetId(i);
                var title = EnvironmentManager.GetPresetTitle(i);
                Assert.IsNotEmpty(id, $"Missing preset ID at {i}");
                Assert.IsNotEmpty(title, $"Missing preset title at {i}");

                if (EnvironmentManager.IsPresetProcedural(i))
                {
                    hasCinema = true;
                    continue;
                }

                var thumbUrl = EnvironmentManager.GetPresetThumbUrl(i);
                Assert.IsTrue(thumbUrl.StartsWith("https://cdn.polyhaven.com/asset_img/primary/"),
                    $"Unexpected cover URL for {id}: {thumbUrl}");
                Assert.IsTrue(thumbUrl.Contains("format=jpg"),
                    $"Cover URL must request JPEG for Unity decoding: {thumbUrl}");
            }
            Assert.IsTrue(hasCinema, "Expected procedural Sala Cinema fallback in starter environments.");
        }
    }
}
