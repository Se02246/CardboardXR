using System;

namespace CardboardXR.Stereo
{
    // Central event hub allowing assemblies that cannot reference Hands/Demos directly to open
    // OS panels (Settings, Apps, Media, Notification Center, Shizuku onboarding) and sync badges.
    public static class SettingsPanelHub
    {
        public static event Action OpenRequested;
        public static event Action AppsRequested;
        public static event Action EnvironmentsRequested;
        public static event Action EnvironmentsToggleRequested;
        public static event Action<bool> EnvironmentsOpenChanged;
        public static event Action ReorderToggleRequested;
        public static event Action<bool> ReorderModeChanged;
        public static event Action MediaPanelRequested;
        public static event Action NotificationCenterRequested;
        public static event Action ShizukuSetupRequested;
        public static event Action<int> UnreadNotificationsChanged;
        public static event Action<bool> MediaStateChanged;

        public static bool IsEnvironmentsOpen { get; private set; }
        public static bool IsReorderMode { get; private set; }
        public static int UnreadNotificationCount { get; private set; }
        public static bool IsMediaPlaying { get; private set; }
        public static bool HasEnvironmentsHandler => EnvironmentsToggleRequested != null || EnvironmentsRequested != null;

        public static void Open() => OpenRequested?.Invoke();

        public static void OpenApps()
        {
            if (AppsRequested != null)
                AppsRequested.Invoke();
            else
                OpenRequested?.Invoke();
        }

        public static void OpenEnvironments()
        {
            if (EnvironmentsRequested != null)
                EnvironmentsRequested.Invoke();
            else
                OpenRequested?.Invoke();
        }

        public static void ToggleEnvironments()
        {
            if (EnvironmentsToggleRequested != null)
                EnvironmentsToggleRequested.Invoke();
            else
                OpenEnvironments();
        }

        public static void NotifyEnvironmentsOpen(bool open)
        {
            if (IsEnvironmentsOpen == open)
                return;
            IsEnvironmentsOpen = open;
            EnvironmentsOpenChanged?.Invoke(open);
        }

        public static void ToggleReorder() => ReorderToggleRequested?.Invoke();

        public static void NotifyReorderMode(bool active)
        {
            if (IsReorderMode == active)
                return;
            IsReorderMode = active;
            ReorderModeChanged?.Invoke(active);
        }

        public static void OpenMediaPanel() => MediaPanelRequested?.Invoke();

        public static void OpenNotificationCenter() => NotificationCenterRequested?.Invoke();

        public static void OpenShizukuSetup() => ShizukuSetupRequested?.Invoke();

        public static void NotifyUnreadNotifications(int count)
        {
            var clamped = Math.Max(0, count);
            if (UnreadNotificationCount == clamped)
                return;
            UnreadNotificationCount = clamped;
            UnreadNotificationsChanged?.Invoke(clamped);
        }

        public static void NotifyMediaPlaying(bool playing)
        {
            if (IsMediaPlaying == playing)
                return;
            IsMediaPlaying = playing;
            MediaStateChanged?.Invoke(playing);
        }
    }
}
