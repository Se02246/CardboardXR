package com.brenpoly.cardboardxr.system;

import android.app.Activity;
import android.app.Application;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.media.AudioManager;
import android.media.MediaMetadata;
import android.media.session.MediaController;
import android.media.session.MediaSessionManager;
import android.media.session.PlaybackState;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.provider.Settings;
import android.text.TextUtils;
import android.util.Log;
import android.view.KeyEvent;

import com.brenpoly.cardboardxr.phoneapps.ShizukuSessionManager;

import java.io.ByteArrayOutputStream;
import java.util.ArrayList;
import java.util.List;

// Single Android bridge exposed to CardboardXR's SystemBridge.cs for:
//   - Shared Shizuku status, permission prompts, and auto-granting notification access
//   - Active media session metadata, album cover art PNG, timeline seek, and playback controls
//   - Live notification banner triggers, history snapshots, and circular package icons
public final class CardboardSystemBridge {
    private static final String TAG = "CardboardXRSys";
    private static final int MAX_ART_DIM = 384;

    private final Activity mActivity;
    private final Context mAppContext;
    private final ShizukuSessionManager mShizuku;
    private final MediaSessionManager mMediaSessionManager;
    private final AudioManager mAudioManager;
    private final ComponentName mListenerComponent;

    private List<CardboardNotificationListener.NotificationItem> mCachedNotifs = new ArrayList<>();
    private int mLastSyncedNotifVersion = -1;

    private MediaController mActiveController;
    private boolean mMediaHasSession;
    private boolean mMediaPlaying;
    private String mMediaPackage = "";
    private String mMediaTitle = "";
    private String mMediaArtist = "";
    private String mMediaAlbum = "";
    private long mMediaDurationMs;
    private long mMediaPositionMs;
    private String mLastArtSignature = "";
    private byte[] mMediaArtPng;
    private int mMediaArtVersion = 1;
    private Application.ActivityLifecycleCallbacks mLifecycleCallbacks;

    public CardboardSystemBridge(Activity activity) {
        mActivity = activity;
        mAppContext = activity.getApplicationContext();
        mShizuku = ShizukuSessionManager.getInstance(activity);
        mMediaSessionManager = (MediaSessionManager) mAppContext.getSystemService(Context.MEDIA_SESSION_SERVICE);
        mAudioManager = (AudioManager) mAppContext.getSystemService(Context.AUDIO_SERVICE);
        mListenerComponent = new ComponentName(mAppContext.getPackageName(), CardboardNotificationListener.class.getName());

        CardboardNotificationListener.setSuppressHeadsUp(true);
        Application app = activity.getApplication();
        if (app != null) {
            mLifecycleCallbacks = new Application.ActivityLifecycleCallbacks() {
                @Override public void onActivityCreated(Activity a, Bundle savedInstanceState) {}
                @Override public void onActivityStarted(Activity a) {}
                @Override public void onActivityResumed(Activity a) {
                    if (a == mActivity) {
                        CardboardNotificationListener.setSuppressHeadsUp(true);
                    }
                }
                @Override public void onActivityPaused(Activity a) {
                    if (a == mActivity) {
                        CardboardNotificationListener.setSuppressHeadsUp(false);
                    }
                }
                @Override public void onActivityStopped(Activity a) {
                    if (a == mActivity) {
                        CardboardNotificationListener.setSuppressHeadsUp(false);
                    }
                }
                @Override public void onActivitySaveInstanceState(Activity a, Bundle outState) {}
                @Override public void onActivityDestroyed(Activity a) {
                    if (a == mActivity) {
                        CardboardNotificationListener.setSuppressHeadsUp(false);
                    }
                }
            };
            app.registerActivityLifecycleCallbacks(mLifecycleCallbacks);
        }
    }

    public void setSuppressHeadsUp(boolean suppress) {
        CardboardNotificationListener.setSuppressHeadsUp(suppress);
    }

    public void dispose() {
        CardboardNotificationListener.setSuppressHeadsUp(false);
        Application app = mActivity != null ? mActivity.getApplication() : null;
        if (app != null && mLifecycleCallbacks != null) {
            app.unregisterActivityLifecycleCallbacks(mLifecycleCallbacks);
            mLifecycleCallbacks = null;
        }
    }

    // --- Shizuku & Notification Access ---

    public int getShizukuStatusCode() {
        return mShizuku.getStatusCode();
    }

    public String getShizukuStatusMessage() {
        return mShizuku.getStatusMessage();
    }

    public int getShizukuStatusVersion() {
        return mShizuku.getStatusVersion();
    }

    public void refreshShizuku() {
        mShizuku.refreshStatus();
        if (mShizuku.isReady() && !isNotificationAccessEnabled()) {
            mShizuku.grantNotificationListener(mListenerComponent.flattenToString());
        }
    }

    public void requestShizukuPermission() {
        mShizuku.requestPermission(mActivity);
    }

    public void openShizukuApp() {
        mShizuku.openShizukuApp(mActivity);
    }

    public boolean grantNotificationAccessViaShizuku() {
        if (!mShizuku.isReady()) {
            return false;
        }
        return mShizuku.grantNotificationListener(mListenerComponent.flattenToString());
    }

    public boolean isNotificationAccessEnabled() {
        return CardboardNotificationListener.isAccessEnabled(mAppContext);
    }

    public void openNotificationAccessSettings() {
        try {
            Intent intent = new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            mActivity.startActivity(intent);
        } catch (Throwable t) {
            Log.w(TAG, "Unable to open notification listener settings: " + t.getMessage());
        }
    }

    // --- Notifications ---

    public int getNotificationHistoryVersion() {
        return CardboardNotificationListener.getHistoryVersion();
    }

    public int getLatestIncomingNotificationId() {
        return CardboardNotificationListener.getLatestIncomingId();
    }

    public int syncNotifications() {
        int ver = CardboardNotificationListener.getHistoryVersion();
        if (ver != mLastSyncedNotifVersion) {
            mLastSyncedNotifVersion = ver;
            mCachedNotifs = CardboardNotificationListener.snapshot();
        }
        return mCachedNotifs.size();
    }

    public int getNotifId(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).id : -1;
    }

    public String getNotifKey(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).key : "";
    }

    public String getNotifPackage(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).packageName : "";
    }

    public String getNotifAppName(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).appName : "";
    }

    public String getNotifTitle(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).title : "";
    }

    public String getNotifText(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).text : "";
    }

    public long getNotifPostTimeMs(int index) {
        return index >= 0 && index < mCachedNotifs.size() ? mCachedNotifs.get(index).postTimeMs : 0L;
    }

    public byte[] getAppIconPng(String pkg) {
        return CardboardNotificationListener.getIconPng(mAppContext, pkg);
    }

    public void dismissNotification(String key) {
        CardboardNotificationListener.dismissByKey(key);
    }

    public void clearNotifications() {
        CardboardNotificationListener.clearAll();
    }

    // --- Media Session Control ---

    public void refreshMediaState() {
        MediaController chosen = findBestController();
        if (chosen == null) {
            mActiveController = null;
            mMediaHasSession = false;
            mMediaPlaying = false;
            mMediaPackage = "";
            mMediaTitle = "";
            mMediaArtist = "";
            mMediaAlbum = "";
            mMediaDurationMs = 0L;
            mMediaPositionMs = 0L;
            if (mMediaArtPng != null) {
                mMediaArtPng = null;
                mLastArtSignature = "";
                mMediaArtVersion++;
            }
            return;
        }

        mActiveController = chosen;
        mMediaHasSession = true;
        mMediaPackage = chosen.getPackageName() != null ? chosen.getPackageName() : "";

        PlaybackState ps = chosen.getPlaybackState();
        if (ps != null) {
            int state = ps.getState();
            mMediaPlaying = state == PlaybackState.STATE_PLAYING
                    || state == PlaybackState.STATE_BUFFERING
                    || state == PlaybackState.STATE_FAST_FORWARDING
                    || state == PlaybackState.STATE_REWINDING;
            long pos = ps.getPosition();
            if (mMediaPlaying && pos >= 0 && ps.getLastPositionUpdateTime() > 0) {
                long delta = Math.max(0L, SystemClock.elapsedRealtime() - ps.getLastPositionUpdateTime());
                pos += Math.round(delta * Math.max(0f, ps.getPlaybackSpeed()));
            }
            mMediaPositionMs = Math.max(0L, pos);
        } else {
            mMediaPlaying = false;
            mMediaPositionMs = 0L;
        }

        MediaMetadata meta = chosen.getMetadata();
        if (meta != null) {
            String title = getMetaText(meta, MediaMetadata.METADATA_KEY_DISPLAY_TITLE);
            if (title.isEmpty()) {
                title = getMetaText(meta, MediaMetadata.METADATA_KEY_TITLE);
            }
            String artist = getMetaText(meta, MediaMetadata.METADATA_KEY_ARTIST);
            if (artist.isEmpty()) {
                artist = getMetaText(meta, MediaMetadata.METADATA_KEY_ALBUM_ARTIST);
            }
            String album = getMetaText(meta, MediaMetadata.METADATA_KEY_ALBUM);
            mMediaTitle = title;
            mMediaArtist = artist;
            mMediaAlbum = album;
            mMediaDurationMs = Math.max(0L, meta.getLong(MediaMetadata.METADATA_KEY_DURATION));
            if (mMediaDurationMs > 0 && mMediaPositionMs > mMediaDurationMs) {
                mMediaPositionMs = mMediaDurationMs;
            }

            String artSig = mMediaPackage + "|" + mMediaTitle + "|" + mMediaArtist + "|" + mMediaAlbum;
            if (!artSig.equals(mLastArtSignature)) {
                mLastArtSignature = artSig;
                Bitmap bmp = meta.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART);
                if (bmp == null) {
                    bmp = meta.getBitmap(MediaMetadata.METADATA_KEY_ART);
                }
                if (bmp == null) {
                    bmp = meta.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON);
                }
                mMediaArtPng = encodeArtPng(bmp);
                mMediaArtVersion++;
            }
        } else {
            mMediaTitle = "";
            mMediaArtist = "";
            mMediaAlbum = "";
            mMediaDurationMs = 0L;
        }
    }

    private MediaController findBestController() {
        if (mMediaSessionManager == null) {
            return null;
        }
        try {
            List<MediaController> list = mMediaSessionManager.getActiveSessions(mListenerComponent);
            if (list == null || list.isEmpty()) {
                return null;
            }
            MediaController fallback = null;
            for (MediaController mc : list) {
                if (mc == null) {
                    continue;
                }
                PlaybackState ps = mc.getPlaybackState();
                int st = ps != null ? ps.getState() : PlaybackState.STATE_NONE;
                if (st == PlaybackState.STATE_PLAYING) {
                    return mc;
                }
                if (fallback == null && st != PlaybackState.STATE_NONE && st != PlaybackState.STATE_STOPPED) {
                    fallback = mc;
                }
            }
            return fallback != null ? fallback : list.get(0);
        } catch (SecurityException se) {
            return null;
        } catch (Throwable t) {
            Log.w(TAG, "getActiveSessions failed: " + t.getMessage());
            return null;
        }
    }

    public boolean isMediaHasSession() { return mMediaHasSession; }
    public boolean isMediaPlaying() { return mMediaPlaying; }
    public String getMediaPackage() { return mMediaPackage; }
    public String getMediaTitle() { return mMediaTitle; }
    public String getMediaArtist() { return mMediaArtist; }
    public String getMediaAlbum() { return mMediaAlbum; }
    public long getMediaDurationMs() { return mMediaDurationMs; }
    public long getMediaPositionMs() { return mMediaPositionMs; }
    public int getMediaArtVersion() { return mMediaArtVersion; }
    public byte[] getMediaArtPng() { return mMediaArtPng; }

    public void mediaTogglePlayPause() {
        MediaController mc = mActiveController != null ? mActiveController : findBestController();
        if (mc != null) {
            try {
                PlaybackState ps = mc.getPlaybackState();
                boolean playing = ps != null && ps.getState() == PlaybackState.STATE_PLAYING;
                if (playing) {
                    mc.getTransportControls().pause();
                } else {
                    mc.getTransportControls().play();
                }
                return;
            } catch (Throwable ignored) {
            }
        }
        dispatchMediaKey(KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE);
    }

    public void mediaNext() {
        MediaController mc = mActiveController != null ? mActiveController : findBestController();
        if (mc != null) {
            try {
                mc.getTransportControls().skipToNext();
                return;
            } catch (Throwable ignored) {
            }
        }
        dispatchMediaKey(KeyEvent.KEYCODE_MEDIA_NEXT);
    }

    public void mediaPrevious() {
        MediaController mc = mActiveController != null ? mActiveController : findBestController();
        if (mc != null) {
            try {
                mc.getTransportControls().skipToPrevious();
                return;
            } catch (Throwable ignored) {
            }
        }
        dispatchMediaKey(KeyEvent.KEYCODE_MEDIA_PREVIOUS);
    }

    public void mediaSeekTo(long positionMs) {
        MediaController mc = mActiveController != null ? mActiveController : findBestController();
        if (mc != null) {
            try {
                mc.getTransportControls().seekTo(Math.max(0L, positionMs));
            } catch (Throwable ignored) {
            }
        }
    }

    private void dispatchMediaKey(int keyCode) {
        if (mAudioManager == null) {
            return;
        }
        try {
            long now = SystemClock.uptimeMillis();
            mAudioManager.dispatchMediaKeyEvent(new KeyEvent(now, now, KeyEvent.ACTION_DOWN, keyCode, 0));
            mAudioManager.dispatchMediaKeyEvent(new KeyEvent(now, now + 10, KeyEvent.ACTION_UP, keyCode, 0));
        } catch (Throwable t) {
            Log.w(TAG, "dispatchMediaKey failed: " + t.getMessage());
        }
    }

    private static String getMetaText(MediaMetadata meta, String key) {
        CharSequence cs = meta.getText(key);
        if (TextUtils.isEmpty(cs)) {
            String s = meta.getString(key);
            return s != null ? s.trim() : "";
        }
        return cs.toString().trim();
    }

    private static byte[] encodeArtPng(Bitmap src) {
        if (src == null || src.isRecycled() || src.getWidth() <= 0 || src.getHeight() <= 0) {
            return null;
        }
        Bitmap scaled = src;
        boolean ownsScaled = false;
        int w = src.getWidth();
        int h = src.getHeight();
        if (w > MAX_ART_DIM || h > MAX_ART_DIM) {
            float scale = (float) MAX_ART_DIM / Math.max(w, h);
            int nw = Math.max(1, Math.round(w * scale));
            int nh = Math.max(1, Math.round(h * scale));
            scaled = Bitmap.createScaledBitmap(src, nw, nh, true);
            ownsScaled = scaled != src;
        }
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        scaled.compress(Bitmap.CompressFormat.JPEG, 88, baos);
        if (ownsScaled) {
            scaled.recycle();
        }
        return baos.toByteArray();
    }
}
