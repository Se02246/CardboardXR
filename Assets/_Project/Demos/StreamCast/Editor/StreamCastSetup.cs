using CardboardXR.Demos;
using CardboardXR.Demos.StreamCast;
using UnityEngine;

namespace ProjectBootstrap
{
    // Schermi Live: ricevitore multi-stream (fino a 4 schermi simultanei) per Windows, macOS,
    // Linux, iPadOS, iOS, Android e AirPlay Video, in AR passthrough o sala cinema VR.
    class StreamCastSetup : DemoSetup
    {
        public override string Name => "StreamCast";
        public override int Order => 28;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "streamcast",
            Title = "Schermi Live",
            Description = "Ricevi fino a 4 stream simultanei da Windows, macOS, iPhone, iPad e Android su schermi 3D fluttuanti (con audio, spostabili nello spazio).",
            Tagline = "Ricevi schermi e video live da PC, Mac, iPad e iPhone",
            Icon = "cast",
            Tint = new Color(0.12f, 0.52f, 0.58f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("StreamCast Demo").AddComponent<StreamCastDemo>();
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("StreamCast Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
