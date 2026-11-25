package com.brenpoly.cardboardxr.camera;

import android.content.Context;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import com.brenpoly.cardboardxr.phoneapps.ShizukuSessionManager;

// Controls the phone's rear LED flashlight cleanly across all CardboardXR modes:
//   - If UltrawideCamera (Camera2) is running, toggles CaptureRequest.FLASH_MODE_TORCH directly
//     on the live repeating capture session (TEMPLATE_RECORD + <=30fps + 1.0x wide sensor).
//   - Otherwise calls CameraManager.setTorchMode / turnOnTorchWithStrengthLevel.
//   - If blocked by CAMERA_IN_USE (e.g. ARCore holding camera 0), tries Shizuku (sysfs root / shell)
//     and otherwise returns -1 so TorchManager can hand off to Camera2 cleanly.
//   - Returns:
//       1  = OK via active Camera2 session
//       2  = OK via CameraManager
//       3  = OK via Shizuku privileged service
//      -1  = CAMERA_IN_USE (ARCore holds camera 0; caller should hand off to Camera2)
//       0  = error / no flash hardware
public final class TorchController {
    private static final String TAG = "CardboardXRTorch";
    private static volatile boolean sTorchOn;
    private static volatile boolean sHardwareTorchOn;
    private static volatile boolean sCallbackRegistered;

    private TorchController() {}

    public static boolean isTorchOn() {
        if (UltrawideCamera.isActiveRunning()) {
            return sTorchOn;
        }
        return sCallbackRegistered ? sHardwareTorchOn : sTorchOn;
    }

    public static void syncState(boolean on) {
        sTorchOn = on;
        sHardwareTorchOn = on;
        UltrawideCamera.setDesiredTorch(on);
    }

    private static void ensureCallbackRegistered(CameraManager manager) {
        if (sCallbackRegistered || manager == null) {
            return;
        }
        sCallbackRegistered = true;
        try {
            manager.registerTorchCallback(new CameraManager.TorchCallback() {
                @Override
                public void onTorchModeUnavailable(String cameraId) {
                    Log.i(TAG, "Torch unavailable on camera " + cameraId);
                }

                @Override
                public void onTorchModeChanged(String cameraId, boolean enabled) {
                    sHardwareTorchOn = enabled;
                    if (!UltrawideCamera.isActiveRunning()) {
                        sTorchOn = enabled;
                    }
                    Log.i(TAG, "Hardware torch changed on camera " + cameraId + ": " + enabled);
                }
            }, new Handler(Looper.getMainLooper()));
        } catch (Throwable t) {
            sCallbackRegistered = false;
            Log.w(TAG, "Unable to register TorchCallback", t);
        }
    }

    public static int setTorch(Context context, boolean on) {
        sTorchOn = on;
        UltrawideCamera.setDesiredTorch(on);

        if (UltrawideCamera.isActiveRunning()) {
            if (UltrawideCamera.setActiveTorch(on)) {
                sHardwareTorchOn = on;
                return 1;
            }
        }

        if (context == null)
            return 0;

        try {
            CameraManager manager = (CameraManager) context.getApplicationContext().getSystemService(Context.CAMERA_SERVICE);
            if (manager == null)
                return 0;
            ensureCallbackRegistered(manager);

            String flashId = findRearFlashCameraId(manager);
            if (flashId == null) {
                Log.w(TAG, "No rear camera with flash unit found");
                return 0;
            }

            if (on && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                Integer maxStrength = manager.getCameraCharacteristics(flashId)
                        .get(CameraCharacteristics.FLASH_INFO_STRENGTH_MAXIMUM_LEVEL);
                if (maxStrength != null && maxStrength > 1) {
                    manager.turnOnTorchWithStrengthLevel(flashId, maxStrength);
                } else {
                    manager.setTorchMode(flashId, true);
                }
            } else {
                manager.setTorchMode(flashId, on);
            }
            Log.i(TAG, "CameraManager torch set to " + on + " on camera " + flashId);
            return 2;
        } catch (CameraAccessException e) {
            int reason = e.getReason();
            if (reason == CameraAccessException.CAMERA_IN_USE
                    || reason == CameraAccessException.MAX_CAMERAS_IN_USE) {
                int shizukuRes = ShizukuSessionManager.setTorchViaShizukuIfReady(on);
                if (shizukuRes > 0) {
                    sHardwareTorchOn = on;
                    Log.i(TAG, "Torch controlled via Shizuku (" + on + ")");
                    return 3;
                }
                Log.i(TAG, "Torch blocked by active camera (" + reason + "): handoff to Camera2 session needed");
                return -1;
            }
            Log.w(TAG, "CameraAccessException setting torch: " + e.getMessage());
            return 0;
        } catch (Throwable t) {
            int shizukuRes = ShizukuSessionManager.setTorchViaShizukuIfReady(on);
            if (shizukuRes > 0) {
                sHardwareTorchOn = on;
                return 3;
            }
            Log.w(TAG, "Failed to set torch", t);
            return 0;
        }
    }

    private static String findRearFlashCameraId(CameraManager manager) throws CameraAccessException {
        for (String id : manager.getCameraIdList()) {
            CameraCharacteristics chars = manager.getCameraCharacteristics(id);
            Integer facing = chars.get(CameraCharacteristics.LENS_FACING);
            Boolean hasFlash = chars.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
            if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK
                    && hasFlash != null && hasFlash) {
                return id;
            }
        }
        return null;
    }
}
