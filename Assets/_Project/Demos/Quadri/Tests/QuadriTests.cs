using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Quadri.Tests
{
    public class QuadriTests
    {
        [Test]
        public void ComputeWindowSize_PreservesAspectWithinBounds()
        {
            var landscape = QuadriLayout.ComputeWindowSize(1920, 1080);
            Assert.AreEqual(600f, landscape.x, 1e-3f);
            Assert.AreEqual(338f, landscape.y, 1.5f);

            var portrait = QuadriLayout.ComputeWindowSize(1080, 1920);
            Assert.AreEqual(580f, portrait.y, 1e-3f);
            Assert.AreEqual(326f, portrait.x, 1.5f);

            var square = QuadriLayout.ComputeWindowSize(1000, 1000);
            Assert.AreEqual(580f, square.x, 1e-3f);
            Assert.AreEqual(580f, square.y, 1e-3f);

            var extreme = QuadriLayout.ComputeWindowSize(4000, 100);
            Assert.LessOrEqual(extreme.x / extreme.y, 2.45f);

            var invalid = QuadriLayout.ComputeWindowSize(0, -10);
            Assert.AreEqual(600f, invalid.x, 1e-3f);
            Assert.AreEqual(450f, invalid.y, 1e-3f);
        }

        [Test]
        public void ComputeCenterCropUv_CentersCropCleanly()
        {
            var wideUv = QuadriLayout.ComputeCenterCropUv(2000, 1000, 1f);
            Assert.AreEqual(0.25f, wideUv.x, 1e-4f);
            Assert.AreEqual(0f, wideUv.y, 1e-4f);
            Assert.AreEqual(0.5f, wideUv.width, 1e-4f);
            Assert.AreEqual(1f, wideUv.height, 1e-4f);

            var tallUv = QuadriLayout.ComputeCenterCropUv(1000, 2000, 1f);
            Assert.AreEqual(0f, tallUv.x, 1e-4f);
            Assert.AreEqual(0.25f, tallUv.y, 1e-4f);
            Assert.AreEqual(1f, tallUv.width, 1e-4f);
            Assert.AreEqual(0.5f, tallUv.height, 1e-4f);
        }

        [Test]
        public void PageCount_HandlesBoundaries()
        {
            Assert.AreEqual(1, QuadriLayout.PageCount(0));
            Assert.AreEqual(1, QuadriLayout.PageCount(1));
            Assert.AreEqual(1, QuadriLayout.PageCount(12));
            Assert.AreEqual(2, QuadriLayout.PageCount(13));
            Assert.AreEqual(3, QuadriLayout.PageCount(25));
        }

        [Test]
        public void QuadriEntry_IsRegisteredAsWindowedApp()
        {
            Assert.IsTrue(DemoManager.IsWindowedApp(new DemoCatalog.Entry { Id = "quadri" }));
        }
    }
}
