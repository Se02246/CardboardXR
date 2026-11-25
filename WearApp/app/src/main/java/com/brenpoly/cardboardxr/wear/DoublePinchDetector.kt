package com.brenpoly.cardboardxr.wear

import java.util.concurrent.atomic.AtomicLong
import kotlin.math.PI
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.pow
import kotlin.math.sqrt

/**
 * Recognizes a double pinch (or single pinch in 1x mode) of thumb and index finger from the
 * watch's accelerometer, with the gyroscope and haptic-motor blanking as vetoes.
 */
class DoublePinchDetector {
    companion object {
        const val HIGH_PASS_HZ = 8f
        const val REFRACTORY_NS = 120_000_000L
        const val MIN_GAP_NS = 100_000_000L
        const val MAX_GAP_NS = 550_000_000L
        const val QUIET_BEFORE_NS = 250_000_000L
        const val SINGLE_QUIET_BEFORE_NS = 200_000_000L
        const val CONFIRM_NS = 220_000_000L
        const val SINGLE_CONFIRM_NS = 120_000_000L
        const val COOLDOWN_NS = 700_000_000L
        const val SINGLE_COOLDOWN_NS = 420_000_000L
        const val GYRO_VETO_RAD_S = 1.2f
        const val SINGLE_GYRO_VETO_RAD_S = 0.85f
        // Single-pinch mode threshold (~2.4 m/s^2 at default sensitivity): catches deliberate pinches
        // reliably even in quick succession while filtering out light finger/wrist noise.
        const val SINGLE_THRESHOLD_SCALE = 2.8f
        const val GYRO_SMOOTH_SECONDS = 0.08f
        const val VETO_NS = 250_000_000L
        // A jolt this many times the threshold (and at least KNOCK_MIN) is a knock, not a pinch.
        const val KNOCK_FACTOR = 4f
        const val KNOCK_MIN = 4f
        // The jolt ends once the magnitude falls below this share of the threshold.
        const val RELEASE_SHARE = 0.6f
        // How quickly the meter falls back, in seconds.
        const val METER_SECONDS = 0.3f
        // A longer gap between samples restarts the filter.
        const val MAX_SAMPLE_GAP_S = 0.1f

        /** The jolt threshold, in m/s^2, for a sensitivity from 0 (least) to 1 (most): 3 down to 0.3. */
        fun thresholdFor(sensitivity: Float): Float =
            3f * 0.1f.pow(sensitivity.coerceIn(0f, 1f))
    }

    @Volatile var sensitivity = 0.5f

    /** When true, a single isolated pinch jolt (with steady wrist) triggers a click after [SINGLE_CONFIRM_NS]. */
    @Volatile var singlePinchMode = false

    /** The recent jolt size, in m/s^2, for the screen's meter; it falls back over [METER_SECONDS]. */
    @Volatile var level = 0f
        private set

    /** The current threshold, in m/s^2. */
    val threshold: Float
        get() {
            val base = thresholdFor(sensitivity)
            return if (singlePinchMode) base * SINGLE_THRESHOLD_SCALE else base
        }

    private val previous = FloatArray(3)
    private val filtered = FloatArray(3)
    private var lastSampleNs = 0L
    private var hasSample = false

    private var lastGyroNs = 0L
    private var gyro = 0f

    private var vetoUntilNs = Long.MIN_VALUE
    private var cooldownUntilNs = Long.MIN_VALUE
    private var suppressUntilSensorNs = Long.MIN_VALUE
    private val pendingSuppressNs = AtomicLong(0L)
    private var above = false
    private var lastJoltNs = Long.MIN_VALUE / 2
    private var jolts = 0
    private var firstNs = 0L
    private var secondNs = 0L

    /**
     * Thread-safe blanking window (e.g. during haptic feedback vibrations) consumed on the next
     * accelerometer sample using the sensor's own monotonic clock.
     */
    fun suppressForMs(durationMs: Long) {
        if (durationMs <= 0L) return
        val durationNs = durationMs * 1_000_000L
        while (true) {
            val cur = pendingSuppressNs.get()
            val next = max(cur, durationNs)
            if (pendingSuppressNs.compareAndSet(cur, next))
                break
        }
    }

    fun reset() {
        hasSample = false
        filtered.fill(0f)
        lastGyroNs = 0L
        gyro = 0f
        vetoUntilNs = Long.MIN_VALUE
        cooldownUntilNs = Long.MIN_VALUE
        suppressUntilSensorNs = Long.MIN_VALUE
        pendingSuppressNs.set(0L)
        above = false
        lastJoltNs = Long.MIN_VALUE / 2
        jolts = 0
        level = 0f
    }

    /** A gyroscope sample, in rad/s, at a sensor timestamp in nanoseconds. */
    fun onGyro(x: Float, y: Float, z: Float, timeNs: Long) {
        val speed = sqrt(x * x + y * y + z * z)
        if (lastGyroNs == 0L) {
            gyro = speed
        } else {
            val dt = ((timeNs - lastGyroNs) / 1e9f).coerceIn(0f, MAX_SAMPLE_GAP_S)
            gyro += (speed - gyro) * (1f - exp(-dt / GYRO_SMOOTH_SECONDS))
        }
        lastGyroNs = timeNs
        val vetoLimit = if (singlePinchMode) SINGLE_GYRO_VETO_RAD_S else GYRO_VETO_RAD_S
        if (gyro > vetoLimit) {
            vetoUntilNs = timeNs + VETO_NS
            jolts = 0
        }
    }

    /**
     * An accelerometer sample, in m/s^2, at a sensor timestamp in nanoseconds. True when it confirms
     * a pinch.
     */
    fun onAccel(x: Float, y: Float, z: Float, timeNs: Long): Boolean {
        val pending = pendingSuppressNs.getAndSet(0L)
        if (pending > 0L) {
            suppressUntilSensorNs = max(suppressUntilSensorNs, timeNs + pending)
            jolts = 0
            above = false
        }

        val dt = (timeNs - lastSampleNs) / 1e9f
        if (!hasSample || dt <= 0f || dt > MAX_SAMPLE_GAP_S) {
            previous[0] = x; previous[1] = y; previous[2] = z
            filtered.fill(0f)
            lastSampleNs = timeNs
            hasSample = true
            return false
        }
        lastSampleNs = timeNs

        // First-order high-pass on each axis.
        val rc = 1f / (2f * PI.toFloat() * HIGH_PASS_HZ)
        val a = rc / (rc + dt)
        filtered[0] = a * (filtered[0] + x - previous[0])
        filtered[1] = a * (filtered[1] + y - previous[1])
        filtered[2] = a * (filtered[2] + z - previous[2])
        previous[0] = x; previous[1] = y; previous[2] = z

        if (timeNs < suppressUntilSensorNs) {
            jolts = 0
            above = false
            lastJoltNs = timeNs
            level *= exp(-dt / METER_SECONDS)
            return false
        }

        val magnitude = sqrt(filtered[0] * filtered[0] + filtered[1] * filtered[1] + filtered[2] * filtered[2])
        level = max(magnitude, level * exp(-dt / METER_SECONDS))

        var confirmed = false
        if (singlePinchMode) {
            if (jolts == 1 && timeNs - firstNs >= SINGLE_CONFIRM_NS) {
                jolts = 0
                cooldownUntilNs = timeNs + SINGLE_COOLDOWN_NS
                confirmed = true
            }
        } else if (jolts == 2 && timeNs - secondNs >= CONFIRM_NS) {
            jolts = 0
            cooldownUntilNs = timeNs + COOLDOWN_NS
            confirmed = true
        } else if (jolts == 1 && timeNs - firstNs > MAX_GAP_NS) {
            jolts = 0
        }

        val threshold = threshold
        if (!above && magnitude > threshold) {
            above = true
            if (timeNs - lastJoltNs >= REFRACTORY_NS)
                onJolt(timeNs, magnitude > max(threshold * KNOCK_FACTOR, KNOCK_MIN))
        } else if (above && magnitude < threshold * RELEASE_SHARE) {
            above = false
        }
        return confirmed
    }

    private fun onJolt(timeNs: Long, knock: Boolean) {
        val sincePrevious = timeNs - lastJoltNs
        lastJoltNs = timeNs
        if (knock) {
            jolts = 0
            vetoUntilNs = max(vetoUntilNs, timeNs + VETO_NS)
            return
        }
        if (timeNs < vetoUntilNs || timeNs < cooldownUntilNs) {
            jolts = 0
            return
        }
        val quietRequired = if (singlePinchMode) SINGLE_QUIET_BEFORE_NS else QUIET_BEFORE_NS
        when (jolts) {
            0 -> if (sincePrevious >= quietRequired) {
                jolts = 1
                firstNs = timeNs
            }
            1 -> {
                val gap = timeNs - firstNs
                if (singlePinchMode) {
                    // In single-pinch mode, a second distinct jolt before confirmation cancels the train.
                    if (gap >= MIN_GAP_NS) jolts = 0
                } else {
                    when {
                        gap < MIN_GAP_NS -> Unit // the first one still ringing
                        gap <= MAX_GAP_NS -> {
                            jolts = 2
                            secondNs = timeNs
                        }
                        else -> jolts = 0
                    }
                }
            }
            // A third: a train of jolts, not a double pinch.
            else -> jolts = 0
        }
    }
}
