using CardboardXR.Interaction;
using CardboardXR.Stereo;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Hands
{
    // Where the hand's pinches come from: hand tracking in 6DoF, which knows where the hand is, the
    // 3DoF pinch sensor, which only sees that a pinch happened, or the Wear OS watch app's double
    // pinch (WatchLink), which doesn't know where the hand is either and never holds.
    public enum PinchSource
    {
        Hand,
        Camera,
        Watch,
    }

    // What a pinch did (PinchRouter.Order).
    public enum PinchRoute
    {
        Settings,
        Button,
        Demo,
        Grab,
        Click,
        Ignored,
        Scroll,
    }

    // Takes pinches that would otherwise click, such as painting in the air (PinchRouter.Handler).
    public interface IPinchHandler
    {
        // A pinch began with the gaze on gazeTarget (or nothing): true to take it, false to let it go on.
        // A Camera pinch has no hand position, so a handler that needs one declines it.
        bool TryBeginPinch(GameObject gazeTarget, PinchSource source);

        // The pinch it took ended: the fingers opened or the hand was lost.
        void EndPinch();
    }

    // Receives the Pixel Watch crown's rotation (+ = clockwise/scroll down/push away, - = scroll up/pull closer).
    public interface IWatchRotaryHandler
    {
        bool TryHandleRotary(GameObject gazeTarget, float rotarySteps);
    }

    // Implemented by scrollable screens/panels so rotating the Pixel Watch crown while looking at them
    // scrolls their content or turns pages without resizing the window.
    public interface IWatchScrollable
    {
        bool TryHandleWatchScroll(float rotarySteps);
    }

    // Every pinch in every scene, from either source, goes through here in one order:
    //   1. the palm's settings gear, while it shows, opens the settings panel;
    //   2. a button under the gaze is clicked, so panels work in every demo whatever it does with
    //      pinches (Orb Rush takes all of them during a round, and was swallowing the panel's);
    //   3. the scene's demo (Handler) is asked, unless the settings panel is open;
    //   4. something the hand's ray is on is picked up (6DoF);
    //   5. otherwise the gaze clicks what it's on, except a placed object in 6DoF, which only the
    //      hand takes, so looking at a cube and pinching elsewhere doesn't remove it by surprise.
    // A pinch that would click (or open settings) within k_ClickLockoutSeconds of the last click does
    // nothing: both sources sometimes see one pinch as two, opening and closing again 0.1-0.3 s apart,
    // which clicked a button two or three times (cycling the video format through three settings).
    // Demo pinches and grabs aren't locked out. Only one source runs at a time (PinchInput hands the
    // camera over between modes). While the Wear OS watch app is connected, its double pinch takes
    // the camera's place (HandPinchMuted), except in scenes that need the hand. Logs [Pinch].
    public static class PinchRouter
    {
        public const float k_ClickLockoutSeconds = 0.35f;
        const string k_VisionPinchPrefKey = "CardboardXR.VisionPinch";

        static IPinchHandler s_Claimed;
        static float s_LastClick = float.NegativeInfinity;
        static bool s_VisionPinchLoaded;
        static bool s_VisionPinchEnabled = true;

        // Whether camera-based (vision) pinch recognition is enabled by the user in Settings.
        public static bool VisionPinchEnabled
        {
            get
            {
                if (!s_VisionPinchLoaded)
                {
                    s_VisionPinchLoaded = true;
                    s_VisionPinchEnabled = PlayerPrefs.GetInt(k_VisionPinchPrefKey, 1) != 0;
                }
                return s_VisionPinchEnabled;
            }
        }

        public static void SetVisionPinchEnabled(bool enabled)
        {
            s_VisionPinchLoaded = true;
            s_VisionPinchEnabled = enabled;
            PlayerPrefs.SetInt(k_VisionPinchPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[Pinch] Vision pinch {(enabled ? "enabled" : "disabled")} in Settings");
        }

        // The current scene's taker of pinches, or null. Demos set it on entering and DemoManager
        // clears it on leaving.
        public static IPinchHandler Handler { get; set; }

        // The current scene's handler for the Pixel Watch's rotating crown, or null.
        public static IWatchRotaryHandler RotaryHandler { get; set; }

        // Whether the current scene needs the camera's pinch even with the watch connected: painting,
        // picking things up (DemoRequirements.HandPinch). DemoManager sets it on each scene change.
        public static bool SceneWantsHandPinch { get; set; }

        // While vision pinch is disabled in Settings, the camera's pinch is muted. When enabled, it
        // stays active even when the Pixel Watch is connected so visual pinch clicks and continuous
        // pinch scrolling always work seamlessly.
        public static bool HandPinchMuted => !VisionPinchEnabled;

        public static float LockoutForSource(PinchSource source, GameObject target)
        {
            if (source == PinchSource.Watch)
                return 0.06f;
            if (IsButton(target))
                return 0.18f;
            return 0.24f;
        }

        public static bool TryConsumeClickLockout()
        {
            var now = Time.unscaledTime;
            if (now - s_LastClick < k_ClickLockoutSeconds)
                return false;
            s_LastClick = now;
            return true;
        }

        // The watch's pinch (or a tap on its screen): double-click grabs a floating window/object
        // to move with the watch gyroscope, single pinch releases it, otherwise clicks once.
        public static PinchRoute WatchPinch(GazePointer gaze)
        {
            if (WatchGrabController.TryHandleWatchPinch(gaze))
                return PinchRoute.Grab;
            var route = Begin(PinchSource.Watch, gaze, false);
            End();
            return route;
        }

        // The order, pure, for testing. demoTakes is asked only once it's the demo's turn. lockedOut:
        // a click was too recent, so what would click does nothing.
        public static PinchRoute Order(bool settingsButton, bool settingsOpen, bool onButton, System.Func<bool> demoTakes,
            bool canGrab, bool onHandOnlyObject, bool lockedOut = false)
        {
            if (settingsButton && !lockedOut)
                return PinchRoute.Settings;
            if (onButton)
                return lockedOut ? PinchRoute.Ignored : PinchRoute.Button;
            if (!settingsOpen && demoTakes != null && demoTakes())
                return PinchRoute.Demo;
            if (canGrab)
                return PinchRoute.Grab;
            if (onHandOnlyObject || lockedOut)
                return PinchRoute.Ignored;
            return PinchRoute.Click;
        }

        // A pinch closed. canGrab: whether the hand's interactor is on something it can pick up, which
        // the caller then does when this returns Grab.
        public static PinchRoute Begin(PinchSource source, GazePointer gaze, bool canGrab)
        {
            if (WatchGrabController.TryConsumeRelease())
                return PinchRoute.Grab;
            if (source != PinchSource.Watch && HandPinchMuted)
            {
                Debug.Log($"[Pinch] {source} pinch ignored: the watch's double pinch clicks in this scene");
                return PinchRoute.Ignored;
            }
            End();
            var target = gaze.Target;
            var handler = Handler;
            var now = Time.unscaledTime;
            var lockout = LockoutForSource(source, target);
            var lockedOut = now - s_LastClick < lockout;
            var route = Order(!lockedOut && SettingsSummoner.TryConsumePinch(), SettingsSummoner.IsPanelOpen, IsButton(target),
                handler != null ? () => handler.TryBeginPinch(target, source) : null,
                canGrab,
                (source == PinchSource.Hand || source == PinchSource.Watch) && target != null && target.GetComponentInParent<PlacedObject>() != null,
                lockedOut);
            if (source != PinchSource.Watch &&
                (route == PinchRoute.Click || route == PinchRoute.Button || (route == PinchRoute.Ignored && lockedOut)) &&
                VisualPinchScroller.TryBegin(source, gaze, target, IsButton(target)))
            {
                var scrollTargetName = target != null ? target.name : "scrollable surface";
                Debug.Log($"[Pinch] {source} continuous pinch started on {scrollTargetName}");
                return PinchRoute.Scroll;
            }
            if (route is PinchRoute.Settings or PinchRoute.Button or PinchRoute.Click)
                s_LastClick = now;
            var name = target != null ? target.name : "nothing";
            switch (route)
            {
                case PinchRoute.Button:
                case PinchRoute.Click:
                    Debug.Log($"[Pinch] {source} pinch: clicking {name}");
                    gaze.TriggerDown();
                    gaze.TriggerUp();
                    break;
                case PinchRoute.Demo:
                    s_Claimed = handler;
                    break;
                case PinchRoute.Ignored when lockedOut:
                    Debug.Log($"[Pinch] {source} pinch {(now - s_LastClick) * 1000f:F0} ms after the last click: taken as the same pinch, ignored");
                    break;
                case PinchRoute.Ignored:
                    Debug.Log($"[Pinch] {source} pinch with the gaze on {name} but the hand's ray elsewhere; " +
                              "placed objects are taken by hand, so this did nothing");
                    break;
            }
            return route;
        }

        // The pinch opened, or its hand was lost: ends a demo's claim or continuous scroll on it.
        public static void End()
        {
            VisualPinchScroller.End();
            var claimed = s_Claimed;
            s_Claimed = null;
            claimed?.EndPinch();
        }

        // The watch's rotating crown:
        // - While actively dragging a window/object: pushes/pulls it closer or farther.
        // - Otherwise, when looking at any scrollable window/page (AppLibrary, Browser, PhoneApps,
        //   Notification Center, or ScrollRect): scrolls content / turns pages (never resizes).
        public static void WatchRotary(GazePointer gaze, float rotarySteps)
        {
            if (Mathf.Abs(rotarySteps) < 1e-4f || gaze == null)
                return;
            if (WatchGrabController.TryHandleRotary(rotarySteps, gaze.transform))
                return;
            var target = gaze.Target;
            if (SettingsSummoner.TryHandleRotary(target, rotarySteps))
                return;
            if (TryScrollTarget(target, rotarySteps))
                return;
            if (RotaryHandler != null && RotaryHandler.TryHandleRotary(target, rotarySteps))
                return;
        }

        // Scrolls any scrollable surface or page target under the gaze with the Watch crown.
        public static bool TryScrollTarget(GameObject target, float rotarySteps)
        {
            if (target == null || Mathf.Abs(rotarySteps) < 1e-4f)
                return false;

            var scrollable = target.GetComponentInParent<IWatchScrollable>();
            if (scrollable != null && scrollable.TryHandleWatchScroll(rotarySteps))
                return true;

            var scrollRect = target.GetComponentInParent<ScrollRect>();
            if (scrollRect == null || scrollRect.content == null)
                return false;

            var content = scrollRect.content;
            var viewport = scrollRect.viewport != null ? scrollRect.viewport : (RectTransform)scrollRect.transform;
            var pos = content.anchoredPosition;
            if (scrollRect.vertical)
            {
                var maxY = Mathf.Max(0f, content.rect.height - viewport.rect.height);
                pos.y = Mathf.Clamp(pos.y + rotarySteps * 140f, 0f, maxY);
                content.anchoredPosition = pos;
                return true;
            }
            if (scrollRect.horizontal)
            {
                var maxX = Mathf.Max(0f, content.rect.width - viewport.rect.width);
                pos.x = Mathf.Clamp(pos.x - rotarySteps * 140f, -maxX, 0f);
                content.anchoredPosition = pos;
                return true;
            }
            return false;
        }

        // Clears the scene's handler, ending any pinch it holds: on leaving a scene.
        public static void Clear()
        {
            VisualPinchScroller.Reset();
            WatchGrabController.Release();
            End();
            Handler = null;
            RotaryHandler = null;
        }

        // A button or other control, which a pinch always clicks.
        public static bool IsButton(GameObject target) =>
            target != null && target.GetComponentInParent<Selectable>() != null;
    }
}
