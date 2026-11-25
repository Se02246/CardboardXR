package com.brenpoly.cardboardxr.wear

import org.junit.Assert.assertEquals
import org.junit.Test
import kotlin.math.PI
import kotlin.math.exp
import kotlin.math.sin

class DoublePinchDetectorTest {
    private val stepNs = 5_000_000L // 200 Hz

    // A pinch's jolt: a 30 Hz ring dying out in about 30 ms, on top of gravity.
    private fun jolt(t: Double, at: Double, size: Double): Double {
        val dt = t - at
        return if (dt < 0) 0.0 else size * exp(-dt / 0.01) * sin(2 * PI * 30 * dt)
    }

    // Runs seconds of samples with jolts at the given times; returns how many pinches.
    private fun run(
        seconds: Double,
        jolts: List<Double>,
        size: Double = 3.0,
        turning: Boolean = false,
        singleMode: Boolean = false
    ): Int {
        val detector = DoublePinchDetector()
        detector.singlePinchMode = singleMode
        var found = 0
        var timeNs = 1_000_000_000L
        val samples = (seconds * 200).toInt()
        for (i in 0 until samples) {
            val t = i * 0.005
            val x = jolts.sumOf { jolt(t, it, size) }.toFloat()
            detector.onGyro(if (turning) 2f else 0.05f, 0f, 0f, timeNs)
            if (detector.onAccel(x, 0f, 9.81f, timeNs))
                found++
            timeNs += stepNs
        }
        return found
    }

    @Test fun singleJoltInSingleModeNeedsDeliberatePinch() = assertEquals(1, run(1.5, listOf(0.5), size = 7.0, singleMode = true))

    @Test fun twoConsecutiveSinglePinchesBothRegister() = assertEquals(2, run(2.2, listOf(0.5, 1.2), size = 7.0, singleMode = true))

    @Test fun weakJoltInSingleModeIsIgnored() = assertEquals(0, run(1.5, listOf(0.5), size = 2.0, singleMode = true))

    @Test fun turningBlocksSingleModeToo() = assertEquals(0, run(1.5, listOf(0.5), size = 7.0, turning = true, singleMode = true))

    @Test fun vibrationSuppressionBlocksPhantomPinches() {
        val detector = DoublePinchDetector()
        var found = 0
        var timeNs = 1_000_000_000L
        val samples = (2.2 * 200).toInt()
        for (i in 0 until samples) {
            val t = i * 0.005
            // Real double pinch at 0.30s + 0.50s -> confirms around 0.72s, then vibration bursts at 0.78s & 0.90s.
            val x = (jolt(t, 0.30, 3.5) + jolt(t, 0.50, 3.5) + jolt(t, 0.78, 5.0) + jolt(t, 0.90, 5.0)).toFloat()
            detector.onGyro(0.05f, 0f, 0f, timeNs)
            if (detector.onAccel(x, 0f, 9.81f, timeNs)) {
                found++
                detector.suppressForMs(750L)
            }
            timeNs += stepNs
        }
        assertEquals(1, found)
    }

    @Test fun rapidReboundInSingleModeDoesNotDoubleFire() =
        assertEquals(1, run(1.5, listOf(0.50, 0.65), size = 7.0, singleMode = true))

    @Test fun twoJoltsAreADoublePinch() = assertEquals(1, run(2.0, listOf(0.5, 0.75)))

    @Test fun oneJoltIsNot() = assertEquals(0, run(2.0, listOf(0.5)))

    @Test fun threeJoltsAreNot() = assertEquals(0, run(2.0, listOf(0.5, 0.7, 0.9)))

    @Test fun joltsTooFarApartAreNot() = assertEquals(0, run(2.0, listOf(0.5, 1.2)))

    @Test fun nothingCountsWhileTheWristTurns() = assertEquals(0, run(2.0, listOf(0.5, 0.75), turning = true))

    @Test fun aKnockIsNotAPinch() = assertEquals(0, run(2.0, listOf(0.5, 0.75), size = 30.0))

    @Test fun twoDoublePinchesInARow() = assertEquals(2, run(3.0, listOf(0.5, 0.75, 1.8, 2.05)))

    @Test fun theThresholdFollowsTheSensitivity() {
        assertEquals(3f, DoublePinchDetector.thresholdFor(0f), 1e-4f)
        assertEquals(0.3f, DoublePinchDetector.thresholdFor(1f), 1e-4f)
    }
}
