using CardboardXR.Demos.Video;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Browser.Tests
{
    public class BrowserTests
    {
        [Test]
        public void RectToNormalizedMapsCornersAndCenter()
        {
            var rect = new Rect(-480f, -270f, 960f, 540f);
            // Center -> (0.5, 0.5)
            var center = BrowserRules.RectToNormalized(rect, Vector2.zero);
            Assert.AreEqual(0.5f, center.x, 1e-4f);
            Assert.AreEqual(0.5f, center.y, 1e-4f);

            // Top-left in Unity UI (+Y is up) -> web viewport (0, 0)
            var topLeft = BrowserRules.RectToNormalized(rect, new Vector2(-480f, 270f));
            Assert.AreEqual(0f, topLeft.x, 1e-4f);
            Assert.AreEqual(0f, topLeft.y, 1e-4f);

            // Bottom-right in Unity UI -> web viewport (1, 1)
            var bottomRight = BrowserRules.RectToNormalized(rect, new Vector2(480f, -270f));
            Assert.AreEqual(1f, bottomRight.x, 1e-4f);
            Assert.AreEqual(1f, bottomRight.y, 1e-4f);
        }

        [Test]
        public void WindowSlotsStayCenteredAroundZeroYaw()
        {
            Assert.AreEqual(0f, BrowserRules.WindowSlotYaw(0, 1), 1e-4f);
            Assert.AreEqual(-19f, BrowserRules.WindowSlotYaw(0, 2, 38f), 1e-4f);
            Assert.AreEqual(19f, BrowserRules.WindowSlotYaw(1, 2, 38f), 1e-4f);
            Assert.AreEqual(-38f, BrowserRules.WindowSlotYaw(0, 3, 38f), 1e-4f);
            Assert.AreEqual(0f, BrowserRules.WindowSlotYaw(1, 3, 38f), 1e-4f);
            Assert.AreEqual(38f, BrowserRules.WindowSlotYaw(2, 3, 38f), 1e-4f);
        }

        [Test]
        public void ShortcutsIncludeStreamingServices()
        {
            Assert.GreaterOrEqual(BrowserRules.Shortcuts.Length, 5);
            Assert.AreEqual("YouTube", BrowserRules.Shortcuts[0].Label);
            Assert.AreEqual("Netflix", BrowserRules.Shortcuts[1].Label);
            Assert.AreEqual("Prime Video", BrowserRules.Shortcuts[2].Label);
            var last = BrowserRules.Shortcuts[BrowserRules.Shortcuts.Length - 1];
            Assert.AreEqual("Cerca", last.Label);
            Assert.AreEqual("https://www.google.com", last.Url);
        }

        [Test]
        public void DetectsVr360TitlesAndUrlsWithoutFalsePositives()
        {
            Assert.IsTrue(BrowserRules.IsVr360TitleOrUrl("Alpine Helicopter Tour 360° - YouTube", "https://www.youtube.com/watch?v=123"));
            Assert.IsTrue(BrowserRules.IsVr360TitleOrUrl("Japan Walk 360", "https://example.com"));
            Assert.IsTrue(BrowserRules.IsVr360TitleOrUrl("Ocean Dive", "https://example.com/vr180/video"));
            Assert.IsTrue(BrowserRules.IsVr360TitleOrUrl("Space 3D SBS Experience", "https://example.com"));
            Assert.IsTrue(BrowserRules.IsVr360TitleOrUrl("Panorama 180°", "https://example.com"));

            Assert.IsFalse(BrowserRules.IsVr360TitleOrUrl("YouTube - Home", "https://www.youtube.com"));
            Assert.IsFalse(BrowserRules.IsVr360TitleOrUrl("Countdown 3600 seconds", "https://example.com/1800"));
        }

        [Test]
        public void InfersExpectedVrVideoFormatFromTitleAndUrl()
        {
            Assert.AreEqual(VideoFormat.Mono360,
                BrowserRules.InferVrVideoFormat("Mountain 360° Video - YouTube", "https://www.youtube.com/watch?v=abc"));
            Assert.AreEqual(new VideoFormat(Projection.Half180, StereoLayout.SideBySide),
                BrowserRules.InferVrVideoFormat("Concert VR180", "https://example.com/vr180/live"));
            Assert.AreEqual(new VideoFormat(Projection.Full360, StereoLayout.TopBottom),
                BrowserRules.InferVrVideoFormat("Spaceflight 360 TB", "https://example.com/watch"));
        }
    }
}
