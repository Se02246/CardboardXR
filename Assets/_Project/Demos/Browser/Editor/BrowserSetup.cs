using CardboardXR.Demos;
using CardboardXR.Demos.Browser;
using UnityEngine;

namespace ProjectBootstrap
{
    // Web Cinema: floating desktop browser windows with quick links for YouTube, Netflix, Prime Video,
    // Disney+, Twitch, Plex and Google, in AR passthrough or a dark VR theater.
    class BrowserSetup : DemoSetup
    {
        public override string Name => "Browser";
        public override int Order => 25;

        public override DemoCatalog.Entry Entry => new()
        {
            Id = "browser",
            Title = "Web Cinema",
            Description = "Open floating desktop browser windows in your room or in a dark cinema, with quick links for YouTube, Netflix, Prime Video, Disney+, Twitch and Plex. Look and double-pinch on the watch to click.",
            Tagline = "Floating desktop browser windows for streaming and the web",
            Icon = "movie",
            Tint = new Color(0.18f, 0.42f, 0.72f),
            Requirements = new DemoRequirements { SixDof = Need.On, Passthrough = Need.On, Hands = Need.On, HandPinch = false },
        };

        public override void BuildScene(string folder)
        {
            new GameObject("Browser Demo").AddComponent<BrowserDemo>();
            var environment = DemoAssets.Environment();
            var grid = StereoBuilder.SaveAsset(DemoAssets.CreateGridTexture(), $"{folder}/Grid.asset");
            const float floorSize = 30f;
            DemoAssets.Floor(environment.transform,
                StereoBuilder.CreateMaterial("Browser Floor", DemoAssets.Unlit, Color.white, grid, floorSize / 0.5f, folder), floorSize);
        }
    }
}
