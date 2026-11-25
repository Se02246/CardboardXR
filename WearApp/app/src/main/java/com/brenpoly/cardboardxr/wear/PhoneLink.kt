package com.brenpoly.cardboardxr.wear

import android.content.Context
import android.os.SystemClock
import com.google.android.gms.wearable.MessageClient
import com.google.android.gms.wearable.MessageEvent
import com.google.android.gms.wearable.Wearable
import java.util.concurrent.atomic.AtomicInteger

/**
 * The watch's end of the link to CardboardXR on the phone, over the Wearable Data Layer. The phone's
 * end is Assets/Plugins/Android/WatchBridge.java in the Unity project; the paths must match. Both
 * apps have the same application id and must be signed with the same key.
 */
class PhoneLink(context: Context) : MessageClient.OnMessageReceivedListener {
    companion object {
        const val PATH_HEARTBEAT = "/cardboardxr/heartbeat"
        const val PATH_PINCH = "/cardboardxr/pinch"
        const val PATH_SETTINGS = "/cardboardxr/settings"
        const val PATH_ROTARY = "/cardboardxr/rotary"
        const val PATH_GYRO = "/cardboardxr/gyro"
        const val PATH_GRAB = "/cardboardxr/grab"
        const val PATH_BYE = "/cardboardxr/bye"
        const val PATH_STATE = "/cardboardxr/state"
        private val EMPTY = ByteArray(0)
    }

    private val messages = Wearable.getMessageClient(context.applicationContext)
    private val nodes = Wearable.getNodeClient(context.applicationContext)
    @Volatile private var phoneIds: List<String> = emptyList()

    fun start() {
        messages.addListener(this)
        refreshNodes()
    }

    fun stop() {
        send(PATH_BYE)
        messages.removeListener(this)
    }

    /** Looks for the phone again: it may have reconnected. */
    fun refreshNodes() {
        nodes.connectedNodes.addOnSuccessListener { list -> phoneIds = list.map { it.id } }
    }

    fun send(path: String, payload: ByteArray = EMPTY) {
        for (id in phoneIds)
            messages.sendMessage(id, path, payload)
    }

    fun sendHeartbeat() {
        val modeByte = if (Session.singlePinchMode) 1.toByte() else 0.toByte()
        send(PATH_HEARTBEAT, byteArrayOf(modeByte))
    }

    fun sendRotary(steps: Int) {
        if (steps == 0) return
        val bytes = byteArrayOf(
            (steps ushr 24).toByte(),
            (steps ushr 16).toByte(),
            (steps ushr 8).toByte(),
            steps.toByte()
        )
        send(PATH_ROTARY, bytes)
    }

    fun sendGyro(yawMicroRad: Int, pitchMicroRad: Int) {
        if (yawMicroRad == 0 && pitchMicroRad == 0) return
        val bytes = byteArrayOf(
            (yawMicroRad ushr 24).toByte(),
            (yawMicroRad ushr 16).toByte(),
            (yawMicroRad ushr 8).toByte(),
            yawMicroRad.toByte(),
            (pitchMicroRad ushr 24).toByte(),
            (pitchMicroRad ushr 16).toByte(),
            (pitchMicroRad ushr 8).toByte(),
            pitchMicroRad.toByte()
        )
        send(PATH_GYRO, bytes)
    }

    override fun onMessageReceived(event: MessageEvent) {
        when (event.path) {
            PATH_STATE -> {
                val raw = event.data.firstOrNull()?.toInt() ?: Session.MODE_WATCH
                Session.phoneMode = raw and 0x03
                Session.setGrabbing((raw and 0x04) != 0)
                Session.lastPhoneMs = SystemClock.elapsedRealtime()
            }
            PATH_GRAB -> {
                val active = (event.data.firstOrNull()?.toInt() ?: 0) != 0
                Session.setGrabbing(active)
                Session.lastPhoneMs = SystemClock.elapsedRealtime()
            }
        }
    }
}

/** What the service and the screen share while a session runs. */
object Session {
    /** The phone app clicks with the watch's double pinch. */
    const val MODE_WATCH = 1
    /** The phone app's current scene uses the camera's pinch; the double pinch still clicks. */
    const val MODE_HAND = 2
    private const val CONNECTED_MS = 6000L
    private const val MIN_SINGLE_PINCH_GAP_MS = 380L
    private const val MIN_DOUBLE_PINCH_GAP_MS = 500L

    @Volatile var running = false
    @Volatile var lastPhoneMs = Long.MIN_VALUE / 2
    @Volatile var phoneMode = MODE_WATCH
    @Volatile var systemGestureActive = false
    @Volatile var singlePinchMode = false
    @Volatile var grabbing = false
        private set
    @Volatile var lastPinchRealtimeMs = 0L
    @Volatile var blankUntilMs = 0L
    @Volatile var detector: DoublePinchDetector? = null
    @Volatile var link: PhoneLink? = null
    @Volatile var onGrabChanged: ((Boolean) -> Unit)? = null
    val pinches = AtomicInteger()

    val phoneConnected: Boolean
        get() = running && SystemClock.elapsedRealtime() - lastPhoneMs < CONNECTED_MS

    fun setGrabbing(active: Boolean) {
        if (grabbing == active) return
        grabbing = active
        onGrabChanged?.invoke(active)
    }

    /** Suppresses IMU and system-gesture pinches for [durationMs] so haptic vibration never self-triggers. */
    fun blankPinchesFor(durationMs: Long) {
        val until = SystemClock.elapsedRealtime() + durationMs
        if (until > blankUntilMs) blankUntilMs = until
        detector?.suppressForMs(durationMs)
    }

    @Synchronized
    fun sendPinch(): Boolean {
        val activeLink = link ?: return false
        val now = SystemClock.elapsedRealtime()
        val minGap = if (singlePinchMode) MIN_SINGLE_PINCH_GAP_MS else MIN_DOUBLE_PINCH_GAP_MS
        if (now < blankUntilMs || (lastPinchRealtimeMs > 0L && now - lastPinchRealtimeMs < minGap))
            return false
        pinches.incrementAndGet()
        lastPinchRealtimeMs = now
        activeLink.send(PhoneLink.PATH_PINCH)
        return true
    }
}

/** The sensitivity, kept between sessions. */
object Prefs {
    private const val FILE = "settings"
    private const val SENSITIVITY = "sensitivity"
    private const val SINGLE_PINCH = "single_pinch"

    fun sensitivity(context: Context): Float =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getFloat(SENSITIVITY, 0.55f)

    fun setSensitivity(context: Context, value: Float) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit().putFloat(SENSITIVITY, value).apply()
    }

    fun singlePinchMode(context: Context): Boolean =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).getBoolean(SINGLE_PINCH, false)

    fun setSinglePinchMode(context: Context, single: Boolean) {
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE).edit().putBoolean(SINGLE_PINCH, single).apply()
    }
}
