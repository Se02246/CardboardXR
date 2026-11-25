package com.brenpoly.cardboardxr.wear

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.WindowManager
import kotlin.math.abs

/**
 * Pixel Watch companion activity for CardboardXR.
 *
 * Lets the user choose between:
 *  - DOPPIO PINCH (Modo: 2×): uses the Pixel Watch's native system Double Pinch service
 *    (`com.google.wear.input.GestureInputManager` via [SystemGestureDetector]).
 *  - PINCH SINGOLO (Modo: 1×): uses a fast single-pinch detector on the wrist and also keeps
 *    the phone's single-pinch active.
 */
class MainActivity : Activity() {
    companion object {
        private const val TICK_MS = 120L
    }

    private val handler = Handler(Looper.getMainLooper())
    private lateinit var dashboard: WatchDashboardView
    private lateinit var systemGesture: SystemGestureDetector
    private var lastObservedPinchMs = 0L
    private var rotaryAccumulator = 0f

    private val vibrator: Vibrator by lazy {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S)
            getSystemService(VibratorManager::class.java).defaultVibrator
        else
            @Suppress("DEPRECATION") getSystemService(Vibrator::class.java)
    }

    private val tick = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, TICK_MS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        Session.singlePinchMode = Prefs.singlePinchMode(this)

        systemGesture = SystemGestureDetector(this) {
            triggerWatchClick()
        }

        dashboard = WatchDashboardView(
            context = this,
            onCenterClick = {
                if (!Session.running) {
                    PinchService.start(this)
                    updateKeepScreenOn(true)
                } else {
                    triggerWatchClick()
                }
            },
            onTogglePinchMode = {
                val nextSingle = !Session.singlePinchMode
                Session.singlePinchMode = nextSingle
                Prefs.setSinglePinchMode(this, nextSingle)
                Session.detector?.reset()
                Session.detector?.singlePinchMode = nextSingle
                Session.link?.sendHeartbeat()
                Session.blankPinchesFor(450L)
                vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
                refresh()
            },
            onSettingsClick = {
                if (Session.running) {
                    Session.blankPinchesFor(450L)
                    vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
                    Session.link?.send(PhoneLink.PATH_SETTINGS)
                }
            },
            onTopPillClick = {
                if (!Session.singlePinchMode && systemGesture.isSupported && !systemGesture.isEnabledInSystem) {
                    systemGesture.openSystemGestureSettings()
                } else {
                    Session.blankPinchesFor(450L)
                    vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
                    if (Session.running) {
                        PinchService.stop(this)
                        updateKeepScreenOn(false)
                    } else {
                        PinchService.start(this)
                        updateKeepScreenOn(true)
                    }
                    refresh()
                }
            }
        )
        setContentView(dashboard)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }

        if (!Session.running) {
            PinchService.start(this)
        }
        updateKeepScreenOn(true)
    }

    override fun onAttachedToWindow() {
        super.onAttachedToWindow()
        systemGesture.attach()
        refresh()
    }

    override fun onDetachedFromWindow() {
        systemGesture.detach()
        super.onDetachedFromWindow()
    }

    override fun onResume() {
        super.onResume()
        dashboard.requestFocus()
        systemGesture.attach()
        systemGesture.refreshEnabledState()
        updateKeepScreenOn(Session.running)
        handler.post(tick)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (Session.running &&
            event.action == MotionEvent.ACTION_SCROLL &&
            event.isFromSource(InputDevice.SOURCE_ROTARY_ENCODER)) {
            // Positive steps = crown rotated clockwise / down (scroll down or move window farther).
            // Negative steps = crown rotated counter-clockwise / up (scroll up or pull window closer).
            val delta = -event.getAxisValue(MotionEvent.AXIS_SCROLL)
            rotaryAccumulator += delta * 100f
            dashboard.rotaryAngleOffset = (dashboard.rotaryAngleOffset + delta * 18f) % 360f
            dashboard.invalidate()
            if (abs(rotaryAccumulator) >= 12f) {
                val steps = rotaryAccumulator.toInt()
                rotaryAccumulator -= steps
                Session.link?.sendRotary(steps)
            }
            return true
        }
        return super.dispatchGenericMotionEvent(event)
    }

    override fun onPause() {
        handler.removeCallbacks(tick)
        super.onPause()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (Session.running && (keyCode == KeyEvent.KEYCODE_STEM_1 ||
                keyCode == KeyEvent.KEYCODE_STEM_2 ||
                keyCode == KeyEvent.KEYCODE_ENTER ||
                keyCode == KeyEvent.KEYCODE_DPAD_CENTER)) {
            triggerWatchClick()
            return true
        }
        return super.onKeyDown(keyCode, event)
    }

    private fun triggerWatchClick() {
        if (Session.sendPinch()) {
            Session.blankPinchesFor(420L)
            lastObservedPinchMs = Session.lastPinchRealtimeMs
            vibrator.vibrate(VibrationEffect.createPredefined(VibrationEffect.EFFECT_TICK))
            dashboard.pinchCount = Session.pinches.get()
            dashboard.triggerPulse()
        }
    }

    private fun updateKeepScreenOn(keepOn: Boolean) {
        if (keepOn) {
            window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    private fun refresh() {
        val running = Session.running
        updateKeepScreenOn(running)
        if (Session.lastPinchRealtimeMs != lastObservedPinchMs && Session.lastPinchRealtimeMs > 0L) {
            lastObservedPinchMs = Session.lastPinchRealtimeMs
            dashboard.triggerPulse()
        }
        dashboard.isRunning = running
        dashboard.isPhoneConnected = Session.phoneConnected
        dashboard.isHandMode = Session.phoneMode == Session.MODE_HAND
        dashboard.isSinglePinchMode = Session.singlePinchMode
        dashboard.isGrabbing = Session.grabbing
        dashboard.isSystemGestureSupported = systemGesture.isSupported
        dashboard.isSystemGestureEnabled = systemGesture.isEnabledInSystem
        dashboard.pinchCount = Session.pinches.get()
        dashboard.invalidate()
    }
}
