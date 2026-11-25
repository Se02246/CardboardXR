using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.PhoneApps.Tests
{
    public class PhoneAppsTests
    {
        [Test]
        public void DefaultPortraitDimensionsAreTallerThanWide()
        {
            var panel = PhoneAppsRules.PanelSize(landscape: false);
            var screen = PhoneAppsRules.ScreenSize(landscape: false);
            var display = PhoneAppsRules.DisplaySize(landscape: false);

            Assert.Greater(panel.y, panel.x);
            Assert.Greater(screen.y, screen.x);
            Assert.Greater(display.y, display.x);
            Assert.Less(screen.x, panel.x);
            Assert.Less(screen.y, panel.y);
        }

        [Test]
        public void LandscapeDimensionsAreWiderThanTallAndFitDirectBuffer()
        {
            var panel = PhoneAppsRules.PanelSize(landscape: true);
            var screen = PhoneAppsRules.ScreenSize(landscape: true);
            var displayPortrait = PhoneAppsRules.DisplaySize(landscape: false);
            var displayLandscape = PhoneAppsRules.DisplaySize(landscape: true);

            Assert.Greater(panel.x, panel.y);
            Assert.Greater(screen.x, screen.y);
            Assert.Greater(displayLandscape.x, displayLandscape.y);

            Assert.LessOrEqual(displayPortrait.x * displayPortrait.y * 4, PhoneAppsRules.MaxBufferBytes);
            Assert.LessOrEqual(displayLandscape.x * displayLandscape.y * 4, PhoneAppsRules.MaxBufferBytes);
        }

        [Test]
        public void TopBarClearsTopEdgeInBothOrientations()
        {
            Assert.Greater(PhoneAppsRules.TopBarY(false) - 28f, PhoneAppsRules.PanelSize(false).y * 0.5f);
            Assert.Greater(PhoneAppsRules.TopBarY(true) - 28f, PhoneAppsRules.PanelSize(true).y * 0.5f);
        }

        [Test]
        public void RectToNormalizedMapsCornersAndClamps()
        {
            var rect = new Rect(-200f, -386f, 400f, 772f);

            var topLeft = PhoneAppsRules.RectToNormalized(rect, new Vector2(-200f, 386f));
            var center = PhoneAppsRules.RectToNormalized(rect, Vector2.zero);
            var bottomRight = PhoneAppsRules.RectToNormalized(rect, new Vector2(200f, -386f));
            var clamped = PhoneAppsRules.RectToNormalized(rect, new Vector2(-999f, 999f));

            Assert.AreEqual(0f, topLeft.x, 1e-4f);
            Assert.AreEqual(0f, topLeft.y, 1e-4f);
            Assert.AreEqual(0.5f, center.x, 1e-4f);
            Assert.AreEqual(0.5f, center.y, 1e-4f);
            Assert.AreEqual(1f, bottomRight.x, 1e-4f);
            Assert.AreEqual(1f, bottomRight.y, 1e-4f);
            Assert.AreEqual(0f, clamped.x, 1e-4f);
            Assert.AreEqual(0f, clamped.y, 1e-4f);
        }

        [Test]
        public void WindowSlotYawsAreDistinctAndClearCenterLauncher()
        {
            var y0 = PhoneAppsRules.WindowSlotYaw(0);
            var y1 = PhoneAppsRules.WindowSlotYaw(1);
            var y2 = PhoneAppsRules.WindowSlotYaw(2);

            Assert.AreNotEqual(0f, y0);
            Assert.AreNotEqual(0f, y1);
            Assert.AreNotEqual(y0, y1);
            Assert.AreNotEqual(y0, y2);
            Assert.AreNotEqual(y1, y2);
            Assert.GreaterOrEqual(Mathf.Abs(y0), 28f);
            Assert.GreaterOrEqual(Mathf.Abs(y1), 28f);
        }

        [Test]
        public void PaginationCountsAndClampsSafely()
        {
            Assert.AreEqual(1, PhoneAppsRules.PageCount(0));
            Assert.AreEqual(1, PhoneAppsRules.PageCount(1));
            Assert.AreEqual(1, PhoneAppsRules.PageCount(12));
            Assert.AreEqual(2, PhoneAppsRules.PageCount(13));
            Assert.AreEqual(4, PhoneAppsRules.PageCount(37));

            Assert.AreEqual(0, PhoneAppsRules.ClampPage(-3, 25));
            Assert.AreEqual(2, PhoneAppsRules.ClampPage(2, 25));
            Assert.AreEqual(2, PhoneAppsRules.ClampPage(99, 25));
            Assert.AreEqual(0, PhoneAppsRules.ClampPage(5, 0));
        }

        [Test]
        public void FeaturedPriorityRanksPopularAppsAheadOfOthers()
        {
            Assert.Less(PhoneAppsRules.FeaturedPriority("com.android.chrome"), PhoneAppsRules.FeaturedPriority("com.example.unknown"));
            Assert.Less(PhoneAppsRules.FeaturedPriority("com.zhiliaoapp.musically"), PhoneAppsRules.FeaturedPriority("com.example.unknown"));
            Assert.Less(PhoneAppsRules.FeaturedPriority("com.whatsapp"), PhoneAppsRules.FeaturedPriority("com.example.unknown"));
        }

        [Test]
        public void InitialsFormatsLabelsCleanly()
        {
            Assert.AreEqual("CH", PhoneAppsRules.Initials("Chrome"));
            Assert.AreEqual("GP", PhoneAppsRules.Initials("Google Photos"));
            Assert.AreEqual("X", PhoneAppsRules.Initials("X"));
            Assert.AreEqual("AP", PhoneAppsRules.Initials(""));
        }

        [Test]
        public void PhoneAppsIsRegisteredAsConcurrentWindowedApp()
        {
            var entry = new DemoCatalog.Entry { Id = "phoneapps", Title = "App Telefono" };
            Assert.IsTrue(DemoManager.IsWindowedApp(entry));
        }

        [Test]
        public void ShortcutStoreRoundTripsStarredAppsAndDeduplicates()
        {
            var input = new List<StarredPhoneApp>
            {
                new() { PackageName = "com.android.chrome", ActivityName = "Main", Label = "Chrome", ColorRgb = 0x1A73E8 },
                new() { PackageName = "com.whatsapp", ActivityName = "", Label = "WhatsApp", ColorRgb = 0x25D366 },
            };

            var serialized = PhoneAppShortcutStore.Serialize(input);
            var restored = PhoneAppShortcutStore.Deserialize(serialized + "\n" + serialized + "\nbadline");

            Assert.AreEqual(2, restored.Count);
            Assert.AreEqual("com.android.chrome", restored[0].PackageName);
            Assert.AreEqual("Main", restored[0].ActivityName);
            Assert.AreEqual("Chrome", restored[0].Label);
            Assert.AreEqual(0x1A73E8, restored[0].ColorRgb);
            Assert.AreEqual("com.whatsapp", restored[1].PackageName);
            Assert.AreEqual("phoneapp:com.whatsapp", restored[1].ItemId);
        }

        [Test]
        public void AppLibraryOrderAppliesSavedCsvRemovesObsoleteAndAppendsNew()
        {
            var defaults = new[] { "lobby", "browser", "video", "phoneapp:com.whatsapp" };
            var ordered = AppLibrary.ApplySavedOrder(defaults, "phoneapp:com.whatsapp,sandbox,browser,rush");

            CollectionAssert.AreEqual(new[] { "phoneapp:com.whatsapp", "browser", "lobby", "video" }, ordered);
        }

        [Test]
        public void MoveItemShiftsEntriesAndRejectsOutOfBounds()
        {
            var items = new List<string> { "a", "b", "c", "d" };

            Assert.IsFalse(AppLibrary.MoveItem(items, 0, -1));
            Assert.IsFalse(AppLibrary.MoveItem(items, 3, 1));
            Assert.IsTrue(AppLibrary.MoveItem(items, 1, 1));
            CollectionAssert.AreEqual(new[] { "a", "c", "b", "d" }, items);
            Assert.IsTrue(AppLibrary.MoveItem(items, 2, -2));
            CollectionAssert.AreEqual(new[] { "b", "a", "c", "d" }, items);
        }

        [Test]
        public void FullscreenExitLookDownHysteresis_ShowsAndHidesCleanly()
        {
            Assert.IsFalse(PhoneAppsRules.ShouldShowFullscreenExit(false, 30f));
            Assert.IsFalse(PhoneAppsRules.ShouldShowFullscreenExit(false, 45f));
            Assert.IsTrue(PhoneAppsRules.ShouldShowFullscreenExit(false, 46f));

            // Hysteresis band keeps the button open between 34 and 46 deg once shown.
            Assert.IsTrue(PhoneAppsRules.ShouldShowFullscreenExit(true, 40f));
            Assert.IsTrue(PhoneAppsRules.ShouldShowFullscreenExit(true, 34f));
            Assert.IsFalse(PhoneAppsRules.ShouldShowFullscreenExit(true, 33f));
        }

        [Test]
        public void TopBarWidthFitsAllFourButtonsWithoutOverlap()
        {
            Assert.GreaterOrEqual(PhoneAppsRules.TopBarWidth(landscape: true), 748f);
            Assert.GreaterOrEqual(PhoneAppsRules.TopBarWidth(landscape: false), 420f);
            Assert.Less(PhoneAppsRules.VrFullscreenTapPoint.x, 0.45f);
            Assert.Greater(PhoneAppsRules.VrFullscreenTapPoint.x, 0.05f);
            Assert.AreEqual(0.5f, PhoneAppsRules.VrFullscreenTapPoint.y, 1e-4f);
        }

        [Test]
        public void ReplacePopupPromptsOnlyAtMaxCapacityForNewPackage()
        {
            Assert.IsFalse(PhoneAppsRules.ShouldPromptReplaceWindow(0, targetAlreadyOpen: false));
            Assert.IsFalse(PhoneAppsRules.ShouldPromptReplaceWindow(2, targetAlreadyOpen: false));
            Assert.IsTrue(PhoneAppsRules.ShouldPromptReplaceWindow(3, targetAlreadyOpen: false));
            Assert.IsFalse(PhoneAppsRules.ShouldPromptReplaceWindow(3, targetAlreadyOpen: true));
        }

        [Test]
        public void DirectLaunchPackageRejectsEmptyAndDispatchesTrimmedPackage()
        {
            var prevHandler = PhoneAppShortcutStore.DirectLaunchHandler;
            var prevPending = PhoneAppShortcutStore.PendingDirectLaunch;
            try
            {
                PhoneAppShortcutStore.DirectLaunchHandler = null;
                PhoneAppShortcutStore.PendingDirectLaunch = null;

                Assert.IsFalse(PhoneAppShortcutStore.LaunchPackage(null, ""));
                Assert.IsFalse(PhoneAppShortcutStore.LaunchPackage(null, "com.whatsapp", "WhatsApp"));

                StarredPhoneApp? received = null;
                PhoneAppShortcutStore.DirectLaunchHandler = app => { received = app; return true; };

                Assert.IsTrue(PhoneAppShortcutStore.LaunchPackage(null, " com.whatsapp ", "WhatsApp"));
                Assert.IsTrue(received.HasValue);
                Assert.AreEqual("com.whatsapp", received.Value.PackageName);
                Assert.AreEqual("WhatsApp", received.Value.Label);
            }
            finally
            {
                PhoneAppShortcutStore.DirectLaunchHandler = prevHandler;
                PhoneAppShortcutStore.PendingDirectLaunch = prevPending;
            }
        }

        [Test]
        public void VrBrowserRouterNormalizesUrlAndRoutesInsideVr()
        {
            var prevHandler = PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler;
            var prevPending = PhoneAppShortcutStore.VrBrowserRouter.PendingUrl;
            try
            {
                PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler = null;
                PhoneAppShortcutStore.VrBrowserRouter.PendingUrl = null;

                Assert.IsFalse(PhoneAppShortcutStore.VrBrowserRouter.OpenUrl(null, ""));
                Assert.IsFalse(PhoneAppShortcutStore.VrBrowserRouter.OpenUrl(null, "   "));

                string opened = null;
                PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler = url => { opened = url; return true; };

                Assert.IsTrue(PhoneAppShortcutStore.VrBrowserRouter.OpenUrl(null, "sketchfab.com/settings/password"));
                Assert.AreEqual("https://sketchfab.com/settings/password", opened);
            }
            finally
            {
                PhoneAppShortcutStore.VrBrowserRouter.DirectOpenUrlHandler = prevHandler;
                PhoneAppShortcutStore.VrBrowserRouter.PendingUrl = prevPending;
            }
        }

        [Test]
        public void WidgetEntryIsRemovedFromWindowedCatalog()
        {
            Assert.IsFalse(DemoManager.IsWindowedApp(new DemoCatalog.Entry { Id = "widget" }));
        }
    }
}
