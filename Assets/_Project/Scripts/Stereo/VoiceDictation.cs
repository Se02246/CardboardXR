using System;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Shared speech-to-text dictation controller for virtual keyboards (Browser & PhoneApps),
    // backed by Android's headless SpeechRecognizer (VoiceDictationBridge.java).
    public static class VoiceDictation
    {
        static object s_Owner;
        static Action<string> s_OnFinalText;
        static Action<string> s_OnPartialText;
#if UNITY_ANDROID && !UNITY_EDITOR
        static AndroidJavaObject s_Bridge;
        static bool s_BridgeFailed;
#endif

        public static bool IsListening { get; private set; }
        public static object ActiveOwner => IsListening ? s_Owner : null;
        public static float VoiceLevel01 { get; private set; }
        public static string PartialText { get; private set; } = "";
        public static string LastStatus { get; private set; } = "";

        public static void Toggle(object owner, Action<string> onFinalText, Action<string> onPartialText = null)
        {
            if (IsListening && ReferenceEquals(s_Owner, owner))
            {
                Stop(owner);
                return;
            }

            s_Owner = owner;
            s_OnFinalText = onFinalText;
            s_OnPartialText = onPartialText;
            PartialText = "";
            LastStatus = "";
            VoiceLevel01 = 0f;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (EnsureBridge())
            {
                try
                {
                    s_Bridge.Call("startListening", "");
                    IsListening = s_Bridge.Call<bool>("isListening");
                    if (!IsListening)
                        IsListening = true;
                    LastStatus = s_Bridge.Call<string>("pollStatus") ?? "";
                    return;
                }
                catch (Exception e)
                {
                    LastStatus = $"Errore dettazione: {e.Message}";
                    IsListening = false;
                }
            }
            else
            {
                IsListening = false;
            }
#else
            IsListening = false;
            LastStatus = "Dettazione vocale disponibile su Android";
#endif
        }

        public static void Stop(object owner = null)
        {
            if (owner != null && !ReferenceEquals(s_Owner, owner))
                return;

            IsListening = false;
            VoiceLevel01 = 0f;
            PartialText = "";
            s_Owner = null;
            s_OnFinalText = null;
            s_OnPartialText = null;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (s_Bridge != null)
            {
                try { s_Bridge.Call("stopListening"); }
                catch (Exception) { }
            }
#endif
        }

        public static void Poll()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (s_Bridge == null || s_BridgeFailed)
                return;
            try
            {
                var listening = s_Bridge.Call<bool>("isListening");
                VoiceLevel01 = listening ? s_Bridge.Call<float>("getVoiceLevel") : 0f;
                var partial = s_Bridge.Call<string>("pollPartialText");
                if (partial != null)
                {
                    PartialText = partial;
                    s_OnPartialText?.Invoke(partial);
                }

                var final = s_Bridge.Call<string>("pollFinalText");
                if (!string.IsNullOrEmpty(final))
                {
                    PartialText = "";
                    s_OnFinalText?.Invoke(final);
                }

                var status = s_Bridge.Call<string>("pollStatus");
                if (!string.IsNullOrEmpty(status))
                    LastStatus = status;

                if (IsListening && !listening)
                {
                    IsListening = false;
                    VoiceLevel01 = 0f;
                    PartialText = "";
                }
            }
            catch (Exception)
            {
                IsListening = false;
                VoiceLevel01 = 0f;
            }
#endif
        }

        // Inserts a single space when appending newly dictated words onto non-whitespace existing text.
        public static string FormatDictatedAppend(string existingBuffer, string spoken)
        {
            var clean = spoken != null ? spoken.Trim() : "";
            if (string.IsNullOrEmpty(clean))
                return "";
            if (string.IsNullOrEmpty(existingBuffer))
                return clean;
            var last = existingBuffer[existingBuffer.Length - 1];
            return char.IsWhiteSpace(last) ? clean : " " + clean;
        }

        // Computes illuminated pill color and pulsing halo glow while Voice Dictation is listening.
        public static (Color containerColor, Color haloColor, float haloScale) ComputeButtonVisuals(
            bool listening, float voiceLevel01, float timeSec)
        {
            if (!listening)
                return (M3Theme.SecondaryContainer, new Color(0.32f, 0.72f, 1f, 0f), 1f);

            var pulse = 0.5f + 0.5f * Mathf.Sin(timeSec * 6.5f);
            var energy = Mathf.Clamp01(0.35f + 0.65f * Mathf.Max(Mathf.Clamp01(voiceLevel01), pulse * 0.45f));
            var container = Color.Lerp(
                new Color(0.20f, 0.58f, 0.96f, 0.96f),
                new Color(0.35f, 0.84f, 1.00f, 1.00f),
                energy);
            var halo = new Color(0.35f, 0.84f, 1.00f, Mathf.Lerp(0.32f, 0.88f, energy));
            var scale = 1.04f + 0.14f * energy;
            return (container, halo, scale);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static bool EnsureBridge()
        {
            if (s_BridgeFailed)
                return false;
            if (s_Bridge != null)
                return true;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                s_Bridge = new AndroidJavaObject("com.brenpoly.cardboardxr.system.VoiceDictationBridge", activity);
                return true;
            }
            catch (Exception e)
            {
                s_BridgeFailed = true;
                LastStatus = $"Bridge vocale non disponibile: {e.Message}";
                return false;
            }
        }
#endif
    }
}
