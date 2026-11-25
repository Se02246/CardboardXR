package com.brenpoly.cardboardxr.phoneapps;

import android.app.Activity;
import android.app.Application;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.drawable.AdaptiveIconDrawable;
import android.graphics.drawable.Drawable;
import android.media.Image;
import android.media.ImageReader;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Parcel;
import android.os.RemoteException;
import android.os.SystemClock;
import android.util.Log;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.View;
import android.view.inputmethod.InputMethodManager;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

// Unity-side bridge to Shizuku and installed phone apps.
// Delegates Shizuku connection ownership to ShizukuSessionManager and manages app discovery
// with real icons plus multiple concurrent PhoneAppSession virtual displays + input injection.
public final class PhoneAppHost {
    private static final String TAG = "CardboardXRPhoneApps";
    private static final String SHIZUKU_PACKAGE = ShizukuSessionManager.SHIZUKU_PACKAGE;

    public static final int STATUS_NOT_INSTALLED = 0;
    public static final int STATUS_NOT_RUNNING = 1;
    public static final int STATUS_PERMISSION_REQUIRED = 2;
    public static final int STATUS_CONNECTING = 3;
    public static final int STATUS_READY = 4;
    public static final int STATUS_ERROR = 5;

    private static final Map<String, Integer> FEATURED_ORDER = new HashMap<>();

    static {
        String[] featured = {
                "com.android.chrome",
                "com.zhiliaoapp.musically",
                "com.ss.android.ugc.trill",
                "com.whatsapp",
                "com.instagram.android",
                "com.google.android.youtube",
                "com.spotify.music",
                "org.telegram.messenger",
                "com.twitter.android",
                "com.reddit.frontpage",
                "com.google.android.gm",
                "com.google.android.apps.maps",
                "com.discord",
                "com.google.android.apps.photos",
                "com.android.settings",
        };
        for (int i = 0; i < featured.length; i++) {
            FEATURED_ORDER.put(featured[i], i);
        }
    }

    private static final class AppEntry {
        final String packageName;
        final String activityName;
        final String label;
        final int colorRgb;
        volatile byte[] iconPngCache;

        AppEntry(String pkg, String act, String lbl, int rgb) {
            packageName = pkg;
            activityName = act;
            label = lbl;
            colorRgb = rgb;
        }
    }

    private final Activity mActivity;
    private final ShizukuSessionManager mShizuku;
    private final List<AppEntry> mApps = new ArrayList<>();
    private final Set<PhoneAppSession> mSessions = Collections.synchronizedSet(new HashSet<>());
    private final ExecutorService mBgExecutor = Executors.newSingleThreadExecutor();

    private volatile int mAppListVersion;
    private volatile int mIconsVersion;
    private volatile int mLastActiveDisplayId = -1;
    private volatile long mLastUserInputMs;
    private volatile boolean mDisposed;
    private Application.ActivityLifecycleCallbacks mLifecycleCallbacks;

    public PhoneAppHost(Activity activity) {
        mActivity = activity;
        mShizuku = ShizukuSessionManager.getInstance(activity);
        Application app = activity != null ? activity.getApplication() : null;
        if (app != null) {
            mLifecycleCallbacks = new Application.ActivityLifecycleCallbacks() {
                @Override public void onActivityCreated(Activity a, Bundle savedInstanceState) {}
                @Override public void onActivityStarted(Activity a) {}
                @Override public void onActivityResumed(Activity a) {}
                @Override public void onActivityPaused(Activity a) {
                    if (a != mActivity || mDisposed || mSessions.isEmpty()) {
                        return;
                    }
                    int displayId = mLastActiveDisplayId;
                    long ageMs = SystemClock.uptimeMillis() - mLastUserInputMs;
                    if (displayId > 0 && ageMs >= 0L && ageMs <= 4500L) {
                        String pkg = mActivity.getPackageName();
                        String comp = mActivity.getComponentName() != null
                                ? mActivity.getComponentName().flattenToString()
                                : "";
                        mShizuku.containRedirectToDisplay(displayId, pkg, comp);
                    }
                }
                @Override public void onActivityStopped(Activity a) {}
                @Override public void onActivitySaveInstanceState(Activity a, Bundle outState) {}
                @Override public void onActivityDestroyed(Activity a) {}
            };
            app.registerActivityLifecycleCallbacks(mLifecycleCallbacks);
        }
        reloadAppListAsync();
        refreshStatus();
    }

    void noteUserInteraction(int displayId) {
        if (displayId > 0) {
            mLastActiveDisplayId = displayId;
            mLastUserInputMs = SystemClock.uptimeMillis();
        }
    }

    public int getAppListVersion() {
        return mAppListVersion;
    }

    public int getIconsVersion() {
        return mIconsVersion;
    }

    public int getStatusCode() {
        return mShizuku.getStatusCode();
    }

    public String getStatusMessage() {
        return mShizuku.getStatusMessage();
    }

    public void refreshStatus() {
        if (!mDisposed) {
            mShizuku.refreshStatus();
        }
    }

    public void requestPermission() {
        if (!mDisposed) {
            mShizuku.requestPermission(mActivity);
        }
    }

    public void openShizukuApp() {
        mShizuku.openShizukuApp(mActivity);
    }

    public void hideSystemKeyboard() {
        if (mDisposed || mActivity == null) {
            return;
        }
        mActivity.runOnUiThread(() -> {
            try {
                InputMethodManager imm = (InputMethodManager) mActivity.getSystemService(Context.INPUT_METHOD_SERVICE);
                if (imm == null) {
                    return;
                }
                View decor = mActivity.getWindow() != null ? mActivity.getWindow().getDecorView() : null;
                if (decor != null && decor.getWindowToken() != null) {
                    imm.hideSoftInputFromWindow(decor.getWindowToken(), 0);
                }
            } catch (Throwable ignored) {
            }
        });
    }

    private boolean isPackageInstalled(String pkg) {
        try {
            mActivity.getPackageManager().getPackageInfo(pkg, 0);
            return true;
        } catch (Throwable ignored) {
            return false;
        }
    }

    public void reloadAppList() {
        reloadAppListAsync();
    }

    public void reloadAppListAsync() {
        if (mDisposed) {
            return;
        }
        mBgExecutor.execute(() -> {
            if (mDisposed) {
                return;
            }
            PackageManager pm = mActivity.getPackageManager();
            Intent query = new Intent(Intent.ACTION_MAIN);
            query.addCategory(Intent.CATEGORY_LAUNCHER);
            List<ResolveInfo> resolved = pm.queryIntentActivities(query, 0);

            String selfPkg = mActivity.getPackageName();
            Map<String, AppEntry> byPkg = new HashMap<>();
            if (resolved != null) {
                for (ResolveInfo ri : resolved) {
                    if (ri.activityInfo == null) {
                        continue;
                    }
                    String pkg = ri.activityInfo.packageName;
                    if (pkg == null || pkg.equals(selfPkg) || pkg.equals(SHIZUKU_PACKAGE)) {
                        continue;
                    }
                    if (byPkg.containsKey(pkg)) {
                        continue;
                    }
                    CharSequence cs = ri.loadLabel(pm);
                    String label = (cs != null && cs.length() > 0) ? cs.toString().trim() : pkg;
                    int rgb = paletteColorForPackage(pkg);
                    byPkg.put(pkg, new AppEntry(pkg, ri.activityInfo.name, label, rgb));
                }
            }

            List<AppEntry> sorted = new ArrayList<>(byPkg.values());
            Collections.sort(sorted, (a, b) -> {
                int pa = FEATURED_ORDER.containsKey(a.packageName) ? FEATURED_ORDER.get(a.packageName) : Integer.MAX_VALUE;
                int pb = FEATURED_ORDER.containsKey(b.packageName) ? FEATURED_ORDER.get(b.packageName) : Integer.MAX_VALUE;
                if (pa != pb) {
                    return Integer.compare(pa, pb);
                }
                return a.label.compareToIgnoreCase(b.label);
            });

            synchronized (mApps) {
                mApps.clear();
                mApps.addAll(sorted);
            }
            mAppListVersion++;
            Log.i(TAG, "Discovered " + sorted.size() + " launchable phone apps (v" + mAppListVersion + ")");
        });
    }

    public int getAppCount() {
        synchronized (mApps) {
            return mApps.size();
        }
    }

    public String getAppPackage(int index) {
        AppEntry e = entryAt(index);
        return e != null ? e.packageName : "";
    }

    public String getAppActivity(int index) {
        AppEntry e = entryAt(index);
        return e != null ? e.activityName : "";
    }

    public String getAppLabel(int index) {
        AppEntry e = entryAt(index);
        return e != null ? e.label : "";
    }

    public int getAppColorRgb(int index) {
        AppEntry e = entryAt(index);
        return e != null ? e.colorRgb : 0x3B82F6;
    }

    public byte[] getAppIconPngIfCached(int index) {
        AppEntry e = entryAt(index);
        return e != null ? e.iconPngCache : null;
    }

    public void prepareIconsAsync(int startIndex, int count) {
        if (mDisposed || count <= 0) {
            return;
        }
        final List<AppEntry> slice = new ArrayList<>();
        synchronized (mApps) {
            int from = Math.max(0, startIndex);
            int to = Math.min(mApps.size(), from + count);
            for (int i = from; i < to; i++) {
                AppEntry entry = mApps.get(i);
                if (entry != null && entry.iconPngCache == null) {
                    slice.add(entry);
                }
            }
        }
        if (slice.isEmpty()) {
            return;
        }
        mBgExecutor.execute(() -> {
            PackageManager pm = mActivity.getPackageManager();
            boolean anyLoaded = false;
            for (AppEntry e : slice) {
                if (mDisposed) {
                    return;
                }
                if (e.iconPngCache != null) {
                    continue;
                }
                try {
                    Drawable icon = pm.getApplicationIcon(e.packageName);
                    byte[] png = renderCircularIconPng(icon);
                    if (png != null) {
                        e.iconPngCache = png;
                        anyLoaded = true;
                    }
                } catch (Throwable ignored) {
                }
            }
            if (anyLoaded && !mDisposed) {
                mIconsVersion++;
            }
        });
    }

    public byte[] getAppIconPng(int index) {
        AppEntry e = entryAt(index);
        if (e == null) {
            return null;
        }
        if (e.iconPngCache != null) {
            return e.iconPngCache;
        }
        try {
            PackageManager pm = mActivity.getPackageManager();
            Drawable icon = pm.getApplicationIcon(e.packageName);
            byte[] png = renderCircularIconPng(icon);
            if (png != null) {
                e.iconPngCache = png;
            }
            return e.iconPngCache;
        } catch (Throwable t) {
            return null;
        }
    }

    // Renders any Android launcher Drawable (adaptive or legacy) clipped into a crisp circle.
    private static byte[] renderCircularIconPng(Drawable icon) {
        if (icon == null) {
            return null;
        }
        final int size = 128;
        Bitmap bmp = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(bmp);
        Path circle = new Path();
        circle.addCircle(size * 0.5f, size * 0.5f, size * 0.5f, Path.Direction.CW);
        canvas.clipPath(circle);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && icon instanceof AdaptiveIconDrawable) {
            AdaptiveIconDrawable adaptive = (AdaptiveIconDrawable) icon;
            int inset = size / 4;
            Drawable bg = adaptive.getBackground();
            if (bg != null) {
                bg.setBounds(-inset, -inset, size + inset, size + inset);
                bg.draw(canvas);
            }
            Drawable fg = adaptive.getForeground();
            if (fg != null) {
                fg.setBounds(-inset, -inset, size + inset, size + inset);
                fg.draw(canvas);
            }
        } else {
            icon.setBounds(0, 0, size, size);
            icon.draw(canvas);
        }

        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        bmp.compress(Bitmap.CompressFormat.PNG, 95, baos);
        bmp.recycle();
        return baos.toByteArray();
    }

    private AppEntry entryAt(int index) {
        synchronized (mApps) {
            if (index < 0 || index >= mApps.size()) {
                return null;
            }
            return mApps.get(index);
        }
    }

    private static int paletteColorForPackage(String pkg) {
        int[] palette = {
                0x2563EB, 0x059669, 0xDB2777, 0xD97706, 0x7C3AED, 0x0891B2, 0xDC2626, 0x4F46E5
        };
        int hash = pkg.hashCode();
        return palette[Math.abs(hash) % palette.length];
    }

    public PhoneAppSession createSession(String packageName, String activityName, boolean landscape) {
        PhoneAppSession session = new PhoneAppSession(this, packageName, activityName, landscape);
        mSessions.add(session);
        return session;
    }

    void removeSession(PhoneAppSession session) {
        mSessions.remove(session);
        if (mSessions.isEmpty()) {
            requestActivityFocus();
        }
    }

    private void requestActivityFocus() {
        if (mDisposed || mActivity == null) {
            return;
        }
        mActivity.runOnUiThread(() -> {
            try {
                if (mActivity.isFinishing() || mActivity.isDestroyed()) {
                    return;
                }
                View decor = mActivity.getWindow() != null ? mActivity.getWindow().getDecorView() : null;
                if (decor != null) {
                    decor.setFocusableInTouchMode(true);
                    decor.requestFocus();
                }
            } catch (Throwable ignored) {
            }
        });
    }

    IBinder getRemoteService() {
        return mShizuku.getRemoteService();
    }

    public void dispose() {
        if (mDisposed) {
            return;
        }
        mDisposed = true;
        Application app = mActivity != null ? mActivity.getApplication() : null;
        if (app != null && mLifecycleCallbacks != null) {
            app.unregisterActivityLifecycleCallbacks(mLifecycleCallbacks);
            mLifecycleCallbacks = null;
        }
        mBgExecutor.shutdownNow();
        List<PhoneAppSession> copy;
        synchronized (mSessions) {
            copy = new ArrayList<>(mSessions);
        }
        for (PhoneAppSession s : copy) {
            s.dispose();
        }
        mSessions.clear();
        if (mActivity != null) {
            mActivity.runOnUiThread(() -> {
                try {
                    if (!mActivity.isFinishing() && !mActivity.isDestroyed()) {
                        View decor = mActivity.getWindow() != null ? mActivity.getWindow().getDecorView() : null;
                        if (decor != null) {
                            decor.setFocusableInTouchMode(true);
                            decor.requestFocus();
                        }
                    }
                } catch (Throwable ignored) {
                }
            });
        }
    }

    // One live VirtualDisplay + ImageReader capture + input injection session for one phone app window.
    public static final class PhoneAppSession {
        // 64-byte RGBA stride-aligned resolutions (432*4 = 1728, 896*4 = 3584) avoid per-row padding copies.
        public static final int PORTRAIT_W = 432;
        public static final int PORTRAIT_H = 864;
        public static final int PORTRAIT_DPI = 200;

        public static final int LANDSCAPE_W = 896;
        public static final int LANDSCAPE_H = 504;
        public static final int LANDSCAPE_DPI = 180;

        private static final int MAX_BUFFER_BYTES = 896 * 864 * 4;
        private static final long MIN_CAPTURE_INTERVAL_MS = 30L;

        private final PhoneAppHost mHost;
        private final String mPackageName;
        private final String mActivityName;
        private final Object mBufferLock = new Object();
        private final Object mReaderLock = new Object();

        // Ping-pong pre-allocated direct buffers: eliminates main-thread CPU memcpy across rotation/frames.
        private final ByteBuffer[] mBuffers = new ByteBuffer[]{
                ByteBuffer.allocateDirect(MAX_BUFFER_BYTES),
                ByteBuffer.allocateDirect(MAX_BUFFER_BYTES),
        };
        private final byte[] mRowScratch = new byte[896 * 4];
        private int mFrontIdx = 0;
        private int mWriteIdx = 1;

        private HandlerThread mImageThread;
        private Handler mImageHandler;
        private HandlerThread mWorkerThread;
        private Handler mWorkerHandler;
        private ImageReader mImageReader;

        private volatile boolean mLandscape;
        private volatile int mDisplayW;
        private volatile int mDisplayH;
        private volatile int mBackW;
        private volatile int mBackH;
        private volatile int mFrontW;
        private volatile int mFrontH;
        private volatile boolean mHasNewFrame;
        private volatile int mDisplayId = -1;
        private volatile int mFrameCount;
        private volatile String mLastError = "";
        private volatile boolean mDisposed;

        private boolean mTouchActive;
        private long mTouchDownTime;
        private long mLastCaptureMs;
        private volatile float mPendingMoveU;
        private volatile float mPendingMoveV;
        private volatile boolean mMovePending;
        private long mLastMoveSentMs;
        private final Runnable mMoveRunnable = new Runnable() {
            @Override
            public void run() {
                if (mDisposed || !mTouchActive || !mMovePending) {
                    mMovePending = false;
                    return;
                }
                long now = SystemClock.uptimeMillis();
                long elapsed = now - mLastMoveSentMs;
                if (elapsed < 20L) {
                    mWorkerHandler.removeCallbacks(this);
                    mWorkerHandler.postDelayed(this, 20L - elapsed);
                    return;
                }
                mMovePending = false;
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                mLastMoveSentMs = now;
                float x = mPendingMoveU * mDisplayW;
                float y = mPendingMoveV * mDisplayH;
                sendMotionRemote(remote, id, MotionEvent.ACTION_MOVE, mTouchDownTime, now, x, y);
            }
        };

        PhoneAppSession(PhoneAppHost host, String pkg, String activity, boolean landscape) {
            mHost = host;
            mPackageName = pkg;
            mActivityName = activity != null ? activity : "";
            mLandscape = landscape;
            mDisplayW = landscape ? LANDSCAPE_W : PORTRAIT_W;
            mDisplayH = landscape ? LANDSCAPE_H : PORTRAIT_H;
            mFrontW = mDisplayW;
            mFrontH = mDisplayH;
            mBackW = mDisplayW;
            mBackH = mDisplayH;

            mImageThread = new HandlerThread("PhoneAppImg-" + pkg);
            mImageThread.start();
            mImageHandler = new Handler(mImageThread.getLooper());

            mWorkerThread = new HandlerThread("PhoneAppWork-" + pkg);
            mWorkerThread.start();
            mWorkerHandler = new Handler(mWorkerThread.getLooper());

            mWorkerHandler.post(this::startOnWorker);
        }

        public ByteBuffer getBuffer(int index) {
            return mBuffers[index == 1 ? 1 : 0];
        }

        public ByteBuffer getFrontBuffer() {
            return mBuffers[mFrontIdx];
        }

        public int getTexWidth() {
            return mFrontW;
        }

        public int getTexHeight() {
            return mFrontH;
        }

        public int getDisplayId() {
            return mDisplayId;
        }

        public int getFrameCount() {
            return mFrameCount;
        }

        public boolean isLandscape() {
            return mLandscape;
        }

        public String getLastError() {
            return mLastError;
        }

        private void startOnWorker() {
            if (mDisposed) {
                return;
            }
            IBinder remote = mHost.getRemoteService();
            if (remote == null || !remote.isBinderAlive()) {
                mLastError = "Shizuku service not connected";
                return;
            }
            try {
                int w = mLandscape ? LANDSCAPE_W : PORTRAIT_W;
                int h = mLandscape ? LANDSCAPE_H : PORTRAIT_H;
                int dpi = mLandscape ? LANDSCAPE_DPI : PORTRAIT_DPI;
                mDisplayW = w;
                mDisplayH = h;

                ImageReader reader = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 3);
                reader.setOnImageAvailableListener(this::onImageAvailable, mImageHandler);
                mImageReader = reader;

                int createdId = createDisplayRemote(remote, "CardboardXR-" + mPackageName, w, h, dpi, reader.getSurface());
                if (createdId < 0) {
                    synchronized (mReaderLock) {
                        if (mImageReader == reader) {
                            mImageReader = null;
                        }
                    }
                    try {
                        reader.setOnImageAvailableListener(null, null);
                        reader.close();
                    } catch (Throwable ignored) {
                    }
                    return;
                }
                if (mDisposed) {
                    releaseDisplayRemote(remote, createdId);
                    synchronized (mReaderLock) {
                        if (mImageReader == reader) {
                            mImageReader = null;
                        }
                    }
                    try {
                        reader.setOnImageAvailableListener(null, null);
                        reader.close();
                    } catch (Throwable ignored) {
                    }
                    return;
                }
                mDisplayId = createdId;
                String launchErr = launchAppRemote(remote, createdId, mPackageName, mActivityName);
                if (launchErr != null && !launchErr.isEmpty()) {
                    mLastError = launchErr;
                    Log.w(TAG, "Launch warning for " + mPackageName + ": " + launchErr);
                } else {
                    mLastError = "";
                    Log.i(TAG, "Launched " + mPackageName + " on display " + createdId + " (" + w + "x" + h + ")");
                }
            } catch (Throwable t) {
                mLastError = "Session start error: " + t.getMessage();
                Log.e(TAG, mLastError, t);
            }
        }

        private void onImageAvailable(ImageReader reader) {
            if (mDisposed) {
                return;
            }
            Image image = null;
            try {
                synchronized (mReaderLock) {
                    if (mDisposed || reader != mImageReader) {
                        return;
                    }
                    image = reader.acquireLatestImage();
                }
                if (image == null) {
                    return;
                }
                long now = SystemClock.uptimeMillis();
                if (now - mLastCaptureMs < MIN_CAPTURE_INTERVAL_MS) {
                    return;
                }
                mLastCaptureMs = now;
                Image.Plane[] planes = image.getPlanes();
                if (planes == null || planes.length == 0) {
                    return;
                }
                ByteBuffer src = planes[0].getBuffer();
                int rowStride = planes[0].getRowStride();
                int width = Math.min(896, image.getWidth());
                int height = Math.min(864, image.getHeight());
                int rowBytes = width * 4;
                if (rowBytes <= 0 || height <= 0 || rowBytes * height > MAX_BUFFER_BYTES) {
                    return;
                }

                synchronized (mBufferLock) {
                    ByteBuffer dst = mBuffers[mWriteIdx];
                    dst.clear();
                    if (rowStride == rowBytes) {
                        src.position(0);
                        src.limit(Math.min(src.capacity(), rowBytes * height));
                        dst.put(src);
                    } else {
                        for (int y = 0; y < height; y++) {
                            int offset = y * rowStride;
                            if (offset + rowBytes > src.capacity()) {
                                break;
                            }
                            src.position(offset);
                            src.get(mRowScratch, 0, rowBytes);
                            dst.put(mRowScratch, 0, rowBytes);
                        }
                    }
                    mBackW = width;
                    mBackH = height;
                    mFrameCount++;
                    mHasNewFrame = true;
                }
            } catch (Throwable ignored) {
            } finally {
                if (image != null) {
                    try {
                        image.close();
                    } catch (Throwable ignored) {
                    }
                }
            }
        }

        public int acquireFrontBufferIndex() {
            if (mDisposed || !mHasNewFrame) {
                return -1;
            }
            synchronized (mBufferLock) {
                if (!mHasNewFrame) {
                    return -1;
                }
                int w = mBackW;
                int h = mBackH;
                int bytes = w * h * 4;
                if (bytes <= 0 || bytes > MAX_BUFFER_BYTES) {
                    mHasNewFrame = false;
                    return -1;
                }
                int prev = mFrontIdx;
                mFrontIdx = mWriteIdx;
                mWriteIdx = prev;
                mFrontW = w;
                mFrontH = h;
                mHasNewFrame = false;
                return mFrontIdx;
            }
        }

        public int acquireFrontBufferPacked() {
            int idx = acquireFrontBufferIndex();
            if (idx < 0) {
                return -1;
            }
            return (idx << 28) | ((mFrontW & 0x3FFF) << 14) | (mFrontH & 0x3FFF);
        }

        public boolean syncFrontBuffer() {
            return acquireFrontBufferIndex() >= 0;
        }

        public void hideSystemKeyboard() {
            mHost.hideSystemKeyboard();
        }

        public void setLandscape(boolean landscape) {
            if (mDisposed || mLandscape == landscape) {
                return;
            }
            mLandscape = landscape;
            mWorkerHandler.post(() -> {
                if (mDisposed) {
                    return;
                }
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null || !remote.isBinderAlive()) {
                    return;
                }
                int newW = landscape ? LANDSCAPE_W : PORTRAIT_W;
                int newH = landscape ? LANDSCAPE_H : PORTRAIT_H;
                int newDpi = landscape ? LANDSCAPE_DPI : PORTRAIT_DPI;
                ImageReader newReader = null;
                ImageReader oldReader = mImageReader;
                try {
                    newReader = ImageReader.newInstance(newW, newH, PixelFormat.RGBA_8888, 3);
                    newReader.setOnImageAvailableListener(this::onImageAvailable, mImageHandler);
                    boolean ok = resizeDisplayRemote(remote, id, newW, newH, newDpi, newReader.getSurface());
                    if (ok) {
                        mDisplayW = newW;
                        mDisplayH = newH;
                        mImageReader = newReader;
                        newReader = null;
                        if (oldReader != null && oldReader != mImageReader) {
                            oldReader.close();
                        }
                        Log.i(TAG, "Rotated display " + id + " -> " + newW + "x" + newH);
                    }
                } catch (Throwable t) {
                    mLastError = "Rotate error: " + t.getMessage();
                } finally {
                    if (newReader != null) {
                        try {
                            newReader.close();
                        } catch (Throwable ignored) {
                        }
                    }
                }
            });
        }

        public void clickNormalized(float u, float v) {
            if (mDisposed) {
                return;
            }
            float cu = clamp01(u);
            float cv = clamp01(v);
            mWorkerHandler.post(() -> {
                mMovePending = false;
                mWorkerHandler.removeCallbacks(mMoveRunnable);
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                mHost.noteUserInteraction(id);
                float x = cu * mDisplayW;
                float y = cv * mDisplayH;
                if (mTouchActive) {
                    long now = SystemClock.uptimeMillis();
                    sendMotionRemote(remote, id, MotionEvent.ACTION_CANCEL, mTouchDownTime, now, x, y);
                    mTouchActive = false;
                }
                sendTapRemote(remote, id, x, y);
            });
        }

        public void touchDown(float u, float v) {
            if (mDisposed) {
                return;
            }
            float cu = clamp01(u);
            float cv = clamp01(v);
            long eventTime = SystemClock.uptimeMillis();
            mWorkerHandler.post(() -> {
                mMovePending = false;
                mWorkerHandler.removeCallbacks(mMoveRunnable);
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                mHost.noteUserInteraction(id);
                float x = cu * mDisplayW;
                float y = cv * mDisplayH;
                if (mTouchActive) {
                    sendMotionRemote(remote, id, MotionEvent.ACTION_CANCEL, mTouchDownTime, eventTime, x, y);
                    mTouchActive = false;
                }
                mTouchDownTime = eventTime;
                mTouchActive = true;
                sendMotionRemote(remote, id, MotionEvent.ACTION_DOWN, eventTime, eventTime, x, y);
            });
        }

        public void touchMove(float u, float v) {
            if (mDisposed) {
                return;
            }
            mPendingMoveU = clamp01(u);
            mPendingMoveV = clamp01(v);
            if (!mMovePending) {
                mMovePending = true;
                mWorkerHandler.post(mMoveRunnable);
            }
        }

        public void touchUp(float u, float v, boolean cancel) {
            if (mDisposed) {
                return;
            }
            float cu = clamp01(u);
            float cv = clamp01(v);
            long eventTime = SystemClock.uptimeMillis();
            mWorkerHandler.post(() -> {
                mMovePending = false;
                mWorkerHandler.removeCallbacks(mMoveRunnable);
                if (!mTouchActive) {
                    return;
                }
                mTouchActive = false;
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                mHost.noteUserInteraction(id);
                float x = cu * mDisplayW;
                float y = cv * mDisplayH;
                int action = cancel ? MotionEvent.ACTION_CANCEL : MotionEvent.ACTION_UP;
                sendMotionRemote(remote, id, action, mTouchDownTime, eventTime, x, y);
            });
        }

        public void scrollSwipe(float deltaNormY) {
            if (mDisposed || Math.abs(deltaNormY) < 0.01f) {
                return;
            }
            mWorkerHandler.post(() -> {
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                float cx = mDisplayW * 0.5f;
                float y1 = mDisplayH * 0.55f;
                float y2 = Math.max(mDisplayH * 0.12f, Math.min(mDisplayH * 0.90f, y1 - deltaNormY * mDisplayH));
                sendSwipeRemote(remote, id, cx, y1, cx, y2, 160);
            });
        }

        public void pressBack() {
            sendKey(KeyEvent.KEYCODE_BACK);
        }

        public void relaunchApp() {
            if (mDisposed) {
                return;
            }
            mWorkerHandler.post(() -> {
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                launchAppRemote(remote, id, mPackageName, mActivityName);
            });
        }

        public void typeText(String text, boolean submit) {
            if (mDisposed) {
                return;
            }
            if ("\b".equals(text)) {
                sendKey(KeyEvent.KEYCODE_DEL);
                return;
            }
            mWorkerHandler.post(() -> {
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                if (text != null && !text.isEmpty()) {
                    sendTextRemote(remote, id, text);
                }
                if (submit) {
                    sendKeyRemote(remote, id, KeyEvent.KEYCODE_ENTER);
                }
            });
        }

        private void sendKey(int keyCode) {
            if (mDisposed) {
                return;
            }
            mWorkerHandler.post(() -> {
                int id = mDisplayId;
                IBinder remote = mHost.getRemoteService();
                if (id < 0 || remote == null) {
                    return;
                }
                sendKeyRemote(remote, id, keyCode);
            });
        }

        public void dispose() {
            if (mDisposed) {
                return;
            }
            mDisposed = true;
            mHost.removeSession(this);
            final int id = mDisplayId;
            mDisplayId = -1;
            if (mImageHandler != null) {
                mImageHandler.post(() -> {
                    synchronized (mReaderLock) {
                        if (mImageReader != null) {
                            try {
                                mImageReader.setOnImageAvailableListener(null, null);
                                mImageReader.close();
                            } catch (Throwable ignored) {
                            }
                            mImageReader = null;
                        }
                    }
                    try {
                        mImageThread.quitSafely();
                    } catch (Throwable ignored) {
                    }
                });
            }
            mWorkerHandler.post(() -> {
                IBinder remote = mHost.getRemoteService();
                if (id >= 0 && remote != null && remote.isBinderAlive()) {
                    releaseDisplayRemote(remote, id);
                }
                try {
                    mWorkerThread.quitSafely();
                } catch (Throwable ignored) {
                }
            });
        }

        private int createDisplayRemote(IBinder remote, String name, int w, int h, int dpi, Surface surface) {
            Parcel data = Parcel.obtain();
            Parcel reply = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeString(name);
                data.writeInt(w);
                data.writeInt(h);
                data.writeInt(dpi);
                data.writeInt(surface != null ? 1 : 0);
                if (surface != null) {
                    surface.writeToParcel(data, 0);
                }
                remote.transact(ShizukuAppService.TX_CREATE_DISPLAY, data, reply, 0);
                reply.readException();
                int id = reply.readInt();
                String err = reply.readString();
                if (id < 0) {
                    mLastError = err != null && !err.isEmpty() ? err : "VirtualDisplay creation failed";
                    Log.w(TAG, mLastError);
                }
                return id;
            } catch (RemoteException e) {
                mLastError = "Create display IPC error: " + e.getMessage();
                return -1;
            } finally {
                data.recycle();
                reply.recycle();
            }
        }

        private boolean resizeDisplayRemote(IBinder remote, int id, int w, int h, int dpi, Surface surface) {
            Parcel data = Parcel.obtain();
            Parcel reply = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeInt(w);
                data.writeInt(h);
                data.writeInt(dpi);
                data.writeInt(surface != null ? 1 : 0);
                if (surface != null) {
                    surface.writeToParcel(data, 0);
                }
                remote.transact(ShizukuAppService.TX_RESIZE_DISPLAY, data, reply, 0);
                reply.readException();
                boolean ok = reply.readInt() != 0;
                String err = reply.readString();
                if (!ok && err != null) {
                    mLastError = err;
                }
                return ok;
            } catch (RemoteException e) {
                mLastError = "Resize display IPC error: " + e.getMessage();
                return false;
            } finally {
                data.recycle();
                reply.recycle();
            }
        }

        private static void releaseDisplayRemote(IBinder remote, int id) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                remote.transact(ShizukuAppService.TX_RELEASE_DISPLAY, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static String launchAppRemote(IBinder remote, int id, String pkg, String act) {
            Parcel data = Parcel.obtain();
            Parcel reply = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeString(pkg);
                data.writeString(act);
                remote.transact(ShizukuAppService.TX_LAUNCH_APP, data, reply, 0);
                reply.readException();
                return reply.readString();
            } catch (Throwable t) {
                return "Launch IPC error: " + t.getMessage();
            } finally {
                data.recycle();
                reply.recycle();
            }
        }

        private static void sendTapRemote(IBinder remote, int id, float x, float y) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeFloat(x);
                data.writeFloat(y);
                remote.transact(ShizukuAppService.TX_INJECT_TAP, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static void sendMotionRemote(IBinder remote, int id, int action, long downTime, long eventTime, float x, float y) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeInt(action);
                data.writeLong(downTime);
                data.writeLong(eventTime);
                data.writeFloat(x);
                data.writeFloat(y);
                remote.transact(ShizukuAppService.TX_INJECT_MOTION, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static void sendSwipeRemote(IBinder remote, int id, float x1, float y1, float x2, float y2, int durationMs) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeFloat(x1);
                data.writeFloat(y1);
                data.writeFloat(x2);
                data.writeFloat(y2);
                data.writeInt(durationMs);
                remote.transact(ShizukuAppService.TX_INJECT_SWIPE, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static void sendKeyRemote(IBinder remote, int id, int keyCode) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeInt(keyCode);
                remote.transact(ShizukuAppService.TX_INJECT_KEY, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static void sendTextRemote(IBinder remote, int id, String text) {
            Parcel data = Parcel.obtain();
            try {
                data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
                data.writeInt(id);
                data.writeString(text);
                remote.transact(ShizukuAppService.TX_INJECT_TEXT, data, null, IBinder.FLAG_ONEWAY);
            } catch (Throwable ignored) {
            } finally {
                data.recycle();
            }
        }

        private static float clamp01(float v) {
            return Math.max(0f, Math.min(1f, v));
        }
    }
}
