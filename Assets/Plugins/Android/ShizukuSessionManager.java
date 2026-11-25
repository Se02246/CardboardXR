package com.brenpoly.cardboardxr.phoneapps;

import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.os.Binder;
import android.os.IBinder;
import android.os.Parcel;
import android.util.Log;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import rikka.shizuku.Shizuku;

// Process-wide singleton managing Shizuku status, UserService binding (ShizukuAppService),
// automatic NotificationListener permission grant, and shared access for PhoneAppHost,
// TorchController, and CardboardSystemBridge.
public final class ShizukuSessionManager {
    private static final String TAG = "CardboardXRShizukuMgr";
    public static final String SHIZUKU_PACKAGE = "moe.shizuku.privileged.api";
    private static final int SHIZUKU_PERM_REQ_CODE = 4201;

    public static final int STATUS_NOT_INSTALLED = 0;
    public static final int STATUS_NOT_RUNNING = 1;
    public static final int STATUS_PERMISSION_REQUIRED = 2;
    public static final int STATUS_CONNECTING = 3;
    public static final int STATUS_READY = 4;
    public static final int STATUS_ERROR = 5;

    private static volatile ShizukuSessionManager sInstance;

    private final Context mAppContext;
    private final Shizuku.UserServiceArgs mServiceArgs;
    private final IBinder mClientToken = new Binder();
    private final ExecutorService mBgExecutor = Executors.newSingleThreadExecutor();

    private volatile int mStatusCode = STATUS_NOT_RUNNING;
    private volatile String mStatusMessage = "";
    private volatile IBinder mRemoteService;
    private volatile boolean mBindingRequested;
    private volatile int mStatusVersion = 1;
    private volatile boolean mNotifGrantTriedForConnection;

    private final Shizuku.OnBinderReceivedListener mBinderReceivedListener = this::refreshStatus;
    private final Shizuku.OnBinderDeadListener mBinderDeadListener = () -> {
        mRemoteService = null;
        mBindingRequested = false;
        mNotifGrantTriedForConnection = false;
        refreshStatus();
    };
    private final Shizuku.OnRequestPermissionResultListener mPermissionListener = (requestCode, grantResult) -> {
        if (requestCode == SHIZUKU_PERM_REQ_CODE) {
            refreshStatus();
        }
    };

    private final ServiceConnection mServiceConnection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName name, IBinder binder) {
            mRemoteService = binder;
            if (binder != null && binder.isBinderAlive()) {
                if (pingAndRegister(binder)) {
                    setStatus(STATUS_READY, "Shizuku pronto");
                    Log.i(TAG, "ShizukuAppService connected and registered");
                    if (!mNotifGrantTriedForConnection) {
                        mNotifGrantTriedForConnection = true;
                        mBgExecutor.execute(() -> grantNotificationListener(
                                mAppContext.getPackageName() + "/com.brenpoly.cardboardxr.system.CardboardNotificationListener"));
                    }
                } else {
                    mRemoteService = null;
                    mBindingRequested = false;
                    setStatus(STATUS_ERROR, "Errore registrazione servizio Shizuku");
                }
            } else {
                mRemoteService = null;
                mBindingRequested = false;
                refreshStatus();
            }
        }

        @Override
        public void onServiceDisconnected(ComponentName name) {
            mRemoteService = null;
            mBindingRequested = false;
            mNotifGrantTriedForConnection = false;
            refreshStatus();
        }
    };

    private ShizukuSessionManager(Context context) {
        mAppContext = context.getApplicationContext();
        mServiceArgs = new Shizuku.UserServiceArgs(new ComponentName(mAppContext, ShizukuAppService.class))
                .daemon(false)
                .processNameSuffix("shizuku_apps")
                .debuggable(false)
                .version(2);

        try {
            Shizuku.addBinderReceivedListenerSticky(mBinderReceivedListener);
            Shizuku.addBinderDeadListener(mBinderDeadListener);
            Shizuku.addRequestPermissionResultListener(mPermissionListener);
        } catch (Throwable t) {
            setStatus(STATUS_ERROR, "Shizuku init failed: " + t.getMessage());
            Log.w(TAG, mStatusMessage, t);
        }

        refreshStatus();
    }

    public static ShizukuSessionManager getInstance(Context context) {
        ShizukuSessionManager inst = sInstance;
        if (inst != null) {
            return inst;
        }
        synchronized (ShizukuSessionManager.class) {
            if (sInstance == null) {
                sInstance = new ShizukuSessionManager(context);
            }
            return sInstance;
        }
    }

    public static ShizukuSessionManager getExisting() {
        return sInstance;
    }

    public static int setTorchViaShizukuIfReady(boolean on) {
        ShizukuSessionManager mgr = sInstance;
        if (mgr == null || !mgr.isReady()) {
            return 0;
        }
        return mgr.setTorchViaShizuku(on);
    }

    private void setStatus(int code, String msg) {
        if (mStatusCode != code || (msg != null && !msg.equals(mStatusMessage))) {
            mStatusCode = code;
            mStatusMessage = msg != null ? msg : "";
            mStatusVersion++;
        }
    }

    public int getStatusCode() {
        return mStatusCode;
    }

    public String getStatusMessage() {
        return mStatusMessage;
    }

    public int getStatusVersion() {
        return mStatusVersion;
    }

    public boolean isReady() {
        IBinder remote = mRemoteService;
        return mStatusCode == STATUS_READY && remote != null && remote.isBinderAlive();
    }

    public IBinder getRemoteService() {
        return mRemoteService;
    }

    public synchronized void refreshStatus() {
        if (mStatusCode == STATUS_READY) {
            IBinder remote = mRemoteService;
            if (remote != null && remote.isBinderAlive()) {
                return;
            }
        }
        try {
            if (!isPackageInstalled(SHIZUKU_PACKAGE)) {
                mRemoteService = null;
                mBindingRequested = false;
                mNotifGrantTriedForConnection = false;
                setStatus(STATUS_NOT_INSTALLED, "Installa l'app Shizuku sul telefono");
                return;
            }
            boolean binderAlive = false;
            try {
                binderAlive = Shizuku.pingBinder();
            } catch (Throwable ignored) {
            }
            if (!binderAlive) {
                mRemoteService = null;
                mBindingRequested = false;
                mNotifGrantTriedForConnection = false;
                setStatus(STATUS_NOT_RUNNING, "Avvia Shizuku sul telefono e torna qui");
                return;
            }
            boolean granted = false;
            try {
                granted = !Shizuku.isPreV11() && Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED;
            } catch (Throwable ignored) {
            }
            if (!granted) {
                mRemoteService = null;
                mBindingRequested = false;
                mNotifGrantTriedForConnection = false;
                setStatus(STATUS_PERMISSION_REQUIRED, "Concedi a CardboardXR il permesso Shizuku");
                return;
            }
            IBinder remote = mRemoteService;
            if (remote != null && remote.isBinderAlive()) {
                setStatus(STATUS_READY, "Shizuku pronto");
                return;
            }
            mRemoteService = null;
            setStatus(STATUS_CONNECTING, "Connessione al servizio Shizuku...");
            bindServiceIfNeeded();
        } catch (Throwable t) {
            setStatus(STATUS_ERROR, "Errore Shizuku: " + t.getMessage());
            Log.w(TAG, mStatusMessage, t);
        }
    }

    private void bindServiceIfNeeded() {
        if (mBindingRequested) {
            return;
        }
        mBindingRequested = true;
        try {
            Shizuku.bindUserService(mServiceArgs, mServiceConnection);
        } catch (Throwable t) {
            mBindingRequested = false;
            setStatus(STATUS_ERROR, "Errore avvio UserService: " + t.getMessage());
            Log.w(TAG, mStatusMessage, t);
        }
    }

    public void requestPermission(Activity activity) {
        try {
            if (!isPackageInstalled(SHIZUKU_PACKAGE)) {
                setStatus(STATUS_NOT_INSTALLED, "Installa l'app Shizuku");
                return;
            }
            if (!Shizuku.pingBinder()) {
                openShizukuApp(activity);
                return;
            }
            if (Shizuku.checkSelfPermission() != PackageManager.PERMISSION_GRANTED) {
                Shizuku.requestPermission(SHIZUKU_PERM_REQ_CODE);
            } else {
                refreshStatus();
            }
        } catch (Throwable t) {
            setStatus(STATUS_ERROR, "Errore richiesta permesso: " + t.getMessage());
        }
    }

    public void openShizukuApp(Activity activity) {
        try {
            Context ctx = activity != null ? activity : mAppContext;
            Intent intent = ctx.getPackageManager().getLaunchIntentForPackage(SHIZUKU_PACKAGE);
            if (intent != null) {
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                ctx.startActivity(intent);
            }
        } catch (Throwable t) {
            Log.w(TAG, "Unable to open Shizuku app: " + t.getMessage());
        }
    }

    public boolean grantNotificationListener(String componentName) {
        IBinder remote = mRemoteService;
        if (remote == null || !remote.isBinderAlive() || componentName == null || componentName.isEmpty()) {
            return false;
        }
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
            data.writeString(componentName);
            remote.transact(ShizukuAppService.TX_GRANT_NOTIFICATION_LISTENER, data, reply, 0);
            reply.readException();
            return reply.readInt() != 0;
        } catch (Throwable t) {
            Log.w(TAG, "grantNotificationListener transaction failed: " + t.getMessage());
            return false;
        } finally {
            data.recycle();
            reply.recycle();
        }
    }

    public int setTorchViaShizuku(boolean on) {
        IBinder remote = mRemoteService;
        if (remote == null || !remote.isBinderAlive()) {
            return 0;
        }
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
            data.writeInt(on ? 1 : 0);
            remote.transact(ShizukuAppService.TX_SET_TORCH, data, reply, 0);
            reply.readException();
            return reply.readInt();
        } catch (Throwable t) {
            Log.w(TAG, "setTorchViaShizuku transaction failed: " + t.getMessage());
            return 0;
        } finally {
            data.recycle();
            reply.recycle();
        }
    }

    private boolean isPackageInstalled(String pkg) {
        try {
            mAppContext.getPackageManager().getPackageInfo(pkg, 0);
            return true;
        } catch (Throwable ignored) {
            return false;
        }
    }

    private boolean pingAndRegister(IBinder remote) {
        Parcel data = Parcel.obtain();
        Parcel reply = Parcel.obtain();
        try {
            data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
            remote.transact(ShizukuAppService.TX_PING, data, reply, 0);
            reply.readException();
            int uid = reply.readInt();
            Log.i(TAG, "UserService ping ok (uid=" + uid + ")");
        } catch (Throwable t) {
            Log.w(TAG, "UserService ping failed: " + t.getMessage());
            return false;
        } finally {
            data.recycle();
            reply.recycle();
        }

        Parcel regData = Parcel.obtain();
        Parcel regReply = Parcel.obtain();
        try {
            regData.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
            regData.writeStrongBinder(mClientToken);
            String pkg = mAppContext.getPackageName();
            String comp = "";
            try {
                Intent launch = mAppContext.getPackageManager().getLaunchIntentForPackage(pkg);
                if (launch != null && launch.getComponent() != null) {
                    comp = launch.getComponent().flattenToString();
                }
            } catch (Throwable ignored) {
            }
            regData.writeString(pkg);
            regData.writeString(comp);
            remote.transact(ShizukuAppService.TX_REGISTER_CLIENT, regData, regReply, 0);
            regReply.readException();
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "UserService register token failed: " + t.getMessage());
            return false;
        } finally {
            regData.recycle();
            regReply.recycle();
        }
    }

    public void containRedirectToDisplay(int displayId, String vrPackage, String vrComponent) {
        IBinder remote = mRemoteService;
        if (displayId <= 0 || remote == null || !remote.isBinderAlive()) {
            return;
        }
        Parcel data = Parcel.obtain();
        try {
            data.writeInterfaceToken(ShizukuAppService.DESCRIPTOR);
            data.writeInt(displayId);
            data.writeString(vrPackage != null ? vrPackage : "");
            data.writeString(vrComponent != null ? vrComponent : "");
            remote.transact(ShizukuAppService.TX_CONTAIN_REDIRECT, data, null, IBinder.FLAG_ONEWAY);
        } catch (Throwable t) {
            Log.w(TAG, "containRedirectToDisplay failed: " + t.getMessage());
        } finally {
            data.recycle();
        }
    }
}
