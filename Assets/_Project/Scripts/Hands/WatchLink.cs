using UnityEngine;

namespace CardboardXR.Hands
{
    // The phone's end of the Wear OS watch app (WearApp/ in the repository). The watch reads its own
    // accelerometer and gyroscope, recognizes a double pinch of thumb and index finger there, and
    // sends just that over the Wearable Data Layer; the Java side (Plugins/Android/WatchBridge.java)
    // counts what arrives, and this reads the counts once a frame. The watch also sends a heartbeat
    // every couple of seconds while its session runs: the watch counts as connected while those keep
    // coming. Its screen has a Settings button too, which opens the settings panel, since with the
    // camera's pinch off there may be no palm to summon it with. Android only: elsewhere it's never
    // connected. PinchInput polls it. Logs [Watch].
    public static class WatchLink
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string k_Bridge = "com.brenpoly.cardboardxr.watch.WatchBridge";
        const long k_CountMask = 0xFFFFFF;
        // A longer gap than this between polls means the app was paused or a scene was loading:
        // what arrived meanwhile is dropped rather than clicking whatever the gaze is on now.
        const float k_StaleSeconds = 0.5f;

        static AndroidJavaClass s_Bridge;
        static bool s_Failed;
        static long s_Pinches = -1;
        static long s_Settings = -1;
        static float s_LastPoll = float.NegativeInfinity;
        static int s_SentWanted = -1;
        static int s_SentGrabbing = -1;
#endif

        const string k_InvertYPrefKey = "CardboardXR.WatchInvertY";
        const string k_GyroCursorPrefKey = "CardboardXR.WatchGyroCursor";
        const string k_GyroWindowPrefKey = "CardboardXR.WatchGyroWindow";
        static bool s_InvertYLoaded;
        static bool s_InvertY;
        static bool s_GyroCursorLoaded;
        static bool s_GyroCursorEnabled;
        static bool s_GyroWindowLoaded;
        static bool s_GyroWindowEnabled = true;

        public static bool IsConnected { get; private set; }
        public static bool IsSinglePinchMode { get; private set; }
        public static float RotarySteps { get; private set; }
        // Wrist angular delta since the last frame while grabbing, in radians: x = yaw (+right), y = pitch (+up).
        public static Vector2 GyroDeltaRad { get; private set; }

        public static bool GyroWindowControlEnabled
        {
            get
            {
                if (!s_GyroWindowLoaded)
                {
                    s_GyroWindowLoaded = true;
                    s_GyroWindowEnabled = PlayerPrefs.GetInt(k_GyroWindowPrefKey, 1) != 0;
                }
                return s_GyroWindowEnabled;
            }
        }

        public static void SetGyroWindowControlEnabled(bool enabled)
        {
            s_GyroWindowLoaded = true;
            s_GyroWindowEnabled = enabled;
            PlayerPrefs.SetInt(k_GyroWindowPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[Watch] Window drag/resize gyro {(enabled ? "on (Watch gyro)" : "off (gaze follow)")}");
        }

        public static bool GyroCursorEnabled
        {
            get
            {
                if (!s_GyroCursorLoaded)
                {
                    s_GyroCursorLoaded = true;
                    s_GyroCursorEnabled = PlayerPrefs.GetInt(k_GyroCursorPrefKey, 0) != 0;
                }
                return s_GyroCursorEnabled;
            }
        }

        public static void SetGyroCursorEnabled(bool enabled)
        {
            s_GyroCursorLoaded = true;
            s_GyroCursorEnabled = enabled;
            PlayerPrefs.SetInt(k_GyroCursorPrefKey, enabled ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[Watch] Gyro cursor control {(enabled ? "on" : "off (centered)")}");
        }

        public static bool InvertPitchY
        {
            get
            {
                if (!s_InvertYLoaded)
                {
                    s_InvertYLoaded = true;
                    s_InvertY = PlayerPrefs.GetInt(k_InvertYPrefKey, 0) != 0;
                }
                return s_InvertY;
            }
        }

        public static void SetInvertPitchY(bool invert)
        {
            s_InvertYLoaded = true;
            s_InvertY = invert;
            PlayerPrefs.SetInt(k_InvertYPrefKey, invert ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[Watch] Invert Y axis {(invert ? "on" : "off")}");
        }

        // Notifies the watch whether a window/object is currently grabbed so it streams gyro deltas.
        public static void SetGrabbing(bool grabbing)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (s_Failed || !EnsureStarted())
                return;
            var flag = grabbing ? 1 : 0;
            if (flag == s_SentGrabbing)
                return;
            try
            {
                s_Bridge.CallStatic("setGrabbing", grabbing);
                s_SentGrabbing = flag;
            }
            catch (System.Exception e)
            {
                Fail(e);
            }
#endif
        }

        // Once a frame. handPinchWanted: the scene needs the camera's pinch, which the watch shows.
        // Gives how many double pinches and settings requests arrived since the last poll.
        public static void Poll(bool handPinchWanted, out int pinches, out int settingsRequests)
        {
            pinches = 0;
            settingsRequests = 0;
            RotarySteps = 0f;
            GyroDeltaRad = Vector2.zero;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (s_Failed || !EnsureStarted())
                return;
            try
            {
                var wanted = handPinchWanted ? 1 : 0;
                if (wanted != s_SentWanted)
                {
                    s_Bridge.CallStatic("setHandPinchWanted", handPinchWanted);
                    s_SentWanted = wanted;
                }

                // Pinch count in bits 0-23, settings requests in 24-47, connected in bit 48, singlePinchMode in bit 49.
                var state = s_Bridge.CallStatic<long>("poll");
                var pinchCount = state & k_CountMask;
                var settingsCount = (state >> 24) & k_CountMask;
                var connected = ((state >> 48) & 1) != 0;
                IsSinglePinchMode = ((state >> 49) & 1) != 0;

                var now = Time.realtimeSinceStartup;
                var fresh = now - s_LastPoll <= k_StaleSeconds && s_Pinches >= 0;
                var rotaryRaw = s_Bridge.CallStatic<int>("pollRotary");
                var gyroYawMicro = s_Bridge.CallStatic<int>("pollGyroYaw");
                var gyroPitchMicro = s_Bridge.CallStatic<int>("pollGyroPitch");
                if (fresh)
                {
                    pinches = (int)((pinchCount - s_Pinches) & k_CountMask);
                    settingsRequests = (int)((settingsCount - s_Settings) & k_CountMask);
                    RotarySteps = rotaryRaw / 100f;
                    var pitchSign = InvertPitchY ? -1f : 1f;
                    GyroDeltaRad = new Vector2(gyroYawMicro * 1e-6f, gyroPitchMicro * 1e-6f * pitchSign);
                }
                s_Pinches = pinchCount;
                s_Settings = settingsCount;
                s_LastPoll = now;

                if (connected != IsConnected)
                {
                    IsConnected = connected;
                    Debug.Log(connected
                        ? "[Watch] Connected: the watch's double pinch clicks, the camera's pinch only where a scene needs the hand"
                        : "[Watch] Disconnected: the camera's pinch clicks again");
                }
            }
            catch (System.Exception e)
            {
                Fail(e);
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool EnsureStarted()
        {
            if (s_Bridge != null)
                return true;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                s_Bridge = new AndroidJavaClass(k_Bridge);
                s_Bridge.CallStatic("start", activity);
                Debug.Log("[Watch] Listening for the Pixel Watch app");
                return true;
            }
            catch (System.Exception e)
            {
                Fail(e);
                return false;
            }
        }

        static void Fail(System.Exception e)
        {
            s_Failed = true;
            IsConnected = false;
            Debug.LogWarning($"[Watch] Off: the watch bridge failed ({e.Message})");
        }
#endif
    }
}
