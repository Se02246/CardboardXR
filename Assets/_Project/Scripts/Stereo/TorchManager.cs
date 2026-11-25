using System;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Stereo
{
    // Quick-toggle rear LED flashlight controller accessible from the top quick-toggles bar and
    // look-up bar. Uses Ultrawide Camera2 CaptureRequest.FLASH_MODE_TORCH (TEMPLATE_RECORD),
    // Android CameraManager / Shizuku fallback when idle, and temporary Camera2 handoff if ARCore
    // holds camera 0 in 6DoF.
    public class TorchManager : MonoBehaviour
    {
        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] M3Button m_Toggle;

        static TorchManager s_Instance;

        bool m_TorchOn;
        bool m_UsingCameraOverride;

        public static bool IsTorchOn => s_Instance != null && s_Instance.m_TorchOn;
        public static event Action<bool> TorchChanged;

        void Awake()
        {
            s_Instance = this;
            m_TorchOn = QueryNativeTorchState();
            SyncToggle();
        }

        void OnDestroy()
        {
            TurnOffIfNeeded();
            if (s_Instance == this)
                s_Instance = null;
        }

        void OnApplicationQuit() => TurnOffIfNeeded();

        void TurnOffIfNeeded()
        {
            if (!m_TorchOn)
                return;
            SetNativeTorch(false);
            if (m_UsingCameraOverride && m_Passthrough != null)
                m_Passthrough.SetTorchCameraOverride(false);
            m_UsingCameraOverride = false;
            m_TorchOn = false;
        }

        public static void ToggleGlobal() => s_Instance?.Toggle();

        public void Toggle()
        {
            var next = !m_TorchOn;

            if (!next)
            {
                SetNativeTorch(false);
                if (m_UsingCameraOverride && m_Passthrough != null)
                    m_Passthrough.SetTorchCameraOverride(false);
                m_UsingCameraOverride = false;
                m_TorchOn = false;
                SyncToggle();
                TorchChanged?.Invoke(false);
                Debug.Log("[Torch] Torcia spenta");
                return;
            }

            var result = SetNativeTorch(true);
            if (result == -1)
            {
                // ARCore holds camera 0 on non-rooted devices: temporarily switch to Camera2
                // with sDesiredTorch = true so TEMPLATE_RECORD opens with FLASH_MODE_TORCH.
                SyncNativeTorchState(true);
                if (m_Passthrough != null)
                {
                    m_UsingCameraOverride = true;
                    m_Passthrough.SetTorchCameraOverride(true);
                }
                m_TorchOn = true;
                SyncToggle();
                TorchChanged?.Invoke(true);
                Debug.Log("[Torch] Torcia accesa con passaggio a sessione Camera2");
                return;
            }

            m_UsingCameraOverride = false;
            m_TorchOn = result > 0;
            if (!m_TorchOn)
                SyncNativeTorchState(false);
            SyncToggle();
            TorchChanged?.Invoke(m_TorchOn);
            if (!m_TorchOn)
                Debug.LogWarning("[Torch] Flash hardware non disponibile su questo dispositivo");
            else
                Debug.Log($"[Torch] Torcia accesa (modalità={result})");
        }

        void SyncToggle()
        {
            if (m_Toggle == null)
                return;
            m_Toggle.Selected = m_TorchOn;
            m_Toggle.Icon = "flashlight_on";
        }

        static void SyncNativeTorchState(bool on)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var cls = new AndroidJavaClass("com.brenpoly.cardboardxr.camera.TorchController");
                cls.CallStatic("syncState", on);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Torch] syncState failed: {e.Message}");
            }
#endif
        }

        static bool QueryNativeTorchState()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var cls = new AndroidJavaClass("com.brenpoly.cardboardxr.camera.TorchController");
                return cls.CallStatic<bool>("isTorchOn");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Torch] Query failed: {e.Message}");
            }
#endif
            return false;
        }

        static int SetNativeTorch(bool on)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var cls = new AndroidJavaClass("com.brenpoly.cardboardxr.camera.TorchController");
                return cls.CallStatic<int>("setTorch", activity, on);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Torch] SetTorch failed: {e.Message}");
                return 0;
            }
#else
            return 2;
#endif
        }
    }
}
