package com.brenpoly.cardboardxr.wear

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.os.Process
import android.os.SystemClock
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.util.Log
import androidx.core.app.ServiceCompat
import kotlin.math.abs
import kotlin.math.sqrt

/**
 * A VR session: reads the accelerometer and gyroscope at 200 Hz on a thread of its own, recognizes
 * double pinches (DoublePinchDetector) and sends each to the phone, with a tick on the wrist. A
 * foreground service with a partial wake lock, so it keeps going with the screen off. It sends the
 * phone a heartbeat every [HEARTBEAT_MS], which the phone answers; with no answer for [IDLE_STOP_MS]
 * it stops itself, so a forgotten session doesn't drain the battery.
 */
class PinchService : Service(), SensorEventListener {
    companion object {
        private const val TAG = "CardboardXRPinch"
        private const val CHANNEL = "session"
        private const val NOTIFICATION_ID = 1
        private const val SAMPLING_US = 5_000
        private const val HEARTBEAT_MS = 2_000L
        private const val IDLE_STOP_MS = 10 * 60_000L
        private const val WAKE_LOCK_MS = 4 * 60 * 60_000L
        private const val GYRO_SEND_INTERVAL_NS = 16_000_000L
        private const val GYRO_DEADZONE_RAD_S = 0.022f

        fun start(context: Context) {
            context.startForegroundService(Intent(context, PinchService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, PinchService::class.java))
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private val detector = DoublePinchDetector()
    private lateinit var sensors: SensorManager
    private lateinit var link: PhoneLink
    private var sensorThread: HandlerThread? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var startedMs = 0L

    // Gravity-aligned gyro integration for steering the VR cursor & moving grabbed windows with the wrist.
    private val grav = floatArrayOf(0f, 0f, 9.81f)
    private var hasGrav = false
    private var lastPointerGyroNs = 0L
    private var lastGyroSendNs = 0L
    private var pointerYawRad = 0f
    private var pointerPitchRad = 0f
    private var loggedGyroStream = false
    @Volatile private var gyroResetPending = false

    private val vibrator: Vibrator by lazy {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S)
            getSystemService(VibratorManager::class.java).defaultVibrator
        else
            @Suppress("DEPRECATION") getSystemService(Vibrator::class.java)
    }

    private val heartbeat = object : Runnable {
        override fun run() {
            link.refreshNodes()
            link.sendHeartbeat()
            val now = SystemClock.elapsedRealtime()
            if (now - maxOf(Session.lastPhoneMs, startedMs) > IDLE_STOP_MS) {
                Log.i(TAG, "No phone for ${IDLE_STOP_MS / 60_000} minutes: stopping")
                stopSelf()
                return
            }
            main.postDelayed(this, HEARTBEAT_MS)
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        startInForeground()
        startedMs = SystemClock.elapsedRealtime()
        detector.sensitivity = Prefs.sensitivity(this)
        Session.singlePinchMode = Prefs.singlePinchMode(this)
        detector.singlePinchMode = Session.singlePinchMode

        link = PhoneLink(this)
        link.start()

        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "CardboardXR:pinch")
            .apply { acquire(WAKE_LOCK_MS) }

        sensors = getSystemService(SensorManager::class.java)
        val thread = HandlerThread("pinch-sensors", Process.THREAD_PRIORITY_URGENT_DISPLAY).also { it.start() }
        sensorThread = thread
        val handler = Handler(thread.looper)
        val accelerometer = sensors.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        val gyroscope = sensors.getDefaultSensor(Sensor.TYPE_GYROSCOPE)
        if (accelerometer == null)
            Log.e(TAG, "No accelerometer")
        accelerometer?.let { sensors.registerListener(this, it, SAMPLING_US, 0, handler) }
        gyroscope?.let { sensors.registerListener(this, it, SAMPLING_US, 0, handler) }

        Session.pinches.set(0)
        Session.lastPhoneMs = Long.MIN_VALUE / 2
        Session.detector = detector
        Session.link = link
        Session.setGrabbing(false)
        Session.onGrabChanged = { active ->
            gyroResetPending = true
            // Blank accelerometer BEFORE vibrating so haptic motor shock never cascades into phantom pinches.
            if (active) {
                Session.blankPinchesFor(750L)
                vibrator.vibrate(VibrationEffect.createOneShot(28L, 120))
            } else {
                Session.blankPinchesFor(550L)
                vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
            }
        }
        Session.running = true
        main.post(heartbeat)
        Log.i(TAG, "Session started")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY

    override fun onDestroy() {
        main.removeCallbacks(heartbeat)
        sensors.unregisterListener(this)
        sensorThread?.quitSafely()
        sensorThread = null
        link.stop()
        wakeLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
        Session.onGrabChanged = null
        Session.setGrabbing(false)
        Session.running = false
        Session.detector = null
        Session.link = null
        Log.i(TAG, "Session stopped")
        super.onDestroy()
    }

    override fun onSensorChanged(event: SensorEvent) {
        val v = event.values
        val type = event.sensor.type

        if (type == Sensor.TYPE_ACCELEROMETER) {
            if (!hasGrav) {
                grav[0] = v[0]; grav[1] = v[1]; grav[2] = v[2]
                hasGrav = true
            } else {
                constAlphaGravity(v)
            }
        }

        // Stream wrist gyro continuously whenever connected to the phone so the user can steer the
        // VR cursor directly with the watch (and move grabbed windows smoothly with the wrist).
        if (Session.phoneConnected && type == Sensor.TYPE_GYROSCOPE) {
            handlePointerGyro(v[0], v[1], v[2], event.timestamp)
        } else if (!Session.phoneConnected || gyroResetPending) {
            if (!Session.phoneConnected) {
                lastPointerGyroNs = 0L
                pointerYawRad = 0f
                pointerPitchRad = 0f
            } else if (gyroResetPending) {
                gyroResetPending = false
                lastPointerGyroNs = 0L
                pointerYawRad = 0f
                pointerPitchRad = 0f
            }
        }

        // In Double-Pinch mode on a Pixel Watch, the native system GestureInputManager handles
        // recognition via the hardware gesture sensor. In Single-Pinch mode (or on watches without
        // GestureInputManager), the IMU detector runs.
        val useImu = Session.singlePinchMode || !Session.systemGestureActive
        if (!useImu) return
        detector.singlePinchMode = Session.singlePinchMode
        when (type) {
            Sensor.TYPE_GYROSCOPE -> detector.onGyro(v[0], v[1], v[2], event.timestamp)
            Sensor.TYPE_ACCELEROMETER -> if (detector.onAccel(v[0], v[1], v[2], event.timestamp)) onDoublePinch()
        }
    }

    private fun constAlphaGravity(v: FloatArray) {
        val a = 0.08f
        grav[0] += (v[0] - grav[0]) * a
        grav[1] += (v[1] - grav[1]) * a
        grav[2] += (v[2] - grav[2]) * a
    }

    private fun handlePointerGyro(wx: Float, wy: Float, wz: Float, tsNs: Long) {
        if (gyroResetPending || lastPointerGyroNs == 0L) {
            gyroResetPending = false
            lastPointerGyroNs = tsNs
            lastGyroSendNs = tsNs
            pointerYawRad = 0f
            pointerPitchRad = 0f
            return
        }
        val dt = ((tsNs - lastPointerGyroNs) / 1e9f).coerceIn(0f, 0.05f)
        lastPointerGyroNs = tsNs
        if (dt <= 0f) return

        // Gravity unit vector (points UP in Android sensor frame).
        val gNorm = sqrt(grav[0] * grav[0] + grav[1] * grav[1] + grav[2] * grav[2])
        val gx: Float; val gy: Float; val gz: Float
        if (gNorm > 1f) {
            gx = grav[0] / gNorm; gy = grav[1] / gNorm; gz = grav[2] / gNorm
        } else {
            gx = 0f; gy = 0f; gz = 1f
        }

        // Horizontal pan around vertical gravity axis (+yaw = turning right).
        var wYaw = -(wx * gx + wy * gy + wz * gz)

        // Vertical pitch around horizontal axis perpendicular to forearm (+X) and gravity:
        // cross(g_up, (+1, 0, 0)) = (0, gz, -gy).
        val pNorm = sqrt(gz * gz + gy * gy)
        val py: Float; val pz: Float
        if (pNorm >= 0.25f) {
            py = gz / pNorm; pz = -gy / pNorm
        } else {
            py = 1f; pz = 0f
        }
        var wPitch = (wy * py + wz * pz)

        if (abs(wYaw) < GYRO_DEADZONE_RAD_S) wYaw = 0f
        if (abs(wPitch) < GYRO_DEADZONE_RAD_S) wPitch = 0f

        pointerYawRad += wYaw * dt
        pointerPitchRad += wPitch * dt

        if (tsNs - lastGyroSendNs >= GYRO_SEND_INTERVAL_NS) {
            lastGyroSendNs = tsNs
            if (abs(pointerYawRad) > 0.0005f || abs(pointerPitchRad) > 0.0005f) {
                if (!loggedGyroStream) {
                    loggedGyroStream = true
                    Log.i(TAG, "Streaming watch gyro pointer to phone")
                }
                link.sendGyro(
                    (pointerYawRad * 1_000_000f).toInt(),
                    (pointerPitchRad * 1_000_000f).toInt()
                )
                pointerYawRad = 0f
                pointerPitchRad = 0f
            }
        }
    }

    override fun onAccuracyChanged(sensor: Sensor, accuracy: Int) = Unit

    private fun onDoublePinch() {
        if (Session.sendPinch()) {
            Session.blankPinchesFor(420L)
            vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
        }
    }

    private fun startInForeground() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW))
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(getString(R.string.notification_text))
            .setContentIntent(open)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, notification)
            return
        }
        try {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_HEALTH)
        } catch (e: RuntimeException) {
            Log.w(TAG, "The health service type was refused ($e): using special use")
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        }
    }
}
