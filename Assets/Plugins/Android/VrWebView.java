package com.brenpoly.cardboardxr.browser;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.app.Presentation;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;
import android.view.Display;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.PermissionRequest;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;

import java.nio.ByteBuffer;

// An Android WebView rendered with full GPU hardware acceleration inside a private VirtualDisplay +
// Presentation, captured via ImageReader into a zero-allocation Direct ByteBuffer for Unity
// (BrowserWindow.cs).
//
// Keeps the Chromium renderer at high foreground priority (waivedWhenNotVisible = false) and
// recovers automatically in onRenderProcessGone so long video sessions stay stable without OOM
// or reload errors.
public final class VrWebView {
    private static final String TAG = "CardboardXRBrowser";
    private static final String DESKTOP_UA =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36";
    private static final String MOBILE_UA =
            "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/134.0.0.0 Mobile Safari/537.36";
    // VirtualDisplay density: 140 dpi on 1440x810 gives a sharp ~1645px desktop CSS layout with crisp text.
    private static final int VIRTUAL_DISPLAY_DPI = 140;
    private static final int FALLBACK_DESKTOP_SCALE_PERCENT = 58;
    private static final int MOBILE_SCALE_PERCENT = 100;
    private static final long METADATA_POLL_MS = 500L;

    private final Activity mActivity;
    private final int mViewWidth;
    private final int mViewHeight;
    private final int mTexWidth;
    private final int mTexHeight;
    private final int mByteCount;
    private final Object mLock = new Object();

    // Direct native buffers: mFrontDirect has a fixed native pointer read directly by Unity with zero GC.
    private final ByteBuffer mFrontDirect;
    private final ByteBuffer mBackDirect;
    private final byte[] mRowScratch;

    private HandlerThread mImageThread;
    private Handler mImageHandler;
    private ImageReader mImageReader;
    private VirtualDisplay mVirtualDisplay;
    private WebPresentation mPresentation;
    private FrameLayout mContainer;
    private WebView mWebView;
    private View mCustomView;
    private WebChromeClient.CustomViewCallback mCustomViewCallback;

    // Software fallback (only used if VirtualDisplay creation fails on an unsupported device).
    private Bitmap mFallbackBitmap;
    private Canvas mFallbackCanvas;
    private volatile boolean mUseVirtualDisplay;

    private volatile boolean mHasNewFrame;
    private volatile long mLastMetadataPollMs;
    private volatile String mCurrentUrl = "";
    private volatile String mCurrentTitle = "";
    private volatile boolean mVr360VideoDetected;
    private volatile boolean mVr360ModeActive;
    private volatile boolean mDesktopMode = true;
    private volatile int mScalePercent = 100;
    private volatile boolean mCanScrollUp;
    private volatile boolean mCanScrollDown;
    private volatile boolean mJsCanScrollUp;
    private volatile boolean mJsCanScrollDown;
    private volatile boolean mDisposed;
    private long mTouchDownTime;
    private boolean mTouchActive;

    public VrWebView(Activity activity, int viewWidth, int viewHeight, int texWidth, int texHeight, String initialUrl) {
        mActivity = activity;
        mViewWidth = viewWidth;
        mViewHeight = viewHeight;
        mTexWidth = texWidth;
        mTexHeight = texHeight;
        mByteCount = texWidth * texHeight * 4;
        mFrontDirect = ByteBuffer.allocateDirect(mByteCount);
        mBackDirect = ByteBuffer.allocateDirect(mByteCount);
        mRowScratch = new byte[texWidth * 4];

        activity.runOnUiThread(() -> initOnUiThread(initialUrl));
    }

    // Exposed once to Unity so C# can pass its fixed native pointer directly to Texture2D.LoadRawTextureData.
    public ByteBuffer getFrontBuffer() {
        return mFrontDirect;
    }

    @SuppressLint("SetJavaScriptEnabled")
    private void initOnUiThread(String initialUrl) {
        if (mDisposed)
            return;

        if (tryInitVirtualDisplay(initialUrl)) {
            mUseVirtualDisplay = true;
            Log.i(TAG, "Hardware-accelerated VirtualDisplay WebView ready (" + mTexWidth + "x" + mTexHeight + ")");
            return;
        }

        // Fallback if VirtualDisplay couldn't be created.
        mUseVirtualDisplay = false;
        mScalePercent = FALLBACK_DESKTOP_SCALE_PERCENT;
        mFallbackBitmap = Bitmap.createBitmap(mTexWidth, mTexHeight, Bitmap.Config.ARGB_8888);
        mFallbackCanvas = new Canvas(mFallbackBitmap);

        mContainer = new FrameLayout(mActivity);
        mWebView = createConfiguredWebView(mActivity, false);
        mWebView.setLayerType(View.LAYER_TYPE_SOFTWARE, null);
        mWebView.setAlpha(0.01f);
        mWebView.setInitialScale(mScalePercent);
        mContainer.addView(mWebView, new FrameLayout.LayoutParams(mViewWidth, mViewHeight));

        ViewGroup root = mActivity.findViewById(android.R.id.content);
        if (root != null) {
            root.addView(mContainer, 0, new FrameLayout.LayoutParams(mViewWidth, mViewHeight));
        }
        loadInitialUrl(initialUrl);
    }

    private boolean tryInitVirtualDisplay(String initialUrl) {
        try {
            DisplayManager dm = (DisplayManager) mActivity.getSystemService(Context.DISPLAY_SERVICE);
            if (dm == null)
                return false;

            mImageThread = new HandlerThread("VrWebView-ImageReader");
            mImageThread.start();
            mImageHandler = new Handler(mImageThread.getLooper());

            mImageReader = ImageReader.newInstance(mTexWidth, mTexHeight, PixelFormat.RGBA_8888, 2);
            mImageReader.setOnImageAvailableListener(this::onImageAvailable, mImageHandler);

            int flags = DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION
                    | DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY;
            mVirtualDisplay = dm.createVirtualDisplay(
                    "CardboardXR-WebView",
                    mTexWidth,
                    mTexHeight,
                    VIRTUAL_DISPLAY_DPI,
                    mImageReader.getSurface(),
                    flags);
            if (mVirtualDisplay == null || mVirtualDisplay.getDisplay() == null) {
                cleanupVirtualDisplay();
                return false;
            }

            mPresentation = new WebPresentation(mActivity, mVirtualDisplay.getDisplay(), initialUrl);
            mPresentation.show();
            return true;
        } catch (Throwable t) {
            Log.w(TAG, "VirtualDisplay WebView init failed, falling back to software mode: " + t);
            cleanupVirtualDisplay();
            return false;
        }
    }

    private void cleanupVirtualDisplay() {
        if (mPresentation != null) {
            try { mPresentation.dismiss(); } catch (Throwable ignored) {}
            mPresentation = null;
        }
        if (mVirtualDisplay != null) {
            try { mVirtualDisplay.release(); } catch (Throwable ignored) {}
            mVirtualDisplay = null;
        }
        if (mImageReader != null) {
            try { mImageReader.close(); } catch (Throwable ignored) {}
            mImageReader = null;
        }
        if (mImageThread != null) {
            try { mImageThread.quitSafely(); } catch (Throwable ignored) {}
            mImageThread = null;
            mImageHandler = null;
        }
    }

    @SuppressLint("SetJavaScriptEnabled")
    private WebView createConfiguredWebView(Context ctx, boolean hardware) {
        WebView webView = new WebView(ctx);
        webView.setLayerType(hardware ? View.LAYER_TYPE_HARDWARE : View.LAYER_TYPE_SOFTWARE, null);
        webView.setBackgroundColor(Color.rgb(18, 18, 24));
        webView.setFocusable(hardware);
        webView.setFocusableInTouchMode(hardware);
        webView.setClickable(hardware);

        // Critical for VirtualDisplay: keep the Chromium renderer at IMPORTANT foreground priority
        // even though the WebView is not attached to the default physical display (waivedWhenNotVisible = false).
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            webView.setRendererPriorityPolicy(WebView.RENDERER_PRIORITY_IMPORTANT, false);
        }

        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setCacheMode(WebSettings.LOAD_DEFAULT);
        settings.setMediaPlaybackRequiresUserGesture(false);
        settings.setUseWideViewPort(true);
        settings.setLoadWithOverviewMode(true);
        settings.setBuiltInZoomControls(true);
        settings.setDisplayZoomControls(false);
        settings.setSupportZoom(true);
        settings.setJavaScriptCanOpenWindowsAutomatically(true);
        settings.setSupportMultipleWindows(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
        settings.setUserAgentString(mDesktopMode ? DESKTOP_UA : MOBILE_UA);
        settings.setTextZoom(mDesktopMode ? 90 : 100);

        CookieManager cookies = CookieManager.getInstance();
        cookies.setAcceptCookie(true);
        cookies.setAcceptThirdPartyCookies(webView, true);

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                String url = request.getUrl().toString();
                if (url.startsWith("http://") || url.startsWith("https://")) {
                    return false;
                }
                if (url.startsWith("intent://")) {
                    try {
                        Intent parsed = Intent.parseUri(url, Intent.URI_INTENT_SCHEME);
                        String fallback = parsed.getStringExtra("browser_fallback_url");
                        if (fallback != null && (fallback.startsWith("http://") || fallback.startsWith("https://"))) {
                            view.loadUrl(fallback);
                        }
                    } catch (Throwable ignored) {
                    }
                }
                return true;
            }

            @Override
            public void onPageStarted(WebView view, String url, Bitmap favicon) {
                mCurrentUrl = url != null ? url : "";
                mVr360VideoDetected = false;
                injectYouTubeH264Override(view, url);
            }

            @Override
            public void onPageCommitVisible(WebView view, String url) {
                injectYouTubeH264Override(view, url);
                if (mDesktopMode)
                    applyDesktopViewport(view);
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                mCurrentUrl = url != null ? url : "";
                String title = view.getTitle();
                if (title != null && !title.isEmpty())
                    mCurrentTitle = title;
                if (mDesktopMode)
                    applyDesktopViewport(view);
            }

            @Override
            public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
                if (mDisposed)
                    return true;
                boolean crashed = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && detail != null && detail.didCrash();
                Log.w(TAG, "WebView renderer gone (crashed=" + crashed + "): recovering automatically");
                recoverWebViewOnUiThread();
                return true;
            }
        });

        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onReceivedTitle(WebView view, String title) {
                if (title != null)
                    mCurrentTitle = title;
            }

            @Override
            public void onPermissionRequest(PermissionRequest request) {
                request.grant(request.getResources());
            }

            @Override
            public void onShowCustomView(View view, CustomViewCallback callback) {
                if (mContainer == null) {
                    if (callback != null) callback.onCustomViewHidden();
                    return;
                }
                if (mCustomView != null) {
                    mContainer.removeView(mCustomView);
                    if (mCustomViewCallback != null)
                        mCustomViewCallback.onCustomViewHidden();
                }
                mCustomView = view;
                mCustomViewCallback = callback;
                if (mWebView != null)
                    mWebView.setVisibility(View.INVISIBLE);
                mContainer.addView(view, new FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT));
            }

            @Override
            public void onHideCustomView() {
                if (mCustomView != null && mContainer != null) {
                    mContainer.removeView(mCustomView);
                    mCustomView = null;
                }
                if (mCustomViewCallback != null) {
                    mCustomViewCallback.onCustomViewHidden();
                    mCustomViewCallback = null;
                }
                if (mWebView != null)
                    mWebView.setVisibility(View.VISIBLE);
            }
        });

        return webView;
    }

    private void recoverWebViewOnUiThread() {
        if (mDisposed || mContainer == null)
            return;
        String urlToRestore = mCurrentUrl;
        if (mCustomView != null) {
            mContainer.removeView(mCustomView);
            mCustomView = null;
            mCustomViewCallback = null;
        }
        if (mWebView != null) {
            mContainer.removeView(mWebView);
            try { mWebView.destroy(); } catch (Throwable ignored) {}
            mWebView = null;
        }
        Context ctx = mContainer.getContext();
        mWebView = createConfiguredWebView(ctx, mUseVirtualDisplay);
        if (!mUseVirtualDisplay) {
            mWebView.setAlpha(0.01f);
            mWebView.setInitialScale(mScalePercent);
        }
        mContainer.addView(mWebView, new FrameLayout.LayoutParams(
                mUseVirtualDisplay ? ViewGroup.LayoutParams.MATCH_PARENT : mViewWidth,
                mUseVirtualDisplay ? ViewGroup.LayoutParams.MATCH_PARENT : mViewHeight));
        mWebView.onResume();
        mWebView.resumeTimers();
        if (urlToRestore != null && !urlToRestore.isEmpty()) {
            mWebView.loadUrl(urlToRestore);
        }
    }

    private void loadInitialUrl(String initialUrl) {
        if (mWebView != null && initialUrl != null && !initialUrl.isEmpty()) {
            mCurrentUrl = initialUrl;
            mWebView.loadUrl(initialUrl);
        }
    }

    private void onImageAvailable(ImageReader reader) {
        if (mDisposed)
            return;
        Image image = null;
        try {
            image = reader.acquireLatestImage();
            if (image == null)
                return;
            Image.Plane[] planes = image.getPlanes();
            if (planes == null || planes.length == 0)
                return;
            ByteBuffer src = planes[0].getBuffer();
            int rowStride = planes[0].getRowStride();
            int rowBytes = mTexWidth * 4;
            int height = Math.min(mTexHeight, image.getHeight());

            synchronized (mLock) {
                mBackDirect.clear();
                if (rowStride == rowBytes) {
                    src.position(0);
                    src.limit(Math.min(src.capacity(), rowBytes * height));
                    mBackDirect.put(src);
                } else {
                    for (int y = 0; y < height; y++) {
                        src.position(y * rowStride);
                        src.get(mRowScratch, 0, rowBytes);
                        mBackDirect.put(mRowScratch, 0, rowBytes);
                    }
                }
                mHasNewFrame = true;
            }
        } catch (Throwable ignored) {
        } finally {
            if (image != null) {
                try { image.close(); } catch (Throwable ignored) {}
            }
        }
    }

    // Copies the latest GPU frame into mFrontDirect (zero heap allocation). Returns true if Unity should upload.
    public boolean syncFrontBuffer() {
        if (mDisposed)
            return false;
        if (mUseVirtualDisplay) {
            long now = SystemClock.uptimeMillis();
            if (now - mLastMetadataPollMs >= METADATA_POLL_MS) {
                mLastMetadataPollMs = now;
                mActivity.runOnUiThread(this::pollMetadataOnUiThread);
            }
        } else {
            mActivity.runOnUiThread(this::drawFallbackOnUiThread);
        }
        if (!mHasNewFrame)
            return false;
        synchronized (mLock) {
            if (!mHasNewFrame)
                return false;
            mBackDirect.clear();
            mFrontDirect.clear();
            mFrontDirect.put(mBackDirect);
            mHasNewFrame = false;
            return true;
        }
    }

    private void pollMetadataOnUiThread() {
        if (mDisposed || mWebView == null)
            return;
        try {
            String url = mWebView.getUrl();
            if (url != null)
                mCurrentUrl = url;
            String title = mWebView.getTitle();
            if (title != null && !title.isEmpty())
                mCurrentTitle = title;
            mCanScrollUp = mWebView.canScrollVertically(-1) || mJsCanScrollUp;
            mCanScrollDown = mWebView.canScrollVertically(1) || mJsCanScrollDown;
            if (mCustomView == null)
                probeScrollability();
            probeVr360Video();
        } catch (Throwable ignored) {
        }
    }

    private void injectYouTubeH264Override(WebView view, String url) {
        if (view == null || url == null)
            return;
        if (!url.contains("youtube.com") && !url.contains("youtu.be"))
            return;
        String js = "(function(){"
                + "if(window.__cardboardH264Hooked)return;"
                + "window.__cardboardH264Hooked=true;"
                + "if(window.MediaSource&&MediaSource.isTypeSupported){"
                + "var orig=MediaSource.isTypeSupported.bind(MediaSource);"
                + "MediaSource.isTypeSupported=function(t){"
                + "var s=(''+t).toLowerCase();"
                + "if(s.indexOf('webm')>=0||s.indexOf('vp9')>=0||s.indexOf('vp09')>=0||s.indexOf('av01')>=0)return false;"
                + "return orig(t);"
                + "};}"
                + "})();";
        view.evaluateJavascript(js, null);
    }

    private void probeVr360Video() {
        if (mDisposed || mWebView == null)
            return;
        String js = "(function(){"
                + "var vids=document.querySelectorAll('video');"
                + "var active=null;"
                + "for(var i=0;i<vids.length;i++){"
                + "var v=vids[i];"
                + "if(v&&!v.paused&&v.readyState>=2&&v.videoWidth>0){active=v;break;}"
                + "}"
                + "if(!active)return '0';"
                + "if(document.querySelector('.ytp-webgl-spherical-canvas,.ytp-spherical-control'))return '1';"
                + "try{var pr=window.ytInitialPlayerResponse;if(pr&&pr.videoDetails&&pr.videoDetails.isSpherical)return '1';}catch(e){}"
                + "var txt=(document.title+' '+location.href).toLowerCase();"
                + "if(/(?:^|[^a-z0-9])(360\\u00B0?|vr180|180\\u00B0|equirectangular)(?:$|[^a-z0-9])/.test(txt))return '1';"
                + "if(/(?:^|[^a-z0-9])3d(?:$|[^a-z0-9])/.test(txt)&&/(?:^|[^a-z0-9])(vr|sbs|180|360)(?:$|[^a-z0-9])/.test(txt))return '1';"
                + "return '0';"
                + "})();";
        mWebView.evaluateJavascript(js, value -> mVr360VideoDetected = value != null && value.contains("1"));
    }

    public boolean isVr360VideoDetected() {
        return mVr360VideoDetected;
    }

    public boolean isVr360ModeActive() {
        return mVr360ModeActive;
    }

    public void setVr360Mode(boolean active) {
        mVr360ModeActive = active;
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            String js = active
                    ? "(function(){"
                      + "var old=document.getElementById('cardboardxr-vr360-style');if(old)old.remove();"
                      + "var vids=document.querySelectorAll('video');"
                      + "var pick=null;"
                      + "for(var i=0;i<vids.length;i++){vids[i].classList.remove('cardboardxr-vr360-video');if(!pick&&!vids[i].paused&&vids[i].videoWidth>0)pick=vids[i];}"
                      + "if(!pick&&vids.length>0)pick=vids[0];"
                      + "if(pick)pick.classList.add('cardboardxr-vr360-video');"
                      + "var st=document.createElement('style');st.id='cardboardxr-vr360-style';"
                      + "st.textContent='html,body{overflow:hidden!important;background:#000!important;}'+"
                      + "'.ytp-webgl-spherical-canvas,.ytp-chrome-top,.ytp-chrome-bottom,.ytp-gradient-top,.ytp-gradient-bottom,.ytp-watermark,.ytp-pause-overlay{display:none!important;opacity:0!important;}'+"
                      + "'video.cardboardxr-vr360-video{position:fixed!important;inset:0!important;left:0!important;top:0!important;right:0!important;bottom:0!important;width:100vw!important;height:100vh!important;max-width:100vw!important;max-height:100vh!important;object-fit:fill!important;transform:none!important;visibility:visible!important;opacity:1!important;display:block!important;z-index:2147483647!important;background:#000!important;}';"
                      + "(document.head||document.documentElement).appendChild(st);"
                      + "})();"
                    : "(function(){"
                      + "var old=document.getElementById('cardboardxr-vr360-style');if(old)old.remove();"
                      + "var vids=document.querySelectorAll('video.cardboardxr-vr360-video');"
                      + "for(var i=0;i<vids.length;i++)vids[i].classList.remove('cardboardxr-vr360-video');"
                      + "})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    private void drawFallbackOnUiThread() {
        if (mDisposed || mWebView == null || mFallbackBitmap == null)
            return;
        try {
            pollMetadataOnUiThread();
            int save = mFallbackCanvas.save();
            float sx = (float) mTexWidth / mViewWidth;
            float sy = (float) mTexHeight / mViewHeight;
            mFallbackCanvas.scale(sx, sy);
            mFallbackCanvas.translate(-mWebView.getScrollX(), -mWebView.getScrollY());
            mWebView.draw(mFallbackCanvas);
            mFallbackCanvas.restoreToCount(save);

            synchronized (mLock) {
                mBackDirect.clear();
                mFallbackBitmap.copyPixelsToBuffer(mBackDirect);
                mHasNewFrame = true;
            }
        } catch (Throwable ignored) {
        }
    }

    public void loadUrl(String url) {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null || url == null)
                return;
            if (mCustomView != null && mCustomViewCallback != null) {
                mCustomViewCallback.onCustomViewHidden();
            }
            mVr360VideoDetected = false;
            mVr360ModeActive = false;
            mCurrentUrl = url;
            mWebView.loadUrl(url);
        });
    }

    private void applyDesktopViewport(WebView view) {
        String url = view.getUrl();
        if (url != null && (url.startsWith("http://127.0.0.1:") || url.startsWith("http://localhost:")))
            return;
        String scale = mUseVirtualDisplay ? "0.75" : "0.60";
        String js = "(function(){"
                + "var m=document.querySelector('meta[name=viewport]');"
                + "if(!m){m=document.createElement('meta');m.name='viewport';if(document.head)document.head.appendChild(m);}"
                + "if(m)m.setAttribute('content','width=1600, initial-scale=" + scale + ", minimum-scale=0.25, maximum-scale=5.0, user-scalable=yes');"
                + "})();";
        view.evaluateJavascript(js, null);
    }

    public void setDesktopMode(boolean desktop) {
        mDesktopMode = desktop;
        mScalePercent = desktop
                ? (mUseVirtualDisplay ? 100 : FALLBACK_DESKTOP_SCALE_PERCENT)
                : MOBILE_SCALE_PERCENT;
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            WebSettings s = mWebView.getSettings();
            s.setUserAgentString(desktop ? DESKTOP_UA : MOBILE_UA);
            s.setUseWideViewPort(desktop);
            s.setLoadWithOverviewMode(desktop);
            s.setTextZoom(desktop ? 90 : 100);
            if (!mUseVirtualDisplay)
                mWebView.setInitialScale(mScalePercent);
            mWebView.reload();
        });
    }

    public void adjustZoom(int deltaPercent) {
        mScalePercent = Math.max(30, Math.min(180, mScalePercent + deltaPercent));
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            if (deltaPercent < 0)
                mWebView.zoomOut();
            else if (deltaPercent > 0)
                mWebView.zoomIn();
        });
    }

    // Types a character or query into the currently focused input element on the page (e.g. Google or YouTube search).
    public void typeText(String text, boolean submit) {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null || text == null)
                return;
            boolean isBackspace = "\b".equals(text);
            String escaped = isBackspace ? "" : text
                    .replace("\\", "\\\\")
                    .replace("'", "\\'")
                    .replace("\n", "")
                    .replace("\r", "");
            String js = "(function(){"
                    + "var el=document.activeElement;"
                    + "var isEditable=el&&(el.tagName==='INPUT'||el.tagName==='TEXTAREA'||el.isContentEditable);"
                    + "if(!isEditable){"
                    + "el=document.querySelector('input[type=search],input[name=search_query],textarea[name=q],input[name=q],input[type=text],input:not([type]),textarea');"
                    + "if(el)el.focus();"
                    + "}"
                    + "if(!el)return;"
                    + "var setVal=function(node,val){"
                    + "if(node.isContentEditable){node.textContent=val;return;}"
                    + "var proto=node.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;"
                    + "var desc=Object.getOwnPropertyDescriptor(proto,'value');"
                    + "if(desc&&desc.set){desc.set.call(node,val);}else{node.value=val;}"
                    + "};"
                    + "var cur=el.isContentEditable?(el.textContent||''):(el.value||'');"
                    + (isBackspace
                        ? "setVal(el,cur.slice(0,-1));"
                        : (escaped.isEmpty() ? "" : "setVal(el,cur+'" + escaped + "');"))
                    + "el.dispatchEvent(new Event('input',{bubbles:true}));"
                    + "el.dispatchEvent(new Event('change',{bubbles:true}));"
                    + (submit
                        ? "el.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));"
                          + "el.dispatchEvent(new KeyboardEvent('keyup',{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}));"
                          + "if(el.form){if(el.form.requestSubmit){try{el.form.requestSubmit();}catch(e){el.form.submit();}}else{el.form.submit();}}"
                          + "else{var q=encodeURIComponent(el.value||el.textContent||'');if(q)window.location.href='https://www.google.com/search?q='+q;}"
                        : "")
                    + "})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    // Clears the currently focused input or search box on the page.
    public void clearInput() {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            String js = "(function(){"
                    + "var el=document.activeElement;"
                    + "var isEditable=el&&(el.tagName==='INPUT'||el.tagName==='TEXTAREA'||el.isContentEditable);"
                    + "if(!isEditable){"
                    + "el=document.querySelector('input[type=search],input[name=search_query],textarea[name=q],input[name=q],input[type=text],textarea');"
                    + "}"
                    + "if(!el)return;"
                    + "if(el.isContentEditable){el.textContent='';}"
                    + "else{"
                    + "var proto=el.tagName==='TEXTAREA'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;"
                    + "var desc=Object.getOwnPropertyDescriptor(proto,'value');"
                    + "if(desc&&desc.set){desc.set.call(el,'');}else{el.value='';}"
                    + "}"
                    + "el.dispatchEvent(new Event('input',{bubbles:true}));"
                    + "el.dispatchEvent(new Event('change',{bubbles:true}));"
                    + "})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    public boolean isDesktopMode() {
        return mDesktopMode;
    }

    public String getUrl() {
        return mCurrentUrl;
    }

    public String getTitle() {
        return mCurrentTitle;
    }

    // Dispatches a click at normalized (u, v) in [0..1] (u left-to-right, v top-to-bottom).
    public void clickNormalized(float u, float v) {
        final float cu = Math.max(0f, Math.min(1f, u));
        final float cv = Math.max(0f, Math.min(1f, v));
        mActivity.runOnUiThread(() -> {
            if (mDisposed)
                return;
            View target = mContainer != null ? mContainer : mWebView;
            if (target == null)
                return;
            int w = target.getWidth() > 0 ? target.getWidth() : (mUseVirtualDisplay ? mTexWidth : mViewWidth);
            int h = target.getHeight() > 0 ? target.getHeight() : (mUseVirtualDisplay ? mTexHeight : mViewHeight);
            float x = cu * w;
            float y = cv * h;
            long now = SystemClock.uptimeMillis();
            MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, x, y, 0);
            MotionEvent up = MotionEvent.obtain(now, now + 40, MotionEvent.ACTION_UP, x, y, 0);
            target.dispatchTouchEvent(down);
            target.dispatchTouchEvent(up);
            down.recycle();
            up.recycle();
        });
    }

    // Direct 1:1 native Android touch injection for continuous visual pinch scrolling within window bounds.
    public void touchDown(float u, float v) {
        if (mDisposed)
            return;
        final float cu = Math.max(0f, Math.min(1f, u));
        final float cv = Math.max(0f, Math.min(1f, v));
        mActivity.runOnUiThread(() -> {
            if (mDisposed)
                return;
            View target = mContainer != null ? mContainer : mWebView;
            if (target == null)
                return;
            int w = target.getWidth() > 0 ? target.getWidth() : (mUseVirtualDisplay ? mTexWidth : mViewWidth);
            int h = target.getHeight() > 0 ? target.getHeight() : (mUseVirtualDisplay ? mTexHeight : mViewHeight);
            float x = cu * w;
            float y = cv * h;
            long now = SystemClock.uptimeMillis();
            if (mTouchActive) {
                MotionEvent cancelEv = MotionEvent.obtain(mTouchDownTime, now, MotionEvent.ACTION_CANCEL, x, y, 0);
                target.dispatchTouchEvent(cancelEv);
                cancelEv.recycle();
                mTouchActive = false;
            }
            mTouchDownTime = now;
            mTouchActive = true;
            MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, x, y, 0);
            target.dispatchTouchEvent(down);
            down.recycle();
        });
    }

    public void touchMove(float u, float v) {
        if (mDisposed)
            return;
        final float cu = Math.max(0f, Math.min(1f, u));
        final float cv = Math.max(0f, Math.min(1f, v));
        mActivity.runOnUiThread(() -> {
            if (mDisposed || !mTouchActive)
                return;
            View target = mContainer != null ? mContainer : mWebView;
            if (target == null)
                return;
            int w = target.getWidth() > 0 ? target.getWidth() : (mUseVirtualDisplay ? mTexWidth : mViewWidth);
            int h = target.getHeight() > 0 ? target.getHeight() : (mUseVirtualDisplay ? mTexHeight : mViewHeight);
            float x = cu * w;
            float y = cv * h;
            long now = SystemClock.uptimeMillis();
            MotionEvent move = MotionEvent.obtain(mTouchDownTime, now, MotionEvent.ACTION_MOVE, x, y, 0);
            target.dispatchTouchEvent(move);
            move.recycle();
        });
    }

    public void touchUp(float u, float v, boolean cancel) {
        final float cu = Math.max(0f, Math.min(1f, u));
        final float cv = Math.max(0f, Math.min(1f, v));
        mActivity.runOnUiThread(() -> {
            if (!mTouchActive)
                return;
            mTouchActive = false;
            View target = mContainer != null ? mContainer : mWebView;
            if (target == null)
                return;
            int w = target.getWidth() > 0 ? target.getWidth() : (mUseVirtualDisplay ? mTexWidth : mViewWidth);
            int h = target.getHeight() > 0 ? target.getHeight() : (mUseVirtualDisplay ? mTexHeight : mViewHeight);
            float x = cu * w;
            float y = cv * h;
            long now = SystemClock.uptimeMillis();
            MotionEvent up = MotionEvent.obtain(
                    mTouchDownTime,
                    now,
                    cancel ? MotionEvent.ACTION_CANCEL : MotionEvent.ACTION_UP,
                    x, y, 0);
            target.dispatchTouchEvent(up);
            up.recycle();
        });
    }

    public boolean tryScroll(int deltaY) {
        if (mCustomView != null)
            return false;
        if (deltaY < 0 && !mCanScrollUp)
            return false;
        if (deltaY > 0 && !mCanScrollDown)
            return false;
        scroll(deltaY);
        return true;
    }

    private void probeScrollability() {
        if (mDisposed || mWebView == null)
            return;
        int w = mWebView.getWidth() > 0 ? mWebView.getWidth() : mTexWidth;
        int h = mWebView.getHeight() > 0 ? mWebView.getHeight() : mTexHeight;
        String js = "(function(){"
                + "var up=false,down=false;"
                + "var se=document.scrollingElement||document.documentElement;"
                + "if(se&&se.scrollHeight>se.clientHeight+12){"
                + "if(se.scrollTop>2)up=true;"
                + "if(se.scrollTop+se.clientHeight<se.scrollHeight-4)down=true;"
                + "}"
                + "var el=document.elementFromPoint(" + (w / 2) + "," + (h / 2) + ");"
                + "while(el&&el!==document.body&&el!==document.documentElement){"
                + "var st=window.getComputedStyle(el);"
                + "if((st.overflowY==='auto'||st.overflowY==='scroll')&&el.scrollHeight>el.clientHeight+12){"
                + "if(el.scrollTop>2)up=true;"
                + "if(el.scrollTop+el.clientHeight<el.scrollHeight-4)down=true;"
                + "break;"
                + "}"
                + "el=el.parentElement;"
                + "}"
                + "return (up?'1':'0')+(down?'1':'0');"
                + "})();";
        mWebView.evaluateJavascript(js, value -> {
            if (value != null && value.length() >= 4) {
                mJsCanScrollUp = value.charAt(1) == '1';
                mJsCanScrollDown = value.charAt(2) == '1';
                if (mWebView != null) {
                    mCanScrollUp = mWebView.canScrollVertically(-1) || mJsCanScrollUp;
                    mCanScrollDown = mWebView.canScrollVertically(1) || mJsCanScrollDown;
                }
            }
        });
    }

    public void scroll(int deltaY) {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            int targetY = Math.max(0, mWebView.getScrollY() + deltaY);
            mWebView.scrollTo(mWebView.getScrollX(), targetY);
            int w = mWebView.getWidth() > 0 ? mWebView.getWidth() : mTexWidth;
            int h = mWebView.getHeight() > 0 ? mWebView.getHeight() : mTexHeight;
            String js = "(function(){window.scrollBy({top:" + deltaY + ",behavior:'smooth'});"
                    + "var el=document.elementFromPoint(" + (w / 2) + "," + (h / 2) + ");"
                    + "while(el&&el!==document.body){if(el.scrollHeight>el.clientHeight){el.scrollBy({top:" + deltaY + ",behavior:'smooth'});break;}el=el.parentElement;}"
                    + "})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    // Smooth immediate drag scrolling (from continuous visual pinch drag) with no CSS smooth animation lag.
    public void scrollDrag(int deltaX, int deltaY, float normU, float normV) {
        if (mCustomView != null || (deltaX == 0 && deltaY == 0))
            return;
        final float cu = Math.max(0f, Math.min(1f, normU));
        final float cv = Math.max(0f, Math.min(1f, normV));
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            int w = mWebView.getWidth() > 0 ? mWebView.getWidth() : mTexWidth;
            int h = mWebView.getHeight() > 0 ? mWebView.getHeight() : mTexHeight;
            int px = Math.round(cu * w);
            int py = Math.round(cv * h);
            String js = "(function(){"
                    + "var dx=" + deltaX + ",dy=" + deltaY + ";"
                    + "var el=document.elementFromPoint(" + px + "," + py + ");"
                    + "while(el&&el!==document.body&&el!==document.documentElement){"
                    + "var st=window.getComputedStyle(el);"
                    + "var sy=(st.overflowY==='auto'||st.overflowY==='scroll'||st.overflowY==='overlay')&&el.scrollHeight>el.clientHeight+4;"
                    + "var sx=(st.overflowX==='auto'||st.overflowX==='scroll'||st.overflowX==='overlay')&&el.scrollWidth>el.clientWidth+4;"
                    + "if((dy!==0&&sy&&((dy>0&&el.scrollTop+el.clientHeight<el.scrollHeight-1)||(dy<0&&el.scrollTop>1)))"
                    + "||(dx!==0&&sx&&((dx>0&&el.scrollLeft+el.clientWidth<el.scrollWidth-1)||(dx<0&&el.scrollLeft>1)))){"
                    + "el.scrollBy(dx,dy);return;}"
                    + "el=el.parentElement;}"
                    + "window.scrollBy(dx,dy);"
                    + "})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    public void togglePlayPause() {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            String js = "(function(){var v=document.querySelector('video');"
                    + "if(v){if(v.paused)v.play();else v.pause();}})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    public void setMuted(boolean muted) {
        mActivity.runOnUiThread(() -> {
            if (mDisposed || mWebView == null)
                return;
            String js = "(function(){var v=document.querySelector('video');if(v)v.muted=" + muted + ";})();";
            mWebView.evaluateJavascript(js, null);
        });
    }

    public void goBack() {
        mActivity.runOnUiThread(() -> {
            if (mDisposed)
                return;
            if (mCustomView != null && mCustomViewCallback != null) {
                mCustomViewCallback.onCustomViewHidden();
                return;
            }
            if (mWebView != null && mWebView.canGoBack())
                mWebView.goBack();
        });
    }

    public void goForward() {
        mActivity.runOnUiThread(() -> {
            if (!mDisposed && mWebView != null && mWebView.canGoForward())
                mWebView.goForward();
        });
    }

    public void reload() {
        mActivity.runOnUiThread(() -> {
            if (!mDisposed && mWebView != null)
                mWebView.reload();
        });
    }

    public void dispose() {
        mDisposed = true;
        mActivity.runOnUiThread(() -> {
            mTouchActive = false;
            if (mCustomView != null && mCustomViewCallback != null) {
                try { mCustomViewCallback.onCustomViewHidden(); } catch (Throwable ignored) {}
                mCustomView = null;
                mCustomViewCallback = null;
            }
            if (mWebView != null) {
                ViewGroup parent = (ViewGroup) mWebView.getParent();
                if (parent != null)
                    parent.removeView(mWebView);
                mWebView.stopLoading();
                mWebView.loadUrl("about:blank");
                mWebView.destroy();
                mWebView = null;
            }
            if (!mUseVirtualDisplay && mContainer != null) {
                ViewGroup parent = (ViewGroup) mContainer.getParent();
                if (parent != null)
                    parent.removeView(mContainer);
            }
            mContainer = null;
            cleanupVirtualDisplay();
            if (mFallbackBitmap != null) {
                mFallbackBitmap.recycle();
                mFallbackBitmap = null;
            }
            mFallbackCanvas = null;
        });
    }

    private final class WebPresentation extends Presentation {
        private final String mInitialUrl;

        WebPresentation(Context outerContext, Display display, String initialUrl) {
            super(outerContext, display, android.R.style.Theme_Black_NoTitleBar_Fullscreen);
            mInitialUrl = initialUrl;
        }

        @Override
        protected void onCreate(Bundle savedInstanceState) {
            super.onCreate(savedInstanceState);
            Window window = getWindow();
            if (window != null) {
                window.requestFeature(Window.FEATURE_NO_TITLE);
                window.addFlags(WindowManager.LayoutParams.FLAG_HARDWARE_ACCELERATED
                        | WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                        | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL);
                window.setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT);
            }
            Context ctx = getContext();
            mContainer = new FrameLayout(ctx);
            mContainer.setBackgroundColor(Color.rgb(18, 18, 24));
            mWebView = createConfiguredWebView(ctx, true);
            mContainer.addView(mWebView, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT));
            setContentView(mContainer);
            mWebView.onResume();
            mWebView.resumeTimers();
            loadInitialUrl(mInitialUrl);
        }
    }
}
