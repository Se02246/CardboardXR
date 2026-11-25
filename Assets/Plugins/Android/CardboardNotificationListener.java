package com.brenpoly.cardboardxr.system;

import android.app.Notification;
import android.content.ComponentName;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Path;
import android.graphics.drawable.AdaptiveIconDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.Icon;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;
import android.text.TextUtils;
import android.util.Log;

import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

// Listens for live Android notifications on the host phone and maintains a bounded history
// alongside circular app icons for CardboardXR's head-up banner and Notification Center.
public final class CardboardNotificationListener extends NotificationListenerService {
    private static final String TAG = "CardboardXRNotif";
    private static final int MAX_HISTORY = 25;

    public static final class NotificationItem {
        public final int id;
        public final String key;
        public final String packageName;
        public final String appName;
        public final String title;
        public final String text;
        public final long postTimeMs;

        NotificationItem(int id, String key, String packageName, String appName, String title, String text, long postTimeMs) {
            this.id = id;
            this.key = key;
            this.packageName = packageName;
            this.appName = appName;
            this.title = title;
            this.text = text;
            this.postTimeMs = postTimeMs;
        }
    }

    private static volatile CardboardNotificationListener sInstance;
    private static volatile boolean sConnected;
    private static final Object sLock = new Object();
    private static final Object sFilterLock = new Object();
    private static final List<NotificationItem> sHistory = new ArrayList<>();
    private static final ConcurrentHashMap<String, byte[]> sIconCache = new ConcurrentHashMap<>();
    private static final AtomicInteger sNextId = new AtomicInteger(1);
    private static volatile int sHistoryVersion = 1;
    private static volatile int sLatestIncomingId = 0;
    private static volatile boolean sSuppressRequested;
    private static boolean sDidSuppress;
    private static int sSavedInterruptionFilter = INTERRUPTION_FILTER_ALL;

    @Override
    public void onListenerConnected() {
        super.onListenerConnected();
        sInstance = this;
        sConnected = true;
        Log.i(TAG, "NotificationListenerService connected");
        applySuppressionIfConnected();
        try {
            StatusBarNotification[] active = getActiveNotifications();
            if (active != null) {
                synchronized (sLock) {
                    sHistory.clear();
                    for (int i = active.length - 1; i >= 0; i--) {
                        NotificationItem item = buildItem(this, active[i]);
                        if (item != null) {
                            addLocked(item, false);
                        }
                    }
                    sHistoryVersion++;
                }
            }
        } catch (Throwable t) {
            Log.w(TAG, "Failed reading initial active notifications: " + t.getMessage());
        }
    }

    @Override
    public void onListenerDisconnected() {
        synchronized (sFilterLock) {
            if (sDidSuppress && sInstance == this) {
                try {
                    requestInterruptionFilter(sSavedInterruptionFilter);
                } catch (Throwable ignored) {
                }
                sDidSuppress = false;
            }
        }
        sConnected = false;
        if (sInstance == this) {
            sInstance = null;
        }
        Log.i(TAG, "NotificationListenerService disconnected");
        super.onListenerDisconnected();
    }

    public static void setSuppressHeadsUp(boolean suppress) {
        sSuppressRequested = suppress;
        applySuppressionIfConnected();
    }

    private static void applySuppressionIfConnected() {
        CardboardNotificationListener inst = sInstance;
        if (inst == null || !sConnected) {
            return;
        }
        synchronized (sFilterLock) {
            try {
                if (sSuppressRequested) {
                    if (!sDidSuppress) {
                        int current = inst.getCurrentInterruptionFilter();
                        if (current == INTERRUPTION_FILTER_ALL
                                || current == INTERRUPTION_FILTER_PRIORITY
                                || current == INTERRUPTION_FILTER_UNKNOWN) {
                            sSavedInterruptionFilter = current == INTERRUPTION_FILTER_UNKNOWN
                                    ? INTERRUPTION_FILTER_ALL
                                    : current;
                            sDidSuppress = true;
                            inst.requestInterruptionFilter(INTERRUPTION_FILTER_ALARMS);
                            Log.i(TAG, "Heads-up notifications suppressed while in VR (prev=" + sSavedInterruptionFilter + ")");
                        }
                    }
                } else if (sDidSuppress) {
                    sDidSuppress = false;
                    inst.requestInterruptionFilter(sSavedInterruptionFilter);
                    Log.i(TAG, "Restored Android notification filter=" + sSavedInterruptionFilter);
                }
            } catch (Throwable t) {
                Log.w(TAG, "applySuppressionIfConnected failed: " + t.getMessage());
            }
        }
    }

    @Override
    public void onNotificationPosted(StatusBarNotification sbn) {
        if (sbn == null) {
            return;
        }
        NotificationItem item = buildItem(this, sbn);
        if (item == null) {
            return;
        }
        synchronized (sLock) {
            removeByKeyLocked(item.key);
            addLocked(item, true);
            sHistoryVersion++;
            sLatestIncomingId = item.id;
        }
        cachePackageIcon(this, item.packageName, sbn);
    }

    @Override
    public void onNotificationRemoved(StatusBarNotification sbn) {
        if (sbn == null || sbn.getKey() == null) {
            return;
        }
        synchronized (sLock) {
            if (removeByKeyLocked(sbn.getKey())) {
                sHistoryVersion++;
            }
        }
    }

    private static void addLocked(NotificationItem item, boolean toFront) {
        if (toFront) {
            sHistory.add(0, item);
        } else {
            sHistory.add(item);
        }
        while (sHistory.size() > MAX_HISTORY) {
            sHistory.remove(sHistory.size() - 1);
        }
    }

    private static boolean removeByKeyLocked(String key) {
        if (key == null) {
            return false;
        }
        for (int i = sHistory.size() - 1; i >= 0; i--) {
            if (key.equals(sHistory.get(i).key)) {
                sHistory.remove(i);
                return true;
            }
        }
        return false;
    }

    private static NotificationItem buildItem(Context ctx, StatusBarNotification sbn) {
        if (sbn == null) {
            return null;
        }
        String pkg = sbn.getPackageName();
        Notification n = sbn.getNotification();
        if (pkg == null || n == null || pkg.equals(ctx.getPackageName())) {
            return null;
        }
        if ((n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) {
            return null;
        }
        Bundle extras = n.extras;
        if (Notification.CATEGORY_TRANSPORT.equals(n.category)
                || (extras != null && extras.containsKey(Notification.EXTRA_MEDIA_SESSION))) {
            return null;
        }
        boolean isSystemPkg = "android".equals(pkg) || "com.android.systemui".equals(pkg);
        if (sbn.isOngoing() && (isSystemPkg || (n.flags & Notification.FLAG_FOREGROUND_SERVICE) != 0)) {
            return null;
        }

        String title = "";
        String body = "";
        if (extras != null) {
            CharSequence t = extras.getCharSequence(Notification.EXTRA_TITLE);
            if (TextUtils.isEmpty(t)) {
                t = extras.getCharSequence(Notification.EXTRA_TITLE_BIG);
            }
            CharSequence b = extras.getCharSequence(Notification.EXTRA_BIG_TEXT);
            if (TextUtils.isEmpty(b)) {
                b = extras.getCharSequence(Notification.EXTRA_TEXT);
            }
            if (TextUtils.isEmpty(b)) {
                b = extras.getCharSequence(Notification.EXTRA_SUB_TEXT);
            }
            if (!TextUtils.isEmpty(t)) {
                title = t.toString().trim();
            }
            if (!TextUtils.isEmpty(b)) {
                body = b.toString().trim();
            }
        }
        if (title.isEmpty() && n.tickerText != null) {
            title = n.tickerText.toString().trim();
        }
        if (title.isEmpty() && body.isEmpty()) {
            return null;
        }

        String appName = resolveAppName(ctx, pkg);
        if (title.isEmpty()) {
            title = appName;
        }
        cachePackageIcon(ctx, pkg, sbn);
        return new NotificationItem(
                sNextId.getAndIncrement(),
                sbn.getKey() != null ? sbn.getKey() : (pkg + "#" + sbn.getId()),
                pkg,
                appName,
                title,
                body,
                sbn.getPostTime());
    }

    private static String resolveAppName(Context ctx, String pkg) {
        try {
            PackageManager pm = ctx.getPackageManager();
            ApplicationInfo ai = pm.getApplicationInfo(pkg, 0);
            CharSequence lbl = pm.getApplicationLabel(ai);
            if (!TextUtils.isEmpty(lbl)) {
                return lbl.toString().trim();
            }
        } catch (Throwable ignored) {
        }
        return pkg;
    }

    static byte[] cachePackageIcon(Context ctx, String pkg, StatusBarNotification sbn) {
        if (pkg == null || pkg.isEmpty()) {
            return null;
        }
        byte[] existing = sIconCache.get(pkg);
        if (existing != null) {
            return existing;
        }
        Drawable drawable = null;
        try {
            PackageManager pm = ctx.getPackageManager();
            drawable = pm.getApplicationIcon(pkg);
        } catch (Throwable ignored) {
        }
        if (drawable == null && sbn != null && sbn.getNotification() != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            try {
                Icon small = sbn.getNotification().getSmallIcon();
                if (small != null) {
                    drawable = small.loadDrawable(ctx);
                }
            } catch (Throwable ignored) {
            }
        }
        byte[] png = renderCircularIconPng(drawable);
        if (png != null) {
            sIconCache.put(pkg, png);
        }
        return png;
    }

    static byte[] renderCircularIconPng(Drawable icon) {
        if (icon == null) {
            return null;
        }
        final int size = 96;
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
        bmp.compress(Bitmap.CompressFormat.PNG, 92, baos);
        bmp.recycle();
        return baos.toByteArray();
    }

    public static boolean isConnected() {
        return sConnected;
    }

    public static boolean isAccessEnabled(Context context) {
        if (sConnected) {
            return true;
        }
        if (context == null) {
            return false;
        }
        try {
            String flat = Settings.Secure.getString(context.getContentResolver(), "enabled_notification_listeners");
            if (TextUtils.isEmpty(flat)) {
                return false;
            }
            String full = new ComponentName(context.getPackageName(), CardboardNotificationListener.class.getName()).flattenToString();
            String shortName = new ComponentName(context.getPackageName(), CardboardNotificationListener.class.getName()).flattenToShortString();
            return flat.contains(full) || flat.contains(shortName);
        } catch (Throwable ignored) {
            return false;
        }
    }

    public static int getHistoryVersion() {
        return sHistoryVersion;
    }

    public static int getLatestIncomingId() {
        return sLatestIncomingId;
    }

    public static List<NotificationItem> snapshot() {
        synchronized (sLock) {
            return new ArrayList<>(sHistory);
        }
    }

    public static byte[] getIconPng(Context ctx, String pkg) {
        if (pkg == null || pkg.isEmpty()) {
            return null;
        }
        byte[] cached = sIconCache.get(pkg);
        if (cached != null) {
            return cached;
        }
        return cachePackageIcon(ctx, pkg, null);
    }

    public static void dismissByKey(String key) {
        if (key == null || key.isEmpty()) {
            return;
        }
        synchronized (sLock) {
            if (removeByKeyLocked(key)) {
                sHistoryVersion++;
            }
        }
        CardboardNotificationListener inst = sInstance;
        if (inst != null && sConnected) {
            try {
                inst.cancelNotification(key);
            } catch (Throwable ignored) {
            }
        }
    }

    public static void clearAll() {
        synchronized (sLock) {
            sHistory.clear();
            sHistoryVersion++;
        }
    }
}
