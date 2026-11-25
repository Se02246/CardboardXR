package com.brenpoly.cardboardxr.camera;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.ImageFormat;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.CaptureResult;
import android.hardware.camera2.TotalCaptureResult;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;
import android.util.Range;
import android.util.Size;
import android.util.SizeF;

import java.nio.ByteBuffer;
import java.util.Collections;

// Opens the rear ultrawide camera (logical back camera with CONTROL_ZOOM_RATIO = minZoomRatio,
// ~0.508x on Pixel 9, or the shortest-focal-length rear camera) via Camera2 and streams RGBA32
// frames to Unity's PassthroughController for a much wider passthrough FOV (~110+ degrees).
public final class UltrawideCamera {
    private static final String TAG = "CardboardXRUW";
    private static final int TARGET_WIDTH = 960;
    private static final int TARGET_HEIGHT = 540;
    private static final long MIN_FRAME_INTERVAL_MS = 16L;

    private static volatile UltrawideCamera sActiveInstance;
    private static volatile boolean sDesiredTorch;
    private static volatile boolean sHardwareTorchFired;

    private final Context mContext;
    private final Object mLock = new Object();

    private HandlerThread mThread;
    private Handler mHandler;
    private CameraDevice mDevice;
    private CameraCaptureSession mSession;
    private ImageReader mReader;

    private int mWidth;
    private int mHeight;
    private float mMinZoom = 1f;
    private float mHorizontalFovDeg = 108f;
    private float mVerticalFovDeg = 72f;
    private boolean mFlip180;
    private Range<Integer> mTargetFpsRange;
    private Range<Integer> mTorchFpsRange;
    private volatile int mLastLoggedFlashState = -1;

    private final CameraCaptureSession.CaptureCallback mCaptureCallback = new CameraCaptureSession.CaptureCallback() {
        @Override
        public void onCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result) {
            Integer state = result != null ? result.get(CaptureResult.FLASH_STATE) : null;
            if (state != null) {
                sHardwareTorchFired = (state == CaptureResult.FLASH_STATE_FIRED);
                if (state != mLastLoggedFlashState) {
                    mLastLoggedFlashState = state;
                    Log.i(TAG, "Camera2 FLASH_STATE -> " + state + " (desired=" + sDesiredTorch + ")");
                }
            }
        }
    };

    private final ByteBuffer[] mBuffers = new ByteBuffer[2];
    private int mFrontIdx = 0;
    private int mWriteIdx = 1;
    private long mLastFrameMs;
    private byte[] mBackArray;
    private byte[] mYRow;
    private byte[] mURow;
    private byte[] mVRow;
    private volatile boolean mHasNewFrame;
    private volatile boolean mRunning;

    public UltrawideCamera(Context context) {
        mContext = context.getApplicationContext();
    }

    public int getWidth() { return mWidth; }
    public int getHeight() { return mHeight; }
    public float getHorizontalFovDeg() { return mHorizontalFovDeg; }
    public float getVerticalFovDeg() { return mVerticalFovDeg; }

    public static boolean isActiveRunning() {
        UltrawideCamera inst = sActiveInstance;
        return inst != null && inst.mRunning && inst.mSession != null && inst.mDevice != null;
    }

    public static void setDesiredTorch(boolean on) {
        sDesiredTorch = on;
    }

    public static boolean isDesiredTorch() {
        return sDesiredTorch;
    }

    public static boolean setActiveTorch(boolean on) {
        sDesiredTorch = on;
        UltrawideCamera inst = sActiveInstance;
        if (inst == null || !inst.mRunning)
            return false;
        return inst.applyTorchOnSession(on);
    }

    private boolean applyTorchOnSession(final boolean on) {
        final Handler handler = mHandler;
        if (!mRunning || handler == null || mDevice == null || mSession == null || mReader == null) {
            return false;
        }
        handler.post(() -> {
            CameraDevice dev = mDevice;
            CameraCaptureSession sess = mSession;
            ImageReader reader = mReader;
            if (!mRunning || dev == null || sess == null || reader == null) {
                return;
            }
            try {
                CaptureRequest.Builder req = buildPreviewRequest(dev, reader, on);
                sess.setRepeatingRequest(req.build(), mCaptureCallback, handler);
                Log.i(TAG, "Ultrawide Camera2 torch set to " + on);
            } catch (Throwable t) {
                Log.w(TAG, "Failed to update Camera2 torch", t);
            }
        });
        return true;
    }

    private CaptureRequest.Builder buildPreviewRequest(CameraDevice camera, ImageReader reader, boolean torchOn) throws Exception {
        CaptureRequest.Builder req;
        if (torchOn) {
            try {
                req = camera.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            } catch (Throwable ignored) {
                req = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            }
            req.addTarget(reader.getSurface());
            req.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO);
            req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            req.set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON);
            req.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_TORCH);
            if (mTorchFpsRange != null) {
                req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, mTorchFpsRange);
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && mMinZoom < 1f) {
                req.set(CaptureRequest.CONTROL_ZOOM_RATIO, 1.0f);
            }
        } else {
            req = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            req.addTarget(reader.getSurface());
            req.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            req.set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON);
            req.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_OFF);
            if (mTargetFpsRange != null) {
                req.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, mTargetFpsRange);
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && mMinZoom < 1f) {
                req.set(CaptureRequest.CONTROL_ZOOM_RATIO, mMinZoom);
            }
        }
        return req;
    }

    @SuppressLint("MissingPermission")
    public synchronized void start() {
        if (mRunning)
            return;
        mRunning = true;
        sActiveInstance = this;

        mThread = new HandlerThread("ultrawide-camera");
        mThread.start();
        mHandler = new Handler(mThread.getLooper());

        try {
            CameraManager manager = (CameraManager) mContext.getSystemService(Context.CAMERA_SERVICE);
            String cameraId = pickUltrawideCamera(manager);
            if (cameraId == null) {
                Log.w(TAG, "No rear camera found");
                stop();
                return;
            }

            CameraCharacteristics chars = manager.getCameraCharacteristics(cameraId);
            Integer orientation = chars.get(CameraCharacteristics.SENSOR_ORIENTATION);
            // CardboardXR runs in LandscapeLeft (90 deg sensor orientation is upright; 270 deg is upside down).
            mFlip180 = orientation != null && orientation == 270;

            mMinZoom = 1f;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                Range<Float> zoomRange = chars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
                if (zoomRange != null && zoomRange.getLower() < 1f) {
                    mMinZoom = zoomRange.getLower();
                }
            }

            computeFov(chars, mMinZoom);
            mTargetFpsRange = chooseFpsRange(chars, 60);
            mTorchFpsRange = chooseFpsRange(chars, 30);

            StreamConfigurationMap map = chars.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            Size chosen = chooseSize(map != null ? map.getOutputSizes(ImageFormat.YUV_420_888) : null);
            mWidth = chosen.getWidth();
            mHeight = chosen.getHeight();
            int byteCount = mWidth * mHeight * 4;
            mBuffers[0] = ByteBuffer.allocateDirect(byteCount);
            mBuffers[1] = ByteBuffer.allocateDirect(byteCount);
            mFrontIdx = 0;
            mWriteIdx = 1;
            mLastFrameMs = 0L;
            mBackArray = new byte[byteCount];
            mYRow = new byte[mWidth];
            mURow = new byte[mWidth];
            mVRow = new byte[mWidth];
            mHasNewFrame = false;

            mReader = ImageReader.newInstance(mWidth, mHeight, ImageFormat.YUV_420_888, 2);
            mReader.setOnImageAvailableListener(this::onImageAvailable, mHandler);

            Log.i(TAG, "Opening camera " + cameraId + " at " + mWidth + "x" + mHeight
                    + " zoom=" + mMinZoom + " fov=" + Math.round(mHorizontalFovDeg) + "x" + Math.round(mVerticalFovDeg));

            manager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override
                public void onOpened(CameraDevice camera) {
                    if (!mRunning) {
                        camera.close();
                        return;
                    }
                    mDevice = camera;
                    startSession(camera);
                }

                @Override
                public void onDisconnected(CameraDevice camera) {
                    camera.close();
                    if (mDevice == camera) mDevice = null;
                }

                @Override
                public void onError(CameraDevice camera, int error) {
                    Log.w(TAG, "Camera error: " + error);
                    camera.close();
                    if (mDevice == camera) mDevice = null;
                }
            }, mHandler);
        } catch (Throwable t) {
            Log.e(TAG, "Failed to start ultrawide camera", t);
            stop();
        }
    }

    private void startSession(CameraDevice camera) {
        try {
            if (mReader == null) return;
            camera.createCaptureSession(
                    Collections.singletonList(mReader.getSurface()),
                    new CameraCaptureSession.StateCallback() {
                        @Override
                        public void onConfigured(CameraCaptureSession session) {
                            if (!mRunning || mDevice == null) {
                                session.close();
                                return;
                            }
                            mSession = session;
                            try {
                                CaptureRequest.Builder req = buildPreviewRequest(camera, mReader, sDesiredTorch);
                                session.setRepeatingRequest(req.build(), mCaptureCallback, mHandler);
                            } catch (Throwable t) {
                                Log.w(TAG, "Repeating request failed", t);
                            }
                        }

                        @Override
                        public void onConfigureFailed(CameraCaptureSession session) {
                            Log.w(TAG, "Capture session configure failed");
                        }
                    },
                    mHandler);
        } catch (Throwable t) {
            Log.w(TAG, "createCaptureSession failed", t);
        }
    }

    private String pickUltrawideCamera(CameraManager manager) throws Exception {
        String bestWithFlashId = null;
        float bestWithFlashFocal = Float.MAX_VALUE;
        String bestId = null;
        float bestEffectiveFocal = Float.MAX_VALUE;
        for (String id : manager.getCameraIdList()) {
            CameraCharacteristics chars = manager.getCameraCharacteristics(id);
            Integer facing = chars.get(CameraCharacteristics.LENS_FACING);
            if (facing == null || facing != CameraCharacteristics.LENS_FACING_BACK)
                continue;
            float focal = 4.5f;
            float[] focals = chars.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
            if (focals != null && focals.length > 0)
                focal = focals[0];
            float minZoom = 1f;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                Range<Float> zoomRange = chars.get(CameraCharacteristics.CONTROL_ZOOM_RATIO_RANGE);
                if (zoomRange != null && zoomRange.getLower() < 1f)
                    minZoom = zoomRange.getLower();
            }
            float effectiveFocal = focal * minZoom;
            boolean hasFlash = Boolean.TRUE.equals(chars.get(CameraCharacteristics.FLASH_INFO_AVAILABLE));
            if (hasFlash && effectiveFocal < bestWithFlashFocal) {
                bestWithFlashFocal = effectiveFocal;
                bestWithFlashId = id;
            }
            if (effectiveFocal < bestEffectiveFocal) {
                bestEffectiveFocal = effectiveFocal;
                bestId = id;
            }
        }
        return bestWithFlashId != null ? bestWithFlashId : bestId;
    }

    private Range<Integer> chooseFpsRange(CameraCharacteristics chars, int maxUpperFps) {
        Range<Integer>[] ranges = chars.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
        if (ranges == null || ranges.length == 0)
            return null;
        Range<Integer> best = null;
        for (Range<Integer> r : ranges) {
            if (r == null) continue;
            if (r.getUpper() > maxUpperFps) continue;
            if (best == null
                    || r.getUpper() > best.getUpper()
                    || (r.getUpper().equals(best.getUpper()) && r.getLower() > best.getLower())) {
                best = r;
            }
        }
        Log.i(TAG, "Chosen ultrawide FPS range (cap " + maxUpperFps + "): " + best);
        return best;
    }

    private void computeFov(CameraCharacteristics chars, float zoomRatio) {
        try {
            SizeF sensor = chars.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE);
            float[] focals = chars.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
            if (sensor != null && focals != null && focals.length > 0 && focals[0] > 0f) {
                float effectiveFocal = focals[0] * Math.max(0.35f, zoomRatio);
                double hRad = 2.0 * Math.atan(sensor.getWidth() / (2.0 * effectiveFocal));
                double vRad = 2.0 * Math.atan(sensor.getHeight() / (2.0 * effectiveFocal));
                mHorizontalFovDeg = (float) Math.max(60.0, Math.min(130.0, Math.toDegrees(hRad)));
                mVerticalFovDeg = (float) Math.max(40.0, Math.min(100.0, Math.toDegrees(vRad)));
                return;
            }
        } catch (Throwable ignored) {
        }
        mHorizontalFovDeg = 108f;
        mVerticalFovDeg = 72f;
    }

    private Size chooseSize(Size[] sizes) {
        if (sizes == null || sizes.length == 0)
            return new Size(640, 360);
        Size best = sizes[0];
        int bestScore = Integer.MAX_VALUE;
        for (Size s : sizes) {
            int w = s.getWidth();
            int h = s.getHeight();
            if (w < 480 || w > 1280 || h < 270 || h > 720)
                continue;
            int score = Math.abs(w - TARGET_WIDTH) + Math.abs(h - TARGET_HEIGHT) * 2;
            if (score < bestScore) {
                bestScore = score;
                best = s;
            }
        }
        return best;
    }

    private void onImageAvailable(ImageReader reader) {
        Image image = null;
        try {
            image = reader.acquireLatestImage();
            if (image == null || !mRunning)
                return;
            long now = android.os.SystemClock.uptimeMillis();
            if (now - mLastFrameMs < MIN_FRAME_INTERVAL_MS)
                return;
            mLastFrameMs = now;
            convertYuvToRgba(image, mBackArray, mWidth, mHeight, mFlip180, mYRow, mURow, mVRow);
            synchronized (mLock) {
                ByteBuffer dst = mBuffers[mWriteIdx];
                if (dst != null) {
                    dst.clear();
                    dst.put(mBackArray);
                    mHasNewFrame = true;
                }
            }
        } catch (Throwable ignored) {
        } finally {
            if (image != null)
                image.close();
        }
    }

    public ByteBuffer getBuffer(int index) {
        return mBuffers[index == 1 ? 1 : 0];
    }

    public ByteBuffer getFrontBuffer() {
        return mBuffers[mFrontIdx];
    }

    public int acquireFrontBufferIndex() {
        if (!mHasNewFrame || mBuffers[0] == null || mBuffers[1] == null)
            return -1;
        synchronized (mLock) {
            if (!mHasNewFrame)
                return -1;
            int prev = mFrontIdx;
            mFrontIdx = mWriteIdx;
            mWriteIdx = prev;
            mHasNewFrame = false;
            return mFrontIdx;
        }
    }

    public boolean syncFrontBuffer() {
        return acquireFrontBufferIndex() >= 0;
    }

    public synchronized void stop() {
        mRunning = false;
        mHasNewFrame = false;
        if (sActiveInstance == this)
            sActiveInstance = null;
        try {
            if (mSession != null) {
                mSession.close();
                mSession = null;
            }
        } catch (Throwable ignored) {
        }
        try {
            if (mDevice != null) {
                mDevice.close();
                mDevice = null;
            }
        } catch (Throwable ignored) {
        }
        try {
            if (mReader != null) {
                mReader.close();
                mReader = null;
            }
        } catch (Throwable ignored) {
        }
        if (mThread != null) {
            mThread.quitSafely();
            mThread = null;
            mHandler = null;
        }
        mYRow = null;
        mURow = null;
        mVRow = null;
    }

    private static void convertYuvToRgba(Image image, byte[] out, int width, int height, boolean flip180,
            byte[] yRowArr, byte[] uRowArr, byte[] vRowArr) {
        Image.Plane[] planes = image.getPlanes();
        ByteBuffer yBuf = planes[0].getBuffer();
        ByteBuffer uBuf = planes[1].getBuffer();
        ByteBuffer vBuf = planes[2].getBuffer();

        int yRowStride = planes[0].getRowStride();
        int yPixelStride = planes[0].getPixelStride();
        int uvRowStride = planes[1].getRowStride();
        int uvPixelStride = planes[1].getPixelStride();
        int uvNeeded = ((width - 1) >> 1) * uvPixelStride + 1;
        boolean bulkUv = (uvPixelStride == 1 || uvPixelStride == 2)
                && uRowArr != null && vRowArr != null
                && uRowArr.length >= uvNeeded && vRowArr.length >= uvNeeded;
        boolean bulkY = (yPixelStride == 1) && yRowArr != null && yRowArr.length >= width;
        int lastUvRow = -1;

        for (int y = 0; y < height; y++) {
            int dstY = flip180 ? y : (height - 1 - y);
            int yRow = y * yRowStride;
            int uvRowIdx = y >> 1;
            int uvRow = uvRowIdx * uvRowStride;

            boolean rowBulkY = bulkY && (yBuf.limit() - yRow >= width);
            if (rowBulkY) {
                yBuf.position(yRow);
                yBuf.get(yRowArr, 0, width);
            }

            boolean rowBulkUv = bulkUv
                    && (uBuf.limit() - uvRow >= uvNeeded)
                    && (vBuf.limit() - uvRow >= uvNeeded);
            if (rowBulkUv && uvRowIdx != lastUvRow) {
                uBuf.position(uvRow);
                uBuf.get(uRowArr, 0, uvNeeded);
                vBuf.position(uvRow);
                vBuf.get(vRowArr, 0, uvNeeded);
                lastUvRow = uvRowIdx;
            }

            int outIdx = (dstY * width + (flip180 ? (width - 1) : 0)) * 4;
            int outStep = flip180 ? -4 : 4;
            for (int x = 0; x < width; x++) {
                int yVal = (rowBulkY ? yRowArr[x] : yBuf.get(yRow + x * yPixelStride)) & 0xFF;
                int uvCol = (x >> 1) * uvPixelStride;
                int uVal = ((rowBulkUv ? uRowArr[uvCol] : uBuf.get(uvRow + uvCol)) & 0xFF) - 128;
                int vVal = ((rowBulkUv ? vRowArr[uvCol] : vBuf.get(uvRow + uvCol)) & 0xFF) - 128;

                int r = yVal + ((1436 * vVal) >> 10);
                int g = yVal - ((352 * uVal + 731 * vVal) >> 10);
                int b = yVal + ((1815 * uVal) >> 10);

                if (r < 0) r = 0; else if (r > 255) r = 255;
                if (g < 0) g = 0; else if (g > 255) g = 255;
                if (b < 0) b = 0; else if (b > 255) b = 255;

                out[outIdx] = (byte) r;
                out[outIdx + 1] = (byte) g;
                out[outIdx + 2] = (byte) b;
                out[outIdx + 3] = (byte) 255;
                outIdx += outStep;
            }
        }
    }
}
