package com.brenpoly.cardboardxr.wear

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.SystemClock
import android.util.Log
import android.view.Window
import java.util.concurrent.Executor
import java.util.function.Consumer

/**
 * Connects to the Pixel Watch's native system Double Pinch recognizer (`IWearGestureService` via
 * `/system/framework/wear-sdk.jar`: `com.google.wear.Sdk` and `com.google.wear.input.GestureInputManager`).
 *
 * All methods called here are marked `SDK` (0x0020) in the Pixel Watch bootclasspath (`wear-sdk.jar`)
 * and require no special permission for foreground/ambient window subscriptions.
 */
class SystemGestureDetector(
    private val activity: Activity,
    private val onDoublePinch: () -> Unit
) {
    companion object {
        private const val TAG = "CardboardXRGesture"
        private const val FEATURE_GESTURE_DETECTION = "com.google.wear.feature.GESTURE_DETECTION"
        private const val ACTION_GESTURE_SETTINGS = "com.google.wear.input.action.GESTURE_INPUT_SETTINGS"
        private const val MIN_GESTURE_GAP_MS = 550L
        const val ACTION_PRIMARY = 1
        const val GESTURE_DOUBLE_PINCH = 1
    }

    private var gestureManager: Any? = null
    private var eventConsumer: Consumer<Any>? = null
    private var lastGestureMs = 0L

    @Volatile var isSupported = false
        private set

    @Volatile var isEnabledInSystem = false
        private set

    @Volatile var isSubscribed = false
        private set

    fun attach() {
        if (isSubscribed) return
        try {
            val sdkClass = Class.forName("com.google.wear.Sdk")
            val managerClass = Class.forName("com.google.wear.input.GestureInputManager")
            val paramsBuilderClass = Class.forName("com.google.wear.input.ForegroundGestureSubscriptionParams\$Builder")
            val paramsClass = Class.forName("com.google.wear.input.ForegroundGestureSubscriptionParams")
            val eventClass = Class.forName("com.google.wear.input.GestureEvent")

            val hasFeature = sdkClass
                .getMethod("hasApiFeature", String::class.java)
                .invoke(null, FEATURE_GESTURE_DETECTION) as? Boolean ?: false

            val manager = sdkClass
                .getMethod("getWearManager", Context::class.java, Class::class.java)
                .invoke(null, activity, managerClass)

            if (manager == null) {
                Log.w(TAG, "GestureInputManager not available on this watch")
                return
            }
            gestureManager = manager

            val supported = managerClass
                .getMethod("isActionSupported", Int::class.javaPrimitiveType)
                .invoke(manager, ACTION_PRIMARY) as? Boolean ?: hasFeature
            val enabled = managerClass
                .getMethod("isActionEnabled", Int::class.javaPrimitiveType)
                .invoke(manager, ACTION_PRIMARY) as? Boolean ?: true

            isSupported = supported
            isEnabledInSystem = enabled

            // Build ForegroundGestureSubscriptionParams for ACTION_PRIMARY (Double Pinch) with ambient support.
            val builder = paramsBuilderClass
                .getConstructor(IntArray::class.java, Window::class.java)
                .newInstance(intArrayOf(ACTION_PRIMARY), activity.window)
            paramsBuilderClass
                .getMethod("setAmbientSupported", Boolean::class.javaPrimitiveType)
                .invoke(builder, true)
            val params = paramsBuilderClass.getMethod("build").invoke(builder)

            val getActionMethod = eventClass.getMethod("getAction")
            val getGestureMethod = eventClass.getMethod("getGesture")
            val notifyConsumedMethod = managerClass.getMethod(
                "notifyGestureConsumed",
                String::class.java,
                Int::class.javaPrimitiveType
            )

            val consumer = Consumer<Any> { event ->
                try {
                    val action = getActionMethod.invoke(event) as? Int ?: 0
                    val gesture = getGestureMethod.invoke(event) as? Int ?: 0
                    Log.i(TAG, "System gesture received: action=$action gesture=$gesture")
                    if (action == ACTION_PRIMARY || gesture == GESTURE_DOUBLE_PINCH) {
                        try {
                            notifyConsumedMethod.invoke(manager, "CardboardXR", ACTION_PRIMARY)
                        } catch (_: Throwable) {
                        }
                        if (Session.running) {
                            val now = SystemClock.elapsedRealtime()
                            if (now - lastGestureMs >= MIN_GESTURE_GAP_MS) {
                                lastGestureMs = now
                                onDoublePinch()
                            } else {
                                Log.d(TAG, "Ignored duplicate system gesture within ${now - lastGestureMs} ms")
                            }
                        }
                    }
                } catch (e: Throwable) {
                    Log.w(TAG, "Error handling system gesture event", e)
                }
            }
            eventConsumer = consumer

            managerClass
                .getMethod(
                    "addGestureEventListener",
                    paramsClass,
                    Executor::class.java,
                    Consumer::class.java
                )
                .invoke(manager, params, activity.mainExecutor, consumer)

            isSubscribed = true
            Session.systemGestureActive = true
            Log.i(TAG, "Subscribed to Pixel Watch system Double Pinch (supported=$supported, enabled=$enabled)")
        } catch (e: Throwable) {
            Log.w(TAG, "System GestureInputManager unavailable: ${e.message}")
            isSupported = false
            isSubscribed = false
            Session.systemGestureActive = false
        }
    }

    fun refreshEnabledState() {
        val manager = gestureManager ?: return
        try {
            val enabled = manager.javaClass
                .getMethod("isActionEnabled", Int::class.javaPrimitiveType)
                .invoke(manager, ACTION_PRIMARY) as? Boolean ?: return
            isEnabledInSystem = enabled
        } catch (_: Throwable) {
        }
    }

    fun detach() {
        val manager = gestureManager
        val consumer = eventConsumer
        gestureManager = null
        eventConsumer = null
        isSubscribed = false
        Session.systemGestureActive = false
        if (manager != null && consumer != null) {
            try {
                manager.javaClass
                    .getMethod("removeGestureEventListener", Consumer::class.java)
                    .invoke(manager, consumer)
            } catch (_: Throwable) {
            }
        }
    }

    fun openSystemGestureSettings() {
        try {
            activity.startActivity(Intent(ACTION_GESTURE_SETTINGS))
        } catch (_: Throwable) {
        }
    }
}
