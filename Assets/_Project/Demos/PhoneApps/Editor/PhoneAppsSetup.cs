using CardboardXR.Demos;
using CardboardXR.Demos.PhoneApps;
using UnityEngine;

namespace ProjectBootstrap
{
    // App Telefono: opens up to 3 real Android phone apps simultaneously via Shizuku
    // VirtualDisplays in rotatable portrait/landscape spatial windows with full pinch control.
    class PhoneAppsSetup : DemoSetup
    {
        public override string Name => "PhoneApps";
        public override int Order => 26;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "phoneapps",
            Title = "App Telefono",
            Description = "Apri fino a 3 app reali del telefono (Chrome, TikTok, WhatsApp, ecc.) in finestre verticali o orizzontali tramite Shizuku, con tocco e scorrimento pinch 1:1.",
            Tagline = "App del telefono in VR con Shizuku, verticali e ruotabili",
            Icon = "smartphone",
            Tint = new Color(0.16f, 0.56f, 0.42f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("PhoneApps Demo").AddComponent<PhoneAppsDemo>();
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("PhoneApps Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
