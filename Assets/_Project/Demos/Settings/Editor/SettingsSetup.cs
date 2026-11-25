using CardboardXR.Demos;
using CardboardXR.Demos.Settings;
using UnityEngine;

namespace ProjectBootstrap
{
    // Impostazioni: app per scegliere la fotocamera passthrough (Principale vs Grandangolare 0.5x).
    class SettingsSetup : DemoSetup
    {
        public override string Name => "Settings";
        public override int Order => 60;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "settings",
            Title = "Impostazioni",
            Description = "Configura la fotocamera passthrough tra obiettivo principale (6DoF) e grandangolare 0.5x per un riquadro molto più grande.",
            Tagline = "Scegli tra fotocamera principale e grandangolare 0.5x",
            Icon = "settings",
            Tint = new Color(0.32f, 0.42f, 0.52f),
            Requirements = new DemoRequirements { SixDof = Need.Any, Passthrough = Need.On, Hands = Need.Off, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("Settings Demo").AddComponent<SettingsDemo>();
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Settings Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
