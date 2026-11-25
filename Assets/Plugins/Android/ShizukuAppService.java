package com.brenpoly.cardboardxr.phoneapps;

import android.annotation.SuppressLint;
import android.app.ActivityManager;
import android.content.AttributionSource;
import android.content.ComponentName;
import android.content.Context;
import android.content.ContextWrapper;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.hardware.display.VirtualDisplayConfig;
import android.os.Binder;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Looper;
import android.os.Parcel;
import android.os.Process;
import android.os.RemoteException;
import android.os.SystemClock;
import android.provider.Settings;
import android.util.Log;
import android.view.Display;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;

// Privileged Shizuku UserService (runs in Shizuku's UID 2000 shell / UID 0 root process).
// Creates offscreen VirtualDisplays backed directly by CardboardXR's ImageReader Surfaces, launches
// real installed Android apps onto those displays, and injects MotionEvent/KeyEvent input per
// displayId without needing AIDL compilation or socket video streaming.
public class ShizukuAppService extends Binder {
    private static final String TAG = "CardboardXRShizukuSvc";

    public static final String DESCRIPTOR = "com.brenpoly.cardboardxr.phoneapps.IShizukuAppService";
    public static final int TX_PING = IBinder.FIRST_CALL_TRANSACTION + 1;
    public static final int TX_REGISTER_CLIENT = IBinder.FIRST_CALL_TRANSACTION + 2;
    public static final int TX_CREATE_DISPLAY = IBinder.FIRST_CALL_TRANSACTION + 3;
    public static final int TX_RESIZE_DISPLAY = IBinder.FIRST_CALL_TRANSACTION + 4;
    public static final int TX_RELEASE_DISPLAY = IBinder.FIRST_CALL_TRANSACTION + 5;
    public static final int TX_LAUNCH_APP = IBinder.FIRST_CALL_TRANSACTION + 6;
    public static final int TX_INJECT_MOTION = IBinder.FIRST_CALL_TRANSACTION + 7;
    public static final int TX_INJECT_SWIPE = IBinder.FIRST_CALL_TRANSACTION + 8;
    public static final int TX_INJECT_KEY = IBinder.FIRST_CALL_TRANSACTION + 9;
    public static final int TX_INJECT_TEXT = IBinder.FIRST_CALL_TRANSACTION + 10;
    public static final int TX_DESTROY = IBinder.FIRST_CALL_TRANSACTION + 11;
    public static final int TX_GRANT_NOTIFICATION_LISTENER = IBinder.FIRST_CALL_TRANSACTION + 12;
    public static final int TX_SET_TORCH = IBinder.FIRST_CALL_TRANSACTION + 13;
    public static final int TX_INJECT_TAP = IBinder.FIRST_CALL_TRANSACTION + 14;
    public static final int TX_CONTAIN_REDIRECT = IBinder.FIRST_CALL_TRANSACTION + 15;

    // Shizuku's internal destroy transaction (moe.shizuku.server.UserService.USER_SERVICE_TRANSACTION_destroy).
    private static final int SHIZUKU_TX_DESTROY = 16777115;
    private static final int DISPLAY_IME_POLICY_HIDE = 2;
    private static final int INJECT_INPUT_EVENT_MODE_WAIT_FOR_FINISH = 2;

    // DisplayManager virtual display flags (AOSP constants; no ROTATES_WITH_CONTENT so portrait apps stay upright,
    // and no OWN_DISPLAY_GROUP so VirtualDisplays share PowerGroup 0 awake state with CardboardXR).
    private static final int VDF_PUBLIC = 1 << 0;
    private static final int VDF_OWN_CONTENT_ONLY = 1 << 3;
    private static final int VDF_SUPPORTS_TOUCH = 1 << 6;
    private static final int VDF_DESTROY_CONTENT_ON_REMOVAL = 1 << 8;
    private static final int VDF_TRUSTED = 1 << 10;
    private static final int VDF_ALWAYS_UNLOCKED = 1 << 12;

    private static final int[] FLAG_LADDER = {
            VDF_PUBLIC | VDF_OWN_CONTENT_ONLY | VDF_SUPPORTS_TOUCH | VDF_DESTROY_CONTENT_ON_REMOVAL
                    | VDF_TRUSTED | VDF_ALWAYS_UNLOCKED,
            VDF_PUBLIC | VDF_OWN_CONTENT_ONLY | VDF_SUPPORTS_TOUCH | VDF_DESTROY_CONTENT_ON_REMOVAL
                    | VDF_TRUSTED,
            VDF_PUBLIC | VDF_OWN_CONTENT_ONLY | VDF_SUPPORTS_TOUCH | VDF_DESTROY_CONTENT_ON_REMOVAL,
            VDF_PUBLIC | VDF_OWN_CONTENT_ONLY | VDF_SUPPORTS_TOUCH,
    };

    private static final class DisplayRecord {
        final VirtualDisplay virtualDisplay;
        Surface surface;

        DisplayRecord(VirtualDisplay vd, Surface s) {
            virtualDisplay = vd;
            surface = s;
        }
    }

    // Spoofs AttributionSource / package identity to com.android.shell (or android for UID 0) so
    // DisplayManager and system services accept calls originating inside Shizuku's UserService.
    private static final class ShellContext extends ContextWrapper {
        private final String mOpPackage;

        ShellContext(Context base) {
            super(base);
            mOpPackage = Process.myUid() == 0 ? "android" : "com.android.shell";
        }

        @Override
        public String getPackageName() {
            return mOpPackage;
        }

        @Override
        public String getOpPackageName() {
            return mOpPackage;
        }

        @Override
        public Context getApplicationContext() {
            return this;
        }

        @Override
        public AttributionSource getAttributionSource() {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                return new AttributionSource.Builder(Process.myUid())
                        .setPackageName(mOpPackage)
                        .build();
            }
            return super.getAttributionSource();
        }
    }

    private Context mRawContext;
    private Context mShellContext;
    private final List<DisplayManager> mDisplayManagers = new ArrayList<>();

    private final ConcurrentHashMap<Integer, DisplayRecord> mDisplays = new ConcurrentHashMap<>();
    private final Object mClientLock = new Object();
    private IBinder mClientToken;
    private IBinder.DeathRecipient mClientDeathRecipient;

    private final HandlerThread mInputThread;
    private final Handler mInputHandler;
    private volatile Object mInputManager;
    private volatile Method mInjectInputEventMethod;
    private volatile Method mSetDisplayIdMethod;
    private volatile boolean mInputInitAttempted;
    private volatile Object mWindowManager;
    private volatile Method mSetDisplayImePolicyMethod;
    private volatile Method mSetInTouchModeMethod;
    private volatile boolean mWindowInitAttempted;
    private final Set<Integer> mDisplaysNeedingTouchMode = Collections.synchronizedSet(new HashSet<>());
    private volatile int mLastTouchedDisplayId = -1;
    private volatile long mLastVirtualInputMs = 0L;
    private volatile String mVrPackage = "";
    private volatile String mVrComponent = "";
    private volatile String mHomePackage = "";
    private volatile boolean mRedirectCheckInFlight = false;
    private volatile Object mActivityTaskManager;
    private volatile Method mGetTasksMethod;
    private volatile Method mMoveRootTaskMethod;
    private volatile boolean mTaskManagerInitAttempted;
    private final Runnable mRedirectCheckRunnable = () -> containDisplay0RedirectIfNeeded(mLastTouchedDisplayId);

    private final KeyCharacterMap mKeyCharMap = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
    private final Object mMotionLock = new Object();
    private final MotionEvent.PointerProperties[] mTouchProps = new MotionEvent.PointerProperties[1];
    private final MotionEvent.PointerCoords[] mTouchCoords = new MotionEvent.PointerCoords[1];
    private volatile String mLastError = "";

    public ShizukuAppService() {
        mInputThread = new HandlerThread("ShizukuInput");
        mInputThread.start();
        mInputHandler = new Handler(mInputThread.getLooper());
        initContext(null);
    }

    public ShizukuAppService(Context context) {
        mInputThread = new HandlerThread("ShizukuInput");
        mInputThread.start();
        mInputHandler = new Handler(mInputThread.getLooper());
        initContext(context);
    }

    @SuppressLint({"PrivateApi", "DiscouragedPrivateApi"})
    private void initContext(Context provided) {
        try {
            Context base = provided;
            if (base == null) {
                Class<?> atClass = Class.forName("android.app.ActivityThread");
                Object at = atClass.getMethod("currentActivityThread").invoke(null);
                if (at != null) {
                    base = (Context) atClass.getMethod("getSystemContext").invoke(at);
                }
            }
            mRawContext = base;
            mDisplayManagers.clear();
            if (base != null) {
                String targetPkg = Process.myUid() == 0 ? "android" : "com.android.shell";
                mShellContext = new ShellContext(base);

                // Candidate 1: genuine ContextImpl for com.android.shell (or android for UID 0).
                try {
                    Context pkgCtx = base.createPackageContext(targetPkg, Context.CONTEXT_IGNORE_SECURITY);
                    Object dm1 = pkgCtx.getSystemService(Context.DISPLAY_SERVICE);
                    if (dm1 instanceof DisplayManager) {
                        mDisplayManagers.add((DisplayManager) dm1);
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "createPackageContext(" + targetPkg + ") failed: " + t.getMessage());
                }

                // Candidate 2: scrcpy-style DisplayManager(Context) constructed directly on ShellContext.
                try {
                    Constructor<DisplayManager> ctor = DisplayManager.class.getDeclaredConstructor(Context.class);
                    ctor.setAccessible(true);
                    DisplayManager dm2 = ctor.newInstance(mShellContext);
                    if (dm2 != null && !mDisplayManagers.contains(dm2)) {
                        mDisplayManagers.add(dm2);
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "DisplayManager(ShellContext) ctor failed: " + t.getMessage());
                }

                // Candidate 3: fallback raw base DisplayManager.
                try {
                    Object dm3 = base.getSystemService(Context.DISPLAY_SERVICE);
                    if (dm3 instanceof DisplayManager && !mDisplayManagers.contains(dm3)) {
                        mDisplayManagers.add((DisplayManager) dm3);
                    }
                } catch (Throwable ignored) {
                }

                Log.i(TAG, "Initialized " + mDisplayManagers.size() + " DisplayManager candidates for " + targetPkg);
            }
        } catch (Throwable t) {
            mLastError = "Context init error: " + t.getMessage();
            Log.e(TAG, mLastError, t);
        }
        ensureInputManager();
        ensureWindowManager();
    }

    @SuppressLint("PrivateApi")
    private void ensureWindowManager() {
        if (mWindowInitAttempted) {
            return;
        }
        mWindowInitAttempted = true;
        try {
            Class<?> smClass = Class.forName("android.os.ServiceManager");
            IBinder wmBinder = (IBinder) smClass.getMethod("getService", String.class).invoke(null, "window");
            if (wmBinder != null) {
                Class<?> stubClass = Class.forName("android.view.IWindowManager$Stub");
                mWindowManager = stubClass.getMethod("asInterface", IBinder.class).invoke(null, wmBinder);
                mSetDisplayImePolicyMethod = mWindowManager.getClass()
                        .getMethod("setDisplayImePolicy", int.class, int.class);
                try {
                    mSetInTouchModeMethod = mWindowManager.getClass()
                            .getMethod("setInTouchMode", boolean.class, int.class);
                } catch (Throwable ignored) {
                    try {
                        mSetInTouchModeMethod = mWindowManager.getClass()
                                .getMethod("setInTouchModeOnDisplay", boolean.class, int.class);
                    } catch (Throwable ignored2) {
                    }
                }
                Log.i(TAG, "IWindowManager.setDisplayImePolicy ready");
            }
        } catch (Throwable t) {
            Log.w(TAG, "IWindowManager.setDisplayImePolicy unavailable: " + t.getMessage());
        }
    }

    private void ensureDisplayTouchMode(int displayId) {
        if (displayId < 0) {
            return;
        }
        ensureWindowManager();
        Method m = mSetInTouchModeMethod;
        Object wm = mWindowManager;
        if (m == null || wm == null) {
            return;
        }
        try {
            m.invoke(wm, true, displayId);
        } catch (Throwable ignored) {
        }
    }

    private void suppressImeOnDisplay(int displayId) {
        if (displayId <= 0) {
            return;
        }
        ensureWindowManager();
        if (mWindowManager == null || mSetDisplayImePolicyMethod == null) {
            return;
        }
        try {
            mSetDisplayImePolicyMethod.invoke(mWindowManager, displayId, DISPLAY_IME_POLICY_HIDE);
            Log.i(TAG, "Set DISPLAY_IME_POLICY_HIDE on displayId=" + displayId);
        } catch (Throwable t) {
            Log.w(TAG, "setDisplayImePolicy failed for displayId=" + displayId + ": " + t.getMessage());
        }
    }

    @SuppressLint({"PrivateApi", "DiscouragedPrivateApi"})
    private void ensureInputManager() {
        if (mInputInitAttempted) {
            return;
        }
        mInputInitAttempted = true;
        try {
            Class<?> smClass = Class.forName("android.os.ServiceManager");
            IBinder inputBinder = (IBinder) smClass.getMethod("getService", String.class).invoke(null, "input");
            if (inputBinder != null) {
                Class<?> stubClass = Class.forName("android.hardware.input.IInputManager$Stub");
                mInputManager = stubClass.getMethod("asInterface", IBinder.class).invoke(null, inputBinder);
                mInjectInputEventMethod = mInputManager.getClass().getMethod("injectInputEvent", InputEvent.class, int.class);
            }
            try {
                mSetDisplayIdMethod = InputEvent.class.getMethod("setDisplayId", int.class);
            } catch (NoSuchMethodException e) {
                mSetDisplayIdMethod = InputEvent.class.getDeclaredMethod("setDisplayId", int.class);
                mSetDisplayIdMethod.setAccessible(true);
            }
            Log.i(TAG, "IInputManager ready for uid=" + Process.myUid());
        } catch (Throwable t) {
            Log.w(TAG, "IInputManager reflection fallback active: " + t);
        }
    }

    @Override
    protected boolean onTransact(int code, Parcel data, Parcel reply, int flags) throws RemoteException {
        if (code == SHIZUKU_TX_DESTROY) {
            releaseAllDisplays();
            if (reply != null) {
                reply.writeNoException();
            }
            scheduleShutdown();
            return true;
        }

        switch (code) {
            case TX_PING: {
                data.enforceInterface(DESCRIPTOR);
                reply.writeNoException();
                reply.writeInt(Process.myUid());
                return true;
            }
            case TX_REGISTER_CLIENT: {
                data.enforceInterface(DESCRIPTOR);
                IBinder token = data.readStrongBinder();
                if (data.dataAvail() > 0) {
                    String vrPkg = data.readString();
                    String vrComp = data.dataAvail() > 0 ? data.readString() : "";
                    if (vrPkg != null && !vrPkg.isEmpty()) {
                        mVrPackage = vrPkg;
                    }
                    if (vrComp != null && !vrComp.isEmpty()) {
                        mVrComponent = vrComp;
                    }
                }
                registerClientToken(token);
                reply.writeNoException();
                return true;
            }
            case TX_CREATE_DISPLAY: {
                data.enforceInterface(DESCRIPTOR);
                String name = data.readString();
                int width = data.readInt();
                int height = data.readInt();
                int dpi = data.readInt();
                Surface surface = data.readInt() != 0 ? Surface.CREATOR.createFromParcel(data) : null;
                int displayId = createVirtualDisplay(name, width, height, dpi, surface);
                reply.writeNoException();
                reply.writeInt(displayId);
                reply.writeString(displayId >= 0 ? "" : mLastError);
                return true;
            }
            case TX_RESIZE_DISPLAY: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                int width = data.readInt();
                int height = data.readInt();
                int dpi = data.readInt();
                Surface surface = data.readInt() != 0 ? Surface.CREATOR.createFromParcel(data) : null;
                boolean ok = resizeVirtualDisplay(displayId, width, height, dpi, surface);
                reply.writeNoException();
                reply.writeInt(ok ? 1 : 0);
                reply.writeString(ok ? "" : mLastError);
                return true;
            }
            case TX_RELEASE_DISPLAY: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                releaseDisplay(displayId);
                if (reply != null) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_LAUNCH_APP: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                String pkg = data.readString();
                String activity = data.readString();
                String err = launchAppOnDisplay(displayId, pkg, activity);
                reply.writeNoException();
                reply.writeString(err);
                return true;
            }
            case TX_INJECT_MOTION: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                int action = data.readInt();
                long downTime = data.readLong();
                long eventTime = data.readLong();
                float x = data.readFloat();
                float y = data.readFloat();
                injectMotionEvent(displayId, action, downTime, eventTime, x, y);
                if (reply != null && (flags & IBinder.FLAG_ONEWAY) == 0) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_INJECT_SWIPE: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                float x1 = data.readFloat();
                float y1 = data.readFloat();
                float x2 = data.readFloat();
                float y2 = data.readFloat();
                int durationMs = data.readInt();
                injectSmoothSwipe(displayId, x1, y1, x2, y2, durationMs);
                if (reply != null) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_INJECT_KEY: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                int keyCode = data.readInt();
                injectKeyCode(displayId, keyCode);
                if (reply != null) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_INJECT_TEXT: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                String text = data.readString();
                injectText(displayId, text);
                if (reply != null) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_DESTROY: {
                data.enforceInterface(DESCRIPTOR);
                releaseAllDisplays();
                if (reply != null) {
                    reply.writeNoException();
                }
                scheduleShutdown();
                return true;
            }
            case TX_GRANT_NOTIFICATION_LISTENER: {
                data.enforceInterface(DESCRIPTOR);
                String component = data.readString();
                boolean ok = grantNotificationListener(component);
                if (reply != null) {
                    reply.writeNoException();
                    reply.writeInt(ok ? 1 : 0);
                }
                return true;
            }
            case TX_SET_TORCH: {
                data.enforceInterface(DESCRIPTOR);
                boolean on = data.readInt() != 0;
                int res = setTorchPrivileged(on);
                if (reply != null) {
                    reply.writeNoException();
                    reply.writeInt(res);
                }
                return true;
            }
            case TX_INJECT_TAP: {
                data.enforceInterface(DESCRIPTOR);
                int displayId = data.readInt();
                float x = data.readFloat();
                float y = data.readFloat();
                injectTap(displayId, x, y);
                if (reply != null && (flags & IBinder.FLAG_ONEWAY) == 0) {
                    reply.writeNoException();
                }
                return true;
            }
            case TX_CONTAIN_REDIRECT: {
                data.enforceInterface(DESCRIPTOR);
                final int targetDisplayId = data.readInt();
                final String vrPkg = data.dataAvail() > 0 ? data.readString() : "";
                final String vrComp = data.dataAvail() > 0 ? data.readString() : "";
                if (vrPkg != null && !vrPkg.isEmpty()) {
                    mVrPackage = vrPkg;
                }
                if (vrComp != null && !vrComp.isEmpty()) {
                    mVrComponent = vrComp;
                }
                mInputHandler.post(() -> containDisplay0RedirectIfNeeded(targetDisplayId));
                if (reply != null && (flags & IBinder.FLAG_ONEWAY) == 0) {
                    reply.writeNoException();
                }
                return true;
            }
        }
        return super.onTransact(code, data, reply, flags);
    }

    private void registerClientToken(IBinder token) {
        synchronized (mClientLock) {
            if (mClientToken != null && mClientDeathRecipient != null) {
                try {
                    mClientToken.unlinkToDeath(mClientDeathRecipient, 0);
                } catch (Throwable ignored) {
                }
            }
            mClientToken = token;
            if (token == null) {
                mClientDeathRecipient = null;
                return;
            }
            mClientDeathRecipient = () -> {
                Log.i(TAG, "Client process died; releasing all VirtualDisplays");
                releaseAllDisplays();
                scheduleShutdown();
            };
            try {
                token.linkToDeath(mClientDeathRecipient, 0);
            } catch (Throwable t) {
                releaseAllDisplays();
            }
        }
    }

    private void scheduleShutdown() {
        new Handler(Looper.getMainLooper()).postDelayed(() -> {
            try {
                mInputThread.quitSafely();
            } catch (Throwable ignored) {
            }
            System.exit(0);
        }, 80);
    }

    private int createVirtualDisplay(String name, int width, int height, int dpi, Surface surface) {
        if (mDisplayManagers.isEmpty()) {
            mLastError = "DisplayManager unavailable in Shell service";
            if (surface != null) {
                surface.release();
            }
            return -1;
        }
        if (surface == null || !surface.isValid()) {
            mLastError = "Invalid Surface passed to createVirtualDisplay";
            return -1;
        }

        StringBuilder errors = new StringBuilder();
        for (int c = 0; c < mDisplayManagers.size(); c++) {
            DisplayManager dm = mDisplayManagers.get(c);
            for (int displayFlags : FLAG_LADDER) {
                VirtualDisplay vd = tryCreateWithFlags(dm, name, width, height, dpi, surface, displayFlags, errors);
                if (vd != null) {
                    Display d = vd.getDisplay();
                    if (d != null) {
                        int id = d.getDisplayId();
                        mDisplays.put(id, new DisplayRecord(vd, surface));
                        mLastError = "";
                        suppressImeOnDisplay(id);
                        Log.i(TAG, "Created VirtualDisplay id=" + id + " " + width + "x" + height
                                + " candidate=" + c + " flags=0x" + Integer.toHexString(displayFlags));
                        return id;
                    }
                    try {
                        vd.release();
                    } catch (Throwable ignored) {
                    }
                }
            }
        }

        surface.release();
        mLastError = errors.length() > 0 ? errors.toString() : "All VirtualDisplay candidates failed";
        Log.w(TAG, "createVirtualDisplay failed: " + mLastError);
        return -1;
    }

    private VirtualDisplay tryCreateWithFlags(
            DisplayManager dm, String name, int width, int height, int dpi, Surface surface, int flags, StringBuilder errors) {
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                VirtualDisplayConfig cfg = new VirtualDisplayConfig.Builder(name, width, height, dpi)
                        .setFlags(flags)
                        .setSurface(surface)
                        .build();
                return dm.createVirtualDisplay(cfg);
            } catch (Throwable t) {
                appendError(errors, t.getClass().getSimpleName() + ": " + t.getMessage());
            }
        }
        try {
            return dm.createVirtualDisplay(name, width, height, dpi, surface, flags);
        } catch (Throwable t) {
            appendError(errors, t.getClass().getSimpleName() + ": " + t.getMessage());
            return null;
        }
    }

    private static void appendError(StringBuilder errors, String msg) {
        if (msg == null || msg.isEmpty()) {
            return;
        }
        if (errors.indexOf(msg) >= 0) {
            return;
        }
        if (errors.length() > 0) {
            errors.append(" | ");
        }
        if (errors.length() < 320) {
            errors.append(msg);
        }
    }

    private boolean resizeVirtualDisplay(int displayId, int width, int height, int dpi, Surface newSurface) {
        DisplayRecord rec = mDisplays.get(displayId);
        if (rec == null || rec.virtualDisplay == null) {
            mLastError = "VirtualDisplay not found: " + displayId;
            if (newSurface != null) {
                newSurface.release();
            }
            return false;
        }
        try {
            if (newSurface != null) {
                rec.virtualDisplay.setSurface(newSurface);
            }
            rec.virtualDisplay.resize(width, height, dpi);
            Surface old = rec.surface;
            if (newSurface != null) {
                rec.surface = newSurface;
            }
            if (old != null && old != newSurface) {
                old.release();
            }
            suppressImeOnDisplay(displayId);
            return true;
        } catch (Throwable t) {
            mLastError = "resizeVirtualDisplay failed: " + t.getMessage();
            if (newSurface != null) {
                newSurface.release();
            }
            return false;
        }
    }

    private void releaseDisplay(int displayId) {
        mDisplaysNeedingTouchMode.remove(displayId);
        if (mLastTouchedDisplayId == displayId) {
            mLastTouchedDisplayId = -1;
        }
        DisplayRecord rec = mDisplays.remove(displayId);
        if (rec == null) {
            return;
        }
        try {
            if (rec.virtualDisplay != null) {
                rec.virtualDisplay.release();
            }
        } catch (Throwable ignored) {
        }
        try {
            if (rec.surface != null) {
                rec.surface.release();
            }
        } catch (Throwable ignored) {
        }
        Log.i(TAG, "Released VirtualDisplay id=" + displayId);
    }

    private void releaseAllDisplays() {
        for (Map.Entry<Integer, DisplayRecord> e : mDisplays.entrySet()) {
            releaseDisplay(e.getKey());
        }
        mDisplays.clear();
    }

    private String launchAppOnDisplay(int displayId, String pkg, String activity) {
        if (displayId < 0 || pkg == null || pkg.isEmpty()) {
            return "Invalid displayId or package";
        }
        String component = (activity != null && !activity.isEmpty()) ? (pkg + "/" + activity) : null;
        String[] amCmd = component != null
                ? new String[]{"am", "start", "--display", String.valueOf(displayId), "-f", "0x10200000", "-n", component}
                : new String[]{"am", "start", "--display", String.valueOf(displayId), "-f", "0x10200000",
                        "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", pkg};
        ExecResult res = execCommand(amCmd);
        if (res.exitCode == 0 && !res.output.contains("Error:") && !res.output.contains("Exception")) {
            mDisplaysNeedingTouchMode.add(displayId);
            Log.i(TAG, "am start ok display=" + displayId + " pkg=" + pkg + " out=" + res.output);
            return "";
        }
        String[] cmdAct = component != null
                ? new String[]{"cmd", "activity", "start-activity", "--display", String.valueOf(displayId), "-f", "0x10200000", "-n", component}
                : new String[]{"cmd", "activity", "start-activity", "--display", String.valueOf(displayId), "-f", "0x10200000",
                        "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", pkg};
        ExecResult fallback = execCommand(cmdAct);
        if (fallback.exitCode == 0 && !fallback.output.contains("Error:") && !fallback.output.contains("Exception")) {
            mDisplaysNeedingTouchMode.add(displayId);
            Log.i(TAG, "cmd activity start ok display=" + displayId + " pkg=" + pkg);
            return "";
        }
        String err = !fallback.output.isEmpty() ? fallback.output : res.output;
        Log.w(TAG, "launch failed display=" + displayId + " pkg=" + pkg + " err=" + err);
        return err;
    }

    private boolean injectInputObject(InputEvent event, int displayId) {
        return injectInputObject(event, displayId, 0);
    }

    private boolean injectInputObject(InputEvent event, int displayId, int mode) {
        if (event == null || mInputManager == null || mInjectInputEventMethod == null) {
            return false;
        }
        try {
            if (mSetDisplayIdMethod != null && displayId >= 0) {
                mSetDisplayIdMethod.invoke(event, displayId);
            }
            Object result = mInjectInputEventMethod.invoke(mInputManager, event, mode);
            boolean ok = result instanceof Boolean && (Boolean) result;
            if (!ok && mode != 0) {
                result = mInjectInputEventMethod.invoke(mInputManager, event, 0);
                ok = result instanceof Boolean && (Boolean) result;
            }
            return ok;
        } catch (Throwable t) {
            if (mode != 0) {
                try {
                    Object fallback = mInjectInputEventMethod.invoke(mInputManager, event, 0);
                    return fallback instanceof Boolean && (Boolean) fallback;
                } catch (Throwable ignored) {
                }
            }
            Log.w(TAG, "injectInputObject failed: " + t.getMessage());
            return false;
        }
    }

    private MotionEvent obtainTouchEvent(int action, long downTime, long eventTime, float x, float y) {
        synchronized (mMotionLock) {
            if (mTouchProps[0] == null) {
                mTouchProps[0] = new MotionEvent.PointerProperties();
                mTouchProps[0].id = 0;
                mTouchProps[0].toolType = MotionEvent.TOOL_TYPE_FINGER;
            }
            if (mTouchCoords[0] == null) {
                mTouchCoords[0] = new MotionEvent.PointerCoords();
            }
            mTouchCoords[0].x = x;
            mTouchCoords[0].y = y;
            mTouchCoords[0].pressure = (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) ? 0f : 1f;
            mTouchCoords[0].size = 1f;

            return MotionEvent.obtain(
                    downTime,
                    eventTime,
                    action,
                    1,
                    mTouchProps,
                    mTouchCoords,
                    0,
                    0,
                    1.0f,
                    1.0f,
                    0,
                    0,
                    InputDevice.SOURCE_TOUCHSCREEN,
                    0);
        }
    }

    private void injectMotionEvent(int displayId, int action, long downTime, long eventTime, float x, float y) {
        ensureInputManager();
        if (action == MotionEvent.ACTION_DOWN) {
            mLastTouchedDisplayId = displayId;
            mLastVirtualInputMs = SystemClock.uptimeMillis();
        } else if (action == MotionEvent.ACTION_UP) {
            mLastTouchedDisplayId = displayId;
            mLastVirtualInputMs = SystemClock.uptimeMillis();
            scheduleRedirectCheck();
        }
        MotionEvent ev = obtainTouchEvent(action, downTime, eventTime, x, y);
        try {
            boolean ok = injectInputObject(ev, displayId, 0);
            if (!ok && action == MotionEvent.ACTION_UP) {
                mInputHandler.post(() -> execCommand(new String[]{
                        "input", "-d", String.valueOf(displayId), "tap",
                        String.valueOf(Math.round(x)), String.valueOf(Math.round(y))
                }));
            }
        } finally {
            ev.recycle();
        }
    }

    private void injectTap(int displayId, float x, float y) {
        ensureInputManager();
        mInputHandler.post(() -> {
            if (displayId != mLastTouchedDisplayId || mDisplaysNeedingTouchMode.remove(displayId)) {
                ensureDisplayTouchMode(displayId);
            }
            mLastTouchedDisplayId = displayId;
            mLastVirtualInputMs = SystemClock.uptimeMillis();

            long downTime = SystemClock.uptimeMillis();
            MotionEvent down = obtainTouchEvent(MotionEvent.ACTION_DOWN, downTime, downTime, x, y);
            boolean downOk;
            try {
                downOk = injectInputObject(down, displayId, INJECT_INPUT_EVENT_MODE_WAIT_FOR_FINISH);
            } finally {
                down.recycle();
            }

            SystemClock.sleep(25);

            MotionEvent up = obtainTouchEvent(MotionEvent.ACTION_UP, downTime, SystemClock.uptimeMillis(), x, y);
            boolean upOk;
            try {
                upOk = injectInputObject(up, displayId, INJECT_INPUT_EVENT_MODE_WAIT_FOR_FINISH);
            } finally {
                up.recycle();
            }

            if (!downOk || !upOk) {
                execCommand(new String[]{
                        "input", "-d", String.valueOf(displayId), "tap",
                        String.valueOf(Math.round(x)), String.valueOf(Math.round(y))
                });
            }
            scheduleRedirectCheck();
        });
    }

    private void scheduleRedirectCheck() {
        if (mDisplays.isEmpty() || mVrPackage.isEmpty()) {
            return;
        }
        mInputHandler.removeCallbacks(mRedirectCheckRunnable);
        mInputHandler.postDelayed(mRedirectCheckRunnable, 350L);
        mInputHandler.postDelayed(mRedirectCheckRunnable, 800L);
    }

    @SuppressLint("PrivateApi")
    private void ensureTaskManager() {
        if (mTaskManagerInitAttempted) {
            return;
        }
        mTaskManagerInitAttempted = true;
        try {
            Class<?> smClass = Class.forName("android.os.ServiceManager");
            IBinder binder = (IBinder) smClass.getMethod("getService", String.class).invoke(null, "activity_task");
            if (binder != null) {
                Class<?> stubClass = Class.forName("android.app.IActivityTaskManager$Stub");
                Object atm = stubClass.getMethod("asInterface", IBinder.class).invoke(null, binder);
                mActivityTaskManager = atm;
                if (atm != null) {
                    for (Method m : atm.getClass().getMethods()) {
                        String name = m.getName();
                        if ("getTasks".equals(name)) {
                            int pc = m.getParameterTypes().length;
                            if (mGetTasksMethod == null || pc > mGetTasksMethod.getParameterTypes().length) {
                                mGetTasksMethod = m;
                            }
                        } else if ("moveRootTaskToDisplay".equals(name) && m.getParameterTypes().length == 2) {
                            mMoveRootTaskMethod = m;
                        }
                    }
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "IActivityTaskManager init warning: " + t.getMessage());
        }
    }

    private void containDisplay0RedirectIfNeeded(int targetDisplayId) {
        if (targetDisplayId <= 0 || !mDisplays.containsKey(targetDisplayId)) {
            return;
        }
        String vrPkg = mVrPackage;
        if (vrPkg == null || vrPkg.isEmpty()) {
            return;
        }
        long ageMs = SystemClock.uptimeMillis() - mLastVirtualInputMs;
        if (ageMs < 0L || ageMs > 4500L) {
            return;
        }
        if (mRedirectCheckInFlight) {
            return;
        }
        mRedirectCheckInFlight = true;
        try {
            ensureTaskManager();
            int topTaskId = -1;
            String topPkg = null;

            Object atm = mActivityTaskManager;
            Method getTasks = mGetTasksMethod;
            if (atm != null && getTasks != null) {
                try {
                    Class<?>[] pts = getTasks.getParameterTypes();
                    Object rawList;
                    if (pts.length == 4) {
                        rawList = getTasks.invoke(atm, 10, false, false, 0);
                    } else if (pts.length == 3) {
                        rawList = getTasks.invoke(atm, 10, false, false);
                    } else if (pts.length == 1) {
                        rawList = getTasks.invoke(atm, 10);
                    } else {
                        rawList = null;
                    }
                    if (rawList instanceof List<?>) {
                        for (Object item : (List<?>) rawList) {
                            if (!(item instanceof ActivityManager.RunningTaskInfo)) {
                                continue;
                            }
                            ActivityManager.RunningTaskInfo rti = (ActivityManager.RunningTaskInfo) item;
                            if (rti.topActivity == null) {
                                continue;
                            }
                            int dispId = readTaskDisplayId(rti);
                            if (dispId == 0 || dispId == -1) {
                                topTaskId = rti.taskId;
                                topPkg = rti.topActivity.getPackageName();
                                break;
                            }
                        }
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "getTasks reflection warning: " + t.getMessage());
                }
            }

            if (topTaskId < 0 || topPkg == null || topPkg.isEmpty()) {
                return;
            }
            if (isSystemOrEscapePackage(topPkg, vrPkg)) {
                return;
            }

            boolean moved = false;
            if (atm != null && mMoveRootTaskMethod != null) {
                try {
                    mMoveRootTaskMethod.invoke(atm, topTaskId, targetDisplayId);
                    moved = true;
                } catch (Throwable t) {
                    Log.w(TAG, "moveRootTaskToDisplay failed: " + t.getMessage());
                }
            }
            if (!moved) {
                ExecResult moveRes = execCommand(new String[]{
                        "am", "display", "move-stack", String.valueOf(topTaskId), String.valueOf(targetDisplayId)
                });
                moved = moveRes.exitCode == 0;
            }
            if (!moved) {
                return;
            }

            String vrComp = mVrComponent;
            String[] restoreCmd = (vrComp != null && !vrComp.isEmpty())
                    ? new String[]{"am", "start", "--display", "0", "-f", "0x10200000", "-n", vrComp}
                    : new String[]{"am", "start", "--display", "0", "-f", "0x10200000",
                            "-a", "android.intent.action.MAIN", "-c", "android.intent.category.LAUNCHER", vrPkg};
            execCommand(restoreCmd);
            suppressImeOnDisplay(targetDisplayId);
            mDisplaysNeedingTouchMode.add(targetDisplayId);
            Log.i(TAG, "Contained external redirect pkg=" + topPkg + " task=" + topTaskId + " -> display=" + targetDisplayId);
        } finally {
            mRedirectCheckInFlight = false;
        }
    }

    private static int readTaskDisplayId(ActivityManager.RunningTaskInfo rti) {
        try {
            Field f = rti.getClass().getField("displayId");
            return f.getInt(rti);
        } catch (Throwable ignored) {
            return -1;
        }
    }

    private String resolveDefaultHomePackage() {
        String cached = mHomePackage;
        if (cached != null && !cached.isEmpty()) {
            return cached;
        }
        Context ctx = mRawContext != null ? mRawContext : mShellContext;
        if (ctx != null) {
            try {
                Intent home = new Intent(Intent.ACTION_MAIN);
                home.addCategory(Intent.CATEGORY_HOME);
                ResolveInfo ri = ctx.getPackageManager().resolveActivity(home, PackageManager.MATCH_DEFAULT_ONLY);
                if (ri != null && ri.activityInfo != null && ri.activityInfo.packageName != null) {
                    mHomePackage = ri.activityInfo.packageName;
                    return mHomePackage;
                }
            } catch (Throwable ignored) {
            }
        }
        return "";
    }

    private boolean isSystemOrEscapePackage(String pkg, String vrPackage) {
        if (pkg == null || pkg.isEmpty() || pkg.equals(vrPackage)) {
            return true;
        }
        if ("android".equals(pkg)
                || "com.android.systemui".equals(pkg)
                || "moe.shizuku.privileged.api".equals(pkg)
                || "com.android.settings".equals(pkg)) {
            return true;
        }
        String homePkg = resolveDefaultHomePackage();
        if (!homePkg.isEmpty() && pkg.equals(homePkg)) {
            return true;
        }
        String lower = pkg.toLowerCase();
        return lower.contains("launcher")
                || lower.contains("trebuchet")
                || lower.contains("permissioncontroller")
                || lower.contains("packageinstaller")
                || lower.contains("chooser")
                || lower.contains("resolver");
    }

    private void injectSmoothSwipe(int displayId, float x1, float y1, float x2, float y2, int durationMs) {
        ensureInputManager();
        mInputHandler.post(() -> {
            int steps = 7;
            int totalMs = Math.max(80, Math.min(450, durationMs));
            int stepDelay = Math.max(8, totalMs / steps);
            long downTime = SystemClock.uptimeMillis();
            injectMotionEvent(displayId, MotionEvent.ACTION_DOWN, downTime, downTime, x1, y1);
            for (int i = 1; i < steps; i++) {
                SystemClock.sleep(stepDelay);
                float t = (float) i / steps;
                float cx = x1 + (x2 - x1) * t;
                float cy = y1 + (y2 - y1) * t;
                injectMotionEvent(displayId, MotionEvent.ACTION_MOVE, downTime, SystemClock.uptimeMillis(), cx, cy);
            }
            SystemClock.sleep(stepDelay);
            injectMotionEvent(displayId, MotionEvent.ACTION_UP, downTime, SystemClock.uptimeMillis(), x2, y2);
        });
    }

    private void injectKeyCode(int displayId, int keyCode) {
        ensureInputManager();
        mDisplaysNeedingTouchMode.add(displayId);
        long now = SystemClock.uptimeMillis();
        KeyEvent down = new KeyEvent(now, now, KeyEvent.ACTION_DOWN, keyCode, 0, 0,
                KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD);
        KeyEvent up = new KeyEvent(now, now + 20, KeyEvent.ACTION_UP, keyCode, 0, 0,
                KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD);
        boolean okDown = injectInputObject(down, displayId);
        boolean okUp = injectInputObject(up, displayId);
        if (!okDown || !okUp) {
            mInputHandler.post(() -> execCommand(new String[]{
                    "input", "-d", String.valueOf(displayId), "keyevent", String.valueOf(keyCode)
            }));
        }
    }

    private void injectText(int displayId, String text) {
        if (text == null || text.isEmpty()) {
            return;
        }
        ensureInputManager();
        mDisplaysNeedingTouchMode.add(displayId);
        KeyEvent[] events = mKeyCharMap != null ? mKeyCharMap.getEvents(text.toCharArray()) : null;
        if (events != null && events.length > 0 && mInputManager != null) {
            boolean allOk = true;
            long now = SystemClock.uptimeMillis();
            for (int i = 0; i < events.length; i++) {
                KeyEvent src = events[i];
                KeyEvent copy = new KeyEvent(
                        now + i * 2L,
                        now + i * 2L,
                        src.getAction(),
                        src.getKeyCode(),
                        src.getRepeatCount(),
                        src.getMetaState(),
                        KeyCharacterMap.VIRTUAL_KEYBOARD,
                        0,
                        0,
                        InputDevice.SOURCE_KEYBOARD);
                if (!injectInputObject(copy, displayId)) {
                    allOk = false;
                    break;
                }
            }
            if (allOk) {
                return;
            }
        }
        mInputHandler.post(() -> execCommand(new String[]{
                "input", "-d", String.valueOf(displayId), "text", text.replace(" ", "%s")
        }));
    }

    private static final class ExecResult {
        final int exitCode;
        final String output;

        ExecResult(int code, String out) {
            exitCode = code;
            output = out;
        }
    }

    private static ExecResult execCommand(String[] cmd) {
        java.lang.Process process = null;
        try {
            process = new ProcessBuilder(cmd).redirectErrorStream(true).start();
            ByteArrayOutputStream baos = new ByteArrayOutputStream();
            InputStream is = process.getInputStream();
            byte[] buf = new byte[1024];
            int n;
            while ((n = is.read(buf)) != -1) {
                baos.write(buf, 0, n);
            }
            boolean finished = process.waitFor(3, TimeUnit.SECONDS);
            int code = finished ? process.exitValue() : -1;
            return new ExecResult(code, baos.toString("UTF-8").trim());
        } catch (Throwable t) {
            return new ExecResult(-1, t.getMessage() != null ? t.getMessage() : t.toString());
        } finally {
            if (process != null) {
                try {
                    process.destroy();
                } catch (Throwable ignored) {
                }
            }
        }
    }

    private boolean grantNotificationListener(String component) {
        if (component == null || component.isEmpty()) {
            return false;
        }
        ExecResult res = execCommand(new String[]{"cmd", "notification", "allow_listener", component});
        if (res.exitCode == 0) {
            Log.i(TAG, "Granted notification listener via cmd: " + component);
            return true;
        }
        try {
            Context ctx = mShellContext != null ? mShellContext : mRawContext;
            if (ctx != null) {
                String current = Settings.Secure.getString(ctx.getContentResolver(), "enabled_notification_listeners");
                if (current != null && current.contains(component)) {
                    return true;
                }
                String updated = (current == null || current.isEmpty()) ? component : (current + ":" + component);
                boolean putOk = Settings.Secure.putString(ctx.getContentResolver(), "enabled_notification_listeners", updated);
                Log.i(TAG, "Updated enabled_notification_listeners via Settings.Secure: " + putOk);
                return putOk;
            }
        } catch (Throwable t) {
            Log.w(TAG, "grantNotificationListener fallback failed: " + t.getMessage());
        }
        return false;
    }

    private int setTorchPrivileged(boolean on) {
        if (Process.myUid() == 0 && trySetSysfsTorch(on)) {
            return 1;
        }
        Context ctx = mShellContext != null ? mShellContext : mRawContext;
        if (ctx != null) {
            try {
                CameraManager cm = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
                if (cm != null) {
                    for (String id : cm.getCameraIdList()) {
                        CameraCharacteristics chars = cm.getCameraCharacteristics(id);
                        Integer facing = chars.get(CameraCharacteristics.LENS_FACING);
                        Boolean hasFlash = chars.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
                        if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK
                                && hasFlash != null && hasFlash) {
                            cm.setTorchMode(id, on);
                            Log.i(TAG, "Shizuku CameraManager torch set to " + on + " on camera " + id);
                            return 1;
                        }
                    }
                }
            } catch (CameraAccessException cae) {
                int reason = cae.getReason();
                if (reason == CameraAccessException.CAMERA_IN_USE
                        || reason == CameraAccessException.MAX_CAMERAS_IN_USE) {
                    return -1;
                }
            } catch (Throwable t) {
                Log.w(TAG, "Shizuku setTorchMode failed: " + t.getMessage());
            }
        }
        return 0;
    }

    private static boolean trySetSysfsTorch(boolean on) {
        try {
            File ledsDir = new File("/sys/class/leds");
            File[] dirs = ledsDir.listFiles();
            if (dirs == null) {
                return false;
            }
            boolean touched = false;
            for (File dir : dirs) {
                String n = dir.getName().toLowerCase();
                if (!n.contains("torch") && !n.contains("flash")) {
                    continue;
                }
                File brightness = new File(dir, "brightness");
                if (!brightness.exists() || !brightness.canWrite()) {
                    continue;
                }
                String value = "0";
                if (on) {
                    int max = readIntFile(new File(dir, "max_brightness"), 1);
                    value = String.valueOf(Math.max(1, max));
                }
                try (FileOutputStream fos = new FileOutputStream(brightness)) {
                    fos.write(value.getBytes());
                }
                touched = true;
            }
            if (touched) {
                Log.i(TAG, "Sysfs torch set to " + on);
            }
            return touched;
        } catch (Throwable ignored) {
            return false;
        }
    }

    private static int readIntFile(File f, int fallback) {
        if (f == null || !f.exists()) {
            return fallback;
        }
        try (java.io.FileInputStream fis = new java.io.FileInputStream(f)) {
            byte[] buf = new byte[32];
            int n = fis.read(buf);
            if (n > 0) {
                return Integer.parseInt(new String(buf, 0, n).trim());
            }
        } catch (Throwable ignored) {
        }
        return fallback;
    }
}
