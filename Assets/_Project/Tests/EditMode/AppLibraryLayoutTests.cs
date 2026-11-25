using CardboardXR.Demos;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    // The app library pages instead of growing taller, so the settings panel below it stays put.
    public class AppLibraryLayoutTests
    {
        [Test]
        public void SixAppsFitOnOnePageAsBefore()
        {
            Assert.AreEqual(1, AppLibrary.Pages(6));
            // The lobby and five demos: 3 by 2, with no arrows or dots.
            Assert.AreEqual(new Vector2(432f, 324f), AppLibrary.Size(6));
            Assert.AreEqual(AppLibrary.Size(4).y, AppLibrary.Size(6).y);
            Assert.Less(AppLibrary.Size(3).y, AppLibrary.Size(4).y);
        }

        [Test]
        public void MoreAppsTurnPagesInsteadOfGrowing()
        {
            Assert.AreEqual(2, AppLibrary.Pages(7));
            Assert.AreEqual(2, AppLibrary.Pages(12));
            Assert.AreEqual(3, AppLibrary.Pages(13));
            Assert.AreEqual(AppLibrary.Size(7), AppLibrary.Size(30));
            // Only the page dots' row taller, and the arrows' width wider.
            Assert.AreEqual(14f, AppLibrary.Size(7).y - AppLibrary.Size(6).y, 1e-4f);
            Assert.AreEqual(2f * (56f + 8f), AppLibrary.Size(7).x - AppLibrary.Size(6).x, 1e-4f);
        }

        [Test]
        public void AppsOnLaterPagesTakeTheSamePlaces()
        {
            Assert.AreEqual(0, AppLibrary.PageOf(5));
            Assert.AreEqual(1, AppLibrary.PageOf(6));
            Assert.AreEqual(2, AppLibrary.PageOf(12));
            for (var slot = 0; slot < 6; slot++)
                Assert.AreEqual(AppLibrary.CellPosition(slot, 13), AppLibrary.CellPosition(slot + 12, 13));
        }

        [Test]
        public void CellsStayInsideAndClearOfTheArrows()
        {
            foreach (var apps in new[] { 1, 6, 7, 13 })
            {
                var size = AppLibrary.Size(apps);
                var inner = size.x / 2f - 16f - (AppLibrary.Pages(apps) > 1 ? 56f + 8f : 0f);
                for (var i = 0; i < apps; i++)
                {
                    var cell = AppLibrary.CellPosition(i, apps);
                    Assert.LessOrEqual(Mathf.Abs(cell.x) + AppLibrary.k_CellWidth / 2f, inner + 1e-3f, $"{apps} apps, cell {i}");
                    Assert.LessOrEqual(cell.y + AppLibrary.k_CellHeight / 2f, size.y / 2f - 16f + 1e-3f, $"{apps} apps, cell {i}");
                    Assert.Greater(cell.y - AppLibrary.k_CellHeight / 2f, AppLibrary.TaglinePosition(size).y, $"{apps} apps, cell {i}");
                }
            }
        }

        [Test]
        public void FewerRowsMakeMorePages()
        {
            Assert.AreEqual(3, AppLibrary.PerPage(1));
            Assert.AreEqual(2, AppLibrary.Pages(6, rows: 1));
            Assert.AreEqual(1, AppLibrary.PageOf(3, rows: 1));
            Assert.Less(AppLibrary.Size(6, rows: 1).y, AppLibrary.Size(6).y);
        }

        [Test]
        public void HoneycombAppsGrid_Uses454FormationAndPaginatesCleanly()
        {
            Assert.AreEqual(1, AppLibrary.HoneycombPages(13));
            Assert.AreEqual(2, AppLibrary.HoneycombPages(14));
            Assert.AreEqual(0, AppLibrary.HoneycombPageOf(12));
            Assert.AreEqual(1, AppLibrary.HoneycombPageOf(13));

            var size13 = AppLibrary.HoneycombSize(13);
            var positions = new Vector2[13];
            for (var i = 0; i < 13; i++)
            {
                positions[i] = AppLibrary.HoneycombCellPosition(i, 13);
                Assert.LessOrEqual(Mathf.Abs(positions[i].x) + AppLibrary.k_CellWidth * 0.5f,
                    size13.x * 0.5f - 15.9f, $"App {i} overflowed horizontally");
                Assert.LessOrEqual(positions[i].y + AppLibrary.k_CellHeight * 0.5f,
                    size13.y * 0.5f - 15.9f, $"App {i} overflowed top");
                Assert.Greater(positions[i].y - AppLibrary.k_CellHeight * 0.5f,
                    AppLibrary.TaglinePosition(size13).y, $"App {i} overlapped tagline");
            }

            // Middle row center tile (index 6 in 4-5-4) is horizontally centered at x = 0.
            Assert.AreEqual(0f, positions[6].x, 1e-4f);
            // Row 0 has 4 tiles (same y), Row 1 has 5 tiles (same y), Row 2 has 4 tiles (same y).
            Assert.AreEqual(positions[0].y, positions[3].y, 1e-4f);
            Assert.Greater(positions[0].y, positions[4].y);
            Assert.AreEqual(positions[4].y, positions[8].y, 1e-4f);
            Assert.Greater(positions[4].y, positions[9].y);
            Assert.AreEqual(positions[9].y, positions[12].y, 1e-4f);

            // Multi-page honeycomb keeps cells clear of side arrows.
            var size14 = AppLibrary.HoneycombSize(14);
            var inner14 = size14.x * 0.5f - 16f - (56f + 8f);
            for (var i = 0; i < 14; i++)
            {
                var pos = AppLibrary.HoneycombCellPosition(i, 14);
                Assert.LessOrEqual(Mathf.Abs(pos.x) + AppLibrary.k_CellWidth * 0.5f, inner14 + 1e-3f);
            }
        }
    }
}
