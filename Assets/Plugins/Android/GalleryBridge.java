package com.brenpoly.cardboardxr.gallery;

import android.app.Activity;
import android.content.ContentResolver;
import android.content.ContentUris;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.media.ExifInterface;
import android.net.Uri;
import android.os.Build;
import android.provider.MediaStore;
import android.util.Log;
import android.util.Size;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;

// Native Android photo gallery bridge for CardboardXR's Quadri app:
//   - Requests runtime gallery permission cleanly (READ_MEDIA_IMAGES / READ_EXTERNAL_STORAGE)
//   - Queries MediaStore images asynchronously (newest first)
//   - Generates upright cached JPEG thumbnails and high-res frames on background executors
public final class GalleryBridge {
    private static final String TAG = "CardboardXRGallery";
    private static final String PERM_READ_MEDIA_IMAGES = "android.permission.READ_MEDIA_IMAGES";
    private static final String PERM_READ_MEDIA_VISUAL_USER_SELECTED = "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
    private static final String PERM_READ_EXTERNAL_STORAGE = "android.permission.READ_EXTERNAL_STORAGE";
    private static final int MAX_QUERY_PHOTOS = 240;
    private static final int THUMB_SIZE = 256;

    private static final class PhotoRecord {
        final long id;
        final String name;
        final int width;
        final int height;
        final int orientationDeg;

        PhotoRecord(long id, String name, int width, int height, int orientationDeg) {
            this.id = id;
            this.name = name;
            this.width = width;
            this.height = height;
            this.orientationDeg = orientationDeg;
        }
    }

    private final Activity mActivity;
    private final ContentResolver mResolver;
    private final File mThumbDir;
    private final File mFullDir;

    private final ExecutorService mListExecutor = Executors.newSingleThreadExecutor();
    private final ExecutorService mImageExecutor = Executors.newFixedThreadPool(2);

    private volatile List<PhotoRecord> mPhotos = Collections.emptyList();
    private final AtomicInteger mListVersion = new AtomicInteger(0);
    private final AtomicBoolean mRefreshing = new AtomicBoolean(false);

    private final ConcurrentHashMap<Long, String> mThumbPaths = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<Long, Boolean> mThumbInFlight = new ConcurrentHashMap<>();
    private final AtomicInteger mThumbVersion = new AtomicInteger(0);

    private final ConcurrentHashMap<Long, String> mFullPaths = new ConcurrentHashMap<>();
    private final ConcurrentHashMap<Long, Boolean> mFullInFlight = new ConcurrentHashMap<>();
    private final AtomicInteger mFullVersion = new AtomicInteger(0);

    private volatile boolean mShutdown;

    public GalleryBridge(Activity activity) {
        mActivity = activity;
        mResolver = activity.getApplicationContext().getContentResolver();
        File cache = activity.getApplicationContext().getCacheDir();
        mThumbDir = new File(cache, "quadri_thumbs");
        mFullDir = new File(cache, "quadri_full");
        if (!mThumbDir.exists()) mThumbDir.mkdirs();
        if (!mFullDir.exists()) mFullDir.mkdirs();
    }

    public boolean hasPermission() {
        if (Build.VERSION.SDK_INT >= 34) {
            return mActivity.checkSelfPermission(PERM_READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED
                || mActivity.checkSelfPermission(PERM_READ_MEDIA_VISUAL_USER_SELECTED) == PackageManager.PERMISSION_GRANTED;
        }
        if (Build.VERSION.SDK_INT >= 33) {
            return mActivity.checkSelfPermission(PERM_READ_MEDIA_IMAGES) == PackageManager.PERMISSION_GRANTED;
        }
        return mActivity.checkSelfPermission(PERM_READ_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED;
    }

    public void requestPermission() {
        final String[] perms = Build.VERSION.SDK_INT >= 33
            ? new String[]{PERM_READ_MEDIA_IMAGES}
            : new String[]{PERM_READ_EXTERNAL_STORAGE};
        mActivity.runOnUiThread(() -> {
            try {
                mActivity.requestPermissions(perms, 4102);
            } catch (Throwable t) {
                Log.w(TAG, "requestPermissions failed", t);
            }
        });
    }

    public int getListVersion() {
        return mListVersion.get();
    }

    public boolean isRefreshing() {
        return mRefreshing.get();
    }

    public int getPhotoCount() {
        return mPhotos.size();
    }

    public long getPhotoId(int index) {
        List<PhotoRecord> list = mPhotos;
        return index >= 0 && index < list.size() ? list.get(index).id : -1L;
    }

    public String getPhotoName(int index) {
        List<PhotoRecord> list = mPhotos;
        return index >= 0 && index < list.size() ? list.get(index).name : "";
    }

    public int getPhotoWidth(int index) {
        List<PhotoRecord> list = mPhotos;
        return index >= 0 && index < list.size() ? list.get(index).width : 0;
    }

    public int getPhotoHeight(int index) {
        List<PhotoRecord> list = mPhotos;
        return index >= 0 && index < list.size() ? list.get(index).height : 0;
    }

    public int getThumbVersion() {
        return mThumbVersion.get();
    }

    public String getThumbnailPath(long id) {
        String p = mThumbPaths.get(id);
        return p != null ? p : "";
    }

    public int getFullVersion() {
        return mFullVersion.get();
    }

    public String getFullImagePath(long id) {
        String p = mFullPaths.get(id);
        return p != null ? p : "";
    }

    public void refreshAsync() {
        if (mShutdown || !hasPermission()) {
            return;
        }
        if (!mRefreshing.compareAndSet(false, true)) {
            return;
        }
        mListExecutor.execute(() -> {
            List<PhotoRecord> found = new ArrayList<>();
            Cursor cursor = null;
            try {
                String[] projection = new String[]{
                    MediaStore.Images.Media._ID,
                    MediaStore.Images.Media.DISPLAY_NAME,
                    MediaStore.Images.Media.WIDTH,
                    MediaStore.Images.Media.HEIGHT,
                    MediaStore.Images.Media.ORIENTATION
                };
                cursor = mResolver.query(
                    MediaStore.Images.Media.EXTERNAL_CONTENT_URI,
                    projection,
                    null,
                    null,
                    MediaStore.Images.Media.DATE_MODIFIED + " DESC"
                );
                if (cursor != null) {
                    int idCol = cursor.getColumnIndexOrThrow(MediaStore.Images.Media._ID);
                    int nameCol = cursor.getColumnIndex(MediaStore.Images.Media.DISPLAY_NAME);
                    int wCol = cursor.getColumnIndex(MediaStore.Images.Media.WIDTH);
                    int hCol = cursor.getColumnIndex(MediaStore.Images.Media.HEIGHT);
                    int oCol = cursor.getColumnIndex(MediaStore.Images.Media.ORIENTATION);
                    while (cursor.moveToNext() && found.size() < MAX_QUERY_PHOTOS) {
                        long id = cursor.getLong(idCol);
                        String name = nameCol >= 0 ? cursor.getString(nameCol) : ("Foto " + id);
                        int w = wCol >= 0 ? cursor.getInt(wCol) : 0;
                        int h = hCol >= 0 ? cursor.getInt(hCol) : 0;
                        int orient = oCol >= 0 ? cursor.getInt(oCol) : 0;
                        if (orient == 90 || orient == 270) {
                            int tmp = w;
                            w = h;
                            h = tmp;
                        }
                        found.add(new PhotoRecord(id, name != null ? name : "", w, h, orient));
                    }
                }
            } catch (Throwable t) {
                Log.w(TAG, "MediaStore query failed", t);
            } finally {
                if (cursor != null) {
                    try { cursor.close(); } catch (Throwable ignored) {}
                }
                mPhotos = Collections.unmodifiableList(found);
                mRefreshing.set(false);
                mListVersion.incrementAndGet();
            }
        });
    }

    public void requestThumbnails(int startIndex, int count) {
        if (mShutdown || !hasPermission()) {
            return;
        }
        List<PhotoRecord> list = mPhotos;
        int start = Math.max(0, startIndex);
        int end = Math.min(list.size(), start + Math.max(0, count));
        for (int i = start; i < end; i++) {
            final PhotoRecord rec = list.get(i);
            final long id = rec.id;
            File cached = new File(mThumbDir, "thumb_" + id + ".jpg");
            if (cached.exists() && cached.length() > 0) {
                if (!cached.getAbsolutePath().equals(mThumbPaths.get(id))) {
                    mThumbPaths.put(id, cached.getAbsolutePath());
                    mThumbVersion.incrementAndGet();
                }
                continue;
            }
            if (mThumbInFlight.putIfAbsent(id, Boolean.TRUE) != null) {
                continue;
            }
            mImageExecutor.execute(() -> {
                try {
                    Bitmap bmp = loadThumbnailBitmap(rec);
                    if (bmp != null) {
                        if (writeJpegAtomic(bmp, cached, 86)) {
                            mThumbPaths.put(id, cached.getAbsolutePath());
                            mThumbVersion.incrementAndGet();
                        }
                        bmp.recycle();
                    }
                } catch (Throwable t) {
                    Log.w(TAG, "Thumbnail load failed for " + id, t);
                } finally {
                    mThumbInFlight.remove(id);
                }
            });
        }
    }

    public void requestFullImage(long id, int maxDim) {
        if (mShutdown || !hasPermission() || id < 0) {
            return;
        }
        final int safeMaxDim = Math.max(512, Math.min(2048, maxDim));
        final File cached = new File(mFullDir, "photo_" + id + "_" + safeMaxDim + ".jpg");
        if (cached.exists() && cached.length() > 0) {
            mFullPaths.put(id, cached.getAbsolutePath());
            mFullVersion.incrementAndGet();
            return;
        }
        if (mFullInFlight.putIfAbsent(id, Boolean.TRUE) != null) {
            return;
        }
        final int recordedOrientation = findRecordedOrientation(id);
        mImageExecutor.execute(() -> {
            try {
                Uri uri = ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id);
                Bitmap bmp = decodeSampledBitmap(uri, safeMaxDim, recordedOrientation);
                if (bmp != null) {
                    if (writeJpegAtomic(bmp, cached, 92)) {
                        mFullPaths.put(id, cached.getAbsolutePath());
                        mFullVersion.incrementAndGet();
                    }
                    bmp.recycle();
                }
            } catch (Throwable t) {
                Log.w(TAG, "Full image decode failed for " + id, t);
            } finally {
                mFullInFlight.remove(id);
            }
        });
    }

    private int findRecordedOrientation(long id) {
        List<PhotoRecord> list = mPhotos;
        for (int i = 0; i < list.size(); i++) {
            PhotoRecord r = list.get(i);
            if (r.id == id) {
                return r.orientationDeg;
            }
        }
        return 0;
    }

    private Bitmap loadThumbnailBitmap(PhotoRecord rec) {
        Uri uri = ContentUris.withAppendedId(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, rec.id);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            try {
                Bitmap thumb = mResolver.loadThumbnail(uri, new Size(THUMB_SIZE, THUMB_SIZE), null);
                if (thumb != null) {
                    return thumb;
                }
            } catch (Throwable ignored) {}
        }
        return decodeSampledBitmap(uri, THUMB_SIZE, rec.orientationDeg);
    }

    private Bitmap decodeSampledBitmap(Uri uri, int maxDim, int fallbackOrientationDeg) {
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        try (InputStream is = mResolver.openInputStream(uri)) {
            if (is == null) return null;
            BitmapFactory.decodeStream(is, null, bounds);
        } catch (Throwable t) {
            return null;
        }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
            return null;
        }

        int inSampleSize = 1;
        int w = bounds.outWidth;
        int h = bounds.outHeight;
        while ((w / (inSampleSize * 2)) >= maxDim || (h / (inSampleSize * 2)) >= maxDim) {
            inSampleSize *= 2;
        }

        BitmapFactory.Options opts = new BitmapFactory.Options();
        opts.inSampleSize = inSampleSize;
        opts.inPreferredConfig = Bitmap.Config.ARGB_8888;
        Bitmap decoded;
        try (InputStream is = mResolver.openInputStream(uri)) {
            if (is == null) return null;
            decoded = BitmapFactory.decodeStream(is, null, opts);
        } catch (Throwable t) {
            return null;
        }
        if (decoded == null) {
            return null;
        }

        int exifDeg = readExifRotation(uri, fallbackOrientationDeg);
        int longest = Math.max(decoded.getWidth(), decoded.getHeight());
        if (exifDeg == 0 && longest <= maxDim) {
            return decoded;
        }

        Matrix m = new Matrix();
        if (longest > maxDim) {
            float scale = (float) maxDim / (float) longest;
            m.postScale(scale, scale);
        }
        if (exifDeg != 0) {
            m.postRotate(exifDeg);
        }
        Bitmap transformed = Bitmap.createBitmap(decoded, 0, 0, decoded.getWidth(), decoded.getHeight(), m, true);
        if (transformed != decoded) {
            decoded.recycle();
        }
        return transformed;
    }

    private int readExifRotation(Uri uri, int fallbackOrientationDeg) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            try (InputStream is = mResolver.openInputStream(uri)) {
                if (is != null) {
                    ExifInterface exif = new ExifInterface(is);
                    int orient = exif.getAttributeInt(ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_UNDEFINED);
                    switch (orient) {
                        case ExifInterface.ORIENTATION_ROTATE_90:
                            return 90;
                        case ExifInterface.ORIENTATION_ROTATE_180:
                            return 180;
                        case ExifInterface.ORIENTATION_ROTATE_270:
                            return 270;
                        case ExifInterface.ORIENTATION_NORMAL:
                            return 0;
                    }
                }
            } catch (Throwable ignored) {}
        }
        return fallbackOrientationDeg;
    }

    private static boolean writeJpegAtomic(Bitmap bmp, File dest, int quality) {
        File tmp = new File(dest.getParentFile(), dest.getName() + ".tmp");
        try (FileOutputStream out = new FileOutputStream(tmp)) {
            if (!bmp.compress(Bitmap.CompressFormat.JPEG, quality, out)) {
                tmp.delete();
                return false;
            }
            out.flush();
        } catch (Throwable t) {
            tmp.delete();
            return false;
        }
        if (dest.exists()) {
            dest.delete();
        }
        if (!tmp.renameTo(dest)) {
            tmp.delete();
            return false;
        }
        return true;
    }

    public void shutdown() {
        mShutdown = true;
        mListExecutor.shutdownNow();
        mImageExecutor.shutdownNow();
    }
}
