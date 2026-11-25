package com.brenpoly.cardboardxr.watch;

import android.content.Context;
import android.os.SystemClock;
import android.util.Log;

import com.google.android.gms.wearable.MessageClient;
import com.google.android.gms.wearable.MessageEvent;
import com.google.android.gms.wearable.Wearable;

import java.util.concurrent.atomic.AtomicInteger;

// The phone's end of the CardboardXR Pixel Watch app (WearApp/ in the repository), over the Wearable
// Data Layer. Both apps have the same application id and must be signed with the same key, or the
// Data Layer doesn't deliver between them. The watch sends:
//   /cardboardxr/heartbeat  every couple of seconds while its session runs; answered with /cardboardxr/state
//   /cardboardxr/pinch      a double pinch (or a tap on the watch's screen)
//   /cardboardxr/settings   the watch's Settings button
//   /cardboardxr/bye        its session stopped
// Messages arrive on a Play services thread; Unity reads the counts once a frame (WatchLink.cs).
public final class WatchBridge {
    private static final String TAG = "CardboardXRWatch";
    static final String PATH_HEARTBEAT = "/cardboardxr/heartbeat";
    static final String PATH_PINCH = "/cardboardxr/pinch";
    static final String PATH_SETTINGS = "/cardboardxr/settings";
    static final String PATH_ROTARY = "/cardboardxr/rotary";
    static final String PATH_GYRO = "/cardboardxr/gyro";
    static final String PATH_GRAB = "/cardboardxr/grab";
    static final String PATH_BYE = "/cardboardxr/bye";
    static final String PATH_STATE = "/cardboardxr/state";
    // Connected while a heartbeat came within this long.
    private static final long CONNECTED_MS = 5000;
    private static final long COUNT_MASK = 0xFFFFFFL;

    private static final AtomicInteger sPinches = new AtomicInteger();
    private static final AtomicInteger sSettings = new AtomicInteger();
    private static final AtomicInteger sRotary = new AtomicInteger();
    private static final AtomicInteger sGyroYawMicro = new AtomicInteger();
    private static final AtomicInteger sGyroPitchMicro = new AtomicInteger();
    private static volatile long sLastHeartbeat = Long.MIN_VALUE / 2;
    private static volatile String sLastNodeId;
    private static volatile boolean sHandPinchWanted;
    private static volatile boolean sSinglePinchMode;
    private static volatile boolean sGrabbing;
    private static MessageClient sClient;

    private static final MessageClient.OnMessageReceivedListener sListener = WatchBridge::onMessage;

    private WatchBridge() {}

    public static synchronized void start(Context context) {
        if (sClient != null)
            return;
        sClient = Wearable.getMessageClient(context.getApplicationContext());
        sClient.addListener(sListener)
                .addOnFailureListener(e -> Log.w(TAG, "Couldn't listen for the watch: " + e));
        Log.i(TAG, "Listening for the watch");
    }

    public static synchronized void stop() {
        if (sClient == null)
            return;
        sClient.removeListener(sListener);
        sClient = null;
    }

    // Pinch count in bits 0-23, settings requests in 24-47, connected in bit 48, singlePinchMode in bit 49.
    public static long poll() {
        boolean connected = SystemClock.elapsedRealtime() - sLastHeartbeat < CONNECTED_MS;
        return (sPinches.get() & COUNT_MASK)
                | ((sSettings.get() & COUNT_MASK) << 24)
                | ((connected ? 1L : 0L) << 48)
                | ((sSinglePinchMode ? 1L : 0L) << 49);
    }

    // Returns and clears accumulated rotary crown steps since the last frame (+ = scroll down / push farther, - = scroll up / pull closer).
    public static int pollRotary() {
        return sRotary.getAndSet(0);
    }

    // Returns and clears accumulated wrist yaw delta (microradians, + = right) while grabbing.
    public static int pollGyroYaw() {
        return sGyroYawMicro.getAndSet(0);
    }

    // Returns and clears accumulated wrist pitch delta (microradians, + = up) while grabbing.
    public static int pollGyroPitch() {
        return sGyroPitchMicro.getAndSet(0);
    }

    // Tells the watch whether an object/window is currently grabbed so it streams gyro deltas and gives haptic feedback.
    public static void setGrabbing(boolean grabbing) {
        if (sGrabbing == grabbing)
            return;
        sGrabbing = grabbing;
        if (!grabbing) {
            sGyroYawMicro.set(0);
            sGyroPitchMicro.set(0);
        }
        MessageClient client = sClient;
        String nodeId = sLastNodeId;
        if (client != null && nodeId != null) {
            client.sendMessage(nodeId, PATH_GRAB, new byte[] { (byte) (grabbing ? 1 : 0) });
        }
    }

    // Whether the current scene needs the camera's pinch: the watch shows it.
    public static void setHandPinchWanted(boolean wanted) {
        sHandPinchWanted = wanted;
    }

    private static void onMessage(MessageEvent event) {
        String path = event.getPath();
        String sourceNode = event.getSourceNodeId();
        if (sourceNode != null && !sourceNode.isEmpty())
            sLastNodeId = sourceNode;
        switch (path) {
            case PATH_HEARTBEAT:
                sLastHeartbeat = SystemClock.elapsedRealtime();
                byte[] data = event.getData();
                if (data != null && data.length > 0)
                    sSinglePinchMode = data[0] == 1;
                reply(sourceNode);
                break;
            case PATH_PINCH:
                sLastHeartbeat = SystemClock.elapsedRealtime();
                sPinches.incrementAndGet();
                break;
            case PATH_SETTINGS:
                sSettings.incrementAndGet();
                break;
            case PATH_ROTARY:
                sLastHeartbeat = SystemClock.elapsedRealtime();
                byte[] rot = event.getData();
                if (rot != null && rot.length >= 4) {
                    int steps = ((rot[0] & 0xFF) << 24)
                            | ((rot[1] & 0xFF) << 16)
                            | ((rot[2] & 0xFF) << 8)
                            | (rot[3] & 0xFF);
                    sRotary.addAndGet(steps);
                }
                break;
            case PATH_GYRO:
                sLastHeartbeat = SystemClock.elapsedRealtime();
                byte[] gyro = event.getData();
                if (gyro != null && gyro.length >= 8) {
                    int yawMicro = ((gyro[0] & 0xFF) << 24)
                            | ((gyro[1] & 0xFF) << 16)
                            | ((gyro[2] & 0xFF) << 8)
                            | (gyro[3] & 0xFF);
                    int pitchMicro = ((gyro[4] & 0xFF) << 24)
                            | ((gyro[5] & 0xFF) << 16)
                            | ((gyro[6] & 0xFF) << 8)
                            | (gyro[7] & 0xFF);
                    sGyroYawMicro.addAndGet(yawMicro);
                    sGyroPitchMicro.addAndGet(pitchMicro);
                }
                break;
            case PATH_BYE:
                sLastHeartbeat = Long.MIN_VALUE / 2;
                sGrabbing = false;
                sGyroYawMicro.set(0);
                sGyroPitchMicro.set(0);
                break;
            default:
                break;
        }
    }

    // Tells the watch the phone app is there, whether this scene uses the camera's pinch, and grab state.
    private static void reply(String nodeId) {
        MessageClient client = sClient;
        if (client == null || nodeId == null)
            return;
        int flags = (sHandPinchWanted ? 2 : 1) | (sGrabbing ? 4 : 0);
        byte[] state = new byte[] { (byte) flags };
        client.sendMessage(nodeId, PATH_STATE, state);
    }
}
