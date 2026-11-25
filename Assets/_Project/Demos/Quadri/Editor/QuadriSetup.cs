using CardboardXR.Demos;
using CardboardXR.Demos.Quadri;
using UnityEngine;

namespace ProjectBootstrap
{
    // Quadri: galleria spaziale per scegliere foto dal telefono e posizionarle in giro per la stanza.
    class QuadriSetup : DemoSetup
    {
        public override string Name => "Quadri";
        public override int Order => 55;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "quadri",
            Title = "Quadri",
            Description = "Seleziona foto dalla galleria del telefono e posizionale come finestre pulite in giro per la stanza.",
            Tagline = "Metti le tue foto come quadri spaziali nella stanza",
            Icon = "photo_library",
            Tint = new Color(0.56f, 0.36f, 0.78f),
            Requirements = new DemoRequirements { SixDof = Need.Any, Passthrough = Need.On, Hands = Need.Off, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("Quadri Demo").AddComponent<QuadriDemo>();
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Quadri Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
