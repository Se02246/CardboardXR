package com.brenpoly.cardboardxr.wear

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.view.MotionEvent
import android.view.View
import android.view.animation.DecelerateInterpolator
import kotlin.math.hypot
import kotlin.math.min

/**
 * Custom Wear OS Material 3 circular UI designed specifically for the round Pixel Watch display.
 * Lets the user switch with one tap between DOPPIO PINCH (native Pixel Watch system recognizer)
 * and PINCH SINGOLO (single pinch), trigger manual clicks, open the VR Menu, or pause the session.
 */
class WatchDashboardView(
    context: Context,
    private val onCenterClick: () -> Unit,
    private val onTogglePinchMode: () -> Unit,
    private val onSettingsClick: () -> Unit,
    private val onTopPillClick: () -> Unit
) : View(context) {

    var isRunning: Boolean = true
    var isPhoneConnected: Boolean = false
    var isHandMode: Boolean = false
    var isSinglePinchMode: Boolean = false
    var isGrabbing: Boolean = false
    var isSystemGestureSupported: Boolean = true
    var isSystemGestureEnabled: Boolean = true
    var pinchCount: Int = 0
    var rotaryAngleOffset: Float = 0f

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    private var pulseProgress: Float = 1f
    private var pressedZone: Int = ZONE_NONE

    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = Color.BLACK
    }

    private val ringTrackPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        color = Color.parseColor("#111827")
    }

    private val ringActivePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
    }

    private val pulsePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
    }

    private val cardFillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val cardStrokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
    }

    private val dotPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val iconPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    private val titlePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
        color = Color.WHITE
    }

    private val subtitlePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif", Typeface.NORMAL)
    }

    private val badgeTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textAlign = Paint.Align.CENTER
        typeface = Typeface.create("sans-serif-medium", Typeface.BOLD)
    }

    private val pillRectTop = RectF()
    private val pillRectLeft = RectF()
    private val pillRectRight = RectF()
    private val badgeRect = RectF()
    private val arcRect = RectF()
    private val iconPath = Path()

    private var centerX = 0f
    private var centerY = 0f
    private var heroCenterY = 0f
    private var heroRadius = 0f

    private val pulseAnimator = ValueAnimator.ofFloat(0f, 1f).apply {
        duration = 520L
        interpolator = DecelerateInterpolator(1.8f)
        addUpdateListener {
            pulseProgress = it.animatedValue as Float
            postInvalidateOnAnimation()
        }
    }

    fun triggerPulse() {
        if (pulseAnimator.isRunning) {
            pulseAnimator.cancel()
        }
        pulseProgress = 0f
        pulseAnimator.start()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val w = width.toFloat()
        val h = height.toFloat()
        val size = min(w, h)
        if (size <= 0f) return

        centerX = w * 0.5f
        centerY = h * 0.5f
        val radius = size * 0.5f

        // 1. Pure OLED black background
        canvas.drawRect(0f, 0f, w, h, bgPaint)

        // 2. Outer bezel track & status arc
        val bezelInset = size * 0.032f
        val bezelStroke = size * 0.022f
        arcRect.set(
            centerX - radius + bezelInset,
            centerY - radius + bezelInset,
            centerX + radius - bezelInset,
            centerY + radius - bezelInset
        )
        ringTrackPaint.strokeWidth = bezelStroke
        canvas.drawOval(arcRect, ringTrackPaint)

        val accentColor = when {
            !isRunning -> Color.parseColor("#475569")
            isGrabbing -> Color.parseColor("#F59E0B")
            isSinglePinchMode -> Color.parseColor("#34D399")
            !isSystemGestureEnabled && isSystemGestureSupported -> Color.parseColor("#F59E0B")
            isPhoneConnected && isHandMode -> Color.parseColor("#A855F7")
            isPhoneConnected -> Color.parseColor("#38BDF8")
            else -> Color.parseColor("#6366F1")
        }

        if (isRunning) {
            ringActivePaint.strokeWidth = bezelStroke
            ringActivePaint.color = accentColor
            ringActivePaint.alpha = if (isPhoneConnected) 215 else 120
            canvas.drawArc(arcRect, -130f + rotaryAngleOffset, 80f, false, ringActivePaint)
            canvas.drawArc(arcRect, 50f + rotaryAngleOffset, 80f, false, ringActivePaint)
        }

        // 3. Top status pill (tapping toggles Pause/Active or opens system gesture settings)
        val topPillW = size * 0.64f
        val topPillH = size * 0.125f
        val topPillY = centerY - radius * 0.72f
        pillRectTop.set(
            centerX - topPillW * 0.5f,
            topPillY - topPillH * 0.5f,
            centerX + topPillW * 0.5f,
            topPillY + topPillH * 0.5f
        )
        cardFillPaint.shader = null
        cardFillPaint.color = if (pressedZone == ZONE_TOP) Color.parseColor("#1E293B") else Color.parseColor("#0F172A")
        canvas.drawRoundRect(pillRectTop, topPillH * 0.5f, topPillH * 0.5f, cardFillPaint)

        cardStrokePaint.strokeWidth = size * 0.005f
        cardStrokePaint.color = Color.parseColor("#1E293B")
        canvas.drawRoundRect(pillRectTop, topPillH * 0.5f, topPillH * 0.5f, cardStrokePaint)

        val (dotColor, statusLabel) = when {
            !isRunning -> Color.parseColor("#64748B") to "PAUSA • TOCCA"
            isGrabbing -> Color.parseColor("#F59E0B") to "SPOSTA OGGETTO"
            !isSinglePinchMode && isSystemGestureSupported && !isSystemGestureEnabled ->
                Color.parseColor("#F59E0B") to "ATTIVA GESTO WATCH"
            !isPhoneConnected -> Color.parseColor("#38BDF8") to "ATTESA PIXEL 9…"
            isHandMode -> Color.parseColor("#C084FC") to "MANO AR + WATCH"
            else -> Color.parseColor("#4ADE80") to "PIXEL 9 • VR"
        }

        val dotRadius = size * 0.017f
        val dotX = pillRectTop.left + size * 0.065f
        dotPaint.color = dotColor
        canvas.drawCircle(dotX, topPillY, dotRadius, dotPaint)

        badgeTextPaint.textSize = size * 0.045f
        badgeTextPaint.color = Color.parseColor("#E2E8F0")
        val textOffsetY = (badgeTextPaint.descent() + badgeTextPaint.ascent()) * 0.5f
        canvas.drawText(statusLabel, centerX + size * 0.018f, topPillY - textOffsetY, badgeTextPaint)

        // 4. Center Hero Circle (Double Pinch or Single Pinch)
        heroCenterY = centerY - size * 0.03f
        heroRadius = size * 0.275f

        if (pulseProgress < 1f) {
            val expandRadius = heroRadius + (size * 0.16f) * pulseProgress
            pulsePaint.color = accentColor
            pulsePaint.strokeWidth = size * 0.025f * (1f - pulseProgress)
            pulsePaint.alpha = ((1f - pulseProgress) * 230f).toInt().coerceIn(0, 255)
            canvas.drawCircle(centerX, heroCenterY, expandRadius, pulsePaint)
        }

        val heroPressed = pressedZone == ZONE_HERO || pulseProgress < 0.25f
        cardFillPaint.shader = LinearGradient(
            centerX,
            heroCenterY - heroRadius,
            centerX,
            heroCenterY + heroRadius,
            if (heroPressed) Color.parseColor("#1E3A8A") else Color.parseColor("#111827"),
            if (heroPressed) Color.parseColor("#0F172A") else Color.parseColor("#090D16"),
            Shader.TileMode.CLAMP
        )
        canvas.drawCircle(centerX, heroCenterY, heroRadius, cardFillPaint)
        cardFillPaint.shader = null

        cardStrokePaint.strokeWidth = size * (if (heroPressed) 0.013f else 0.009f)
        cardStrokePaint.color = accentColor
        cardStrokePaint.alpha = if (isRunning) 220 else 90
        canvas.drawCircle(centerX, heroCenterY, heroRadius, cardStrokePaint)

        drawPinchIcon(canvas, centerX, heroCenterY - heroRadius * 0.36f, size * 0.075f, accentColor, isSinglePinchMode)

        titlePaint.textSize = size * 0.062f
        titlePaint.color = if (isRunning) Color.WHITE else Color.parseColor("#94A3B8")
        val heroTitle = when {
            !isRunning -> "IN PAUSA"
            isGrabbing -> "AGGANCIO ATTIVO"
            isSinglePinchMode -> "PINCH SINGOLO"
            else -> "DOPPIO PINCH"
        }
        canvas.drawText(heroTitle, centerX, heroCenterY + heroRadius * 0.08f, titlePaint)

        subtitlePaint.textSize = size * 0.043f
        subtitlePaint.color = Color.parseColor("#94A3B8")
        val subText = when {
            !isRunning -> "Tocca qui per riattivare"
            isGrabbing -> "Muovi polso • Pinch molla"
            isSinglePinchMode -> "1 Pizzico • Tocca per Click"
            isSystemGestureSupported -> "Sistema Watch • Tocca"
            else -> "2 Pizzichi • Tocca per Click"
        }
        canvas.drawText(subText, centerX, heroCenterY + heroRadius * 0.29f, subtitlePaint)

        // Counter pill inside bottom of hero circle
        val countBadgeW = size * 0.26f
        val countBadgeH = size * 0.085f
        val countBadgeY = heroCenterY + heroRadius * 0.60f
        badgeRect.set(
            centerX - countBadgeW * 0.5f,
            countBadgeY - countBadgeH * 0.5f,
            centerX + countBadgeW * 0.5f,
            countBadgeY + countBadgeH * 0.5f
        )
        cardFillPaint.color = Color.parseColor("#1E293B")
        canvas.drawRoundRect(badgeRect, countBadgeH * 0.5f, countBadgeH * 0.5f, cardFillPaint)

        badgeTextPaint.textSize = size * 0.043f
        badgeTextPaint.color = accentColor
        val badgeOffset = (badgeTextPaint.descent() + badgeTextPaint.ascent()) * 0.5f
        canvas.drawText("$pinchCount CLICK", centerX, countBadgeY - badgeOffset, badgeTextPaint)

        // 5. Bottom action capsules: Left = Mode Toggle (Singolo 1x / Doppio 2x), Right = Menu VR
        val btnW = size * 0.35f
        val btnH = size * 0.145f
        val btnGap = size * 0.032f
        val btnY = centerY + radius * 0.58f

        pillRectLeft.set(
            centerX - btnGap * 0.5f - btnW,
            btnY - btnH * 0.5f,
            centerX - btnGap * 0.5f,
            btnY + btnH * 0.5f
        )
        pillRectRight.set(
            centerX + btnGap * 0.5f,
            btnY - btnH * 0.5f,
            centerX + btnGap * 0.5f + btnW,
            btnY + btnH * 0.5f
        )

        // Left button: Mode switcher (Singolo 1x vs Doppio 2x)
        cardFillPaint.color = when {
            pressedZone == ZONE_LEFT -> Color.parseColor("#334155")
            isSinglePinchMode -> Color.parseColor("#064E3B")
            else -> Color.parseColor("#1E293B")
        }
        canvas.drawRoundRect(pillRectLeft, btnH * 0.5f, btnH * 0.5f, cardFillPaint)
        badgeTextPaint.textSize = size * 0.046f
        badgeTextPaint.color = if (isSinglePinchMode) Color.parseColor("#6EE7B7") else Color.parseColor("#38BDF8")
        val btnTextOffset = (badgeTextPaint.descent() + badgeTextPaint.ascent()) * 0.5f
        canvas.drawText(
            if (isSinglePinchMode) "Modo: 1×" else "Modo: 2×",
            pillRectLeft.centerX(),
            pillRectLeft.centerY() - btnTextOffset,
            badgeTextPaint
        )

        // Right button: Menu VR
        cardFillPaint.color = if (pressedZone == ZONE_RIGHT) Color.parseColor("#334155") else Color.parseColor("#1E293B")
        canvas.drawRoundRect(pillRectRight, btnH * 0.5f, btnH * 0.5f, cardFillPaint)
        badgeTextPaint.color = if (isRunning) Color.WHITE else Color.parseColor("#64748B")
        canvas.drawText(
            "Menu VR",
            pillRectRight.centerX(),
            pillRectRight.centerY() - btnTextOffset,
            badgeTextPaint
        )
    }

    private fun drawPinchIcon(canvas: Canvas, cx: Float, cy: Float, r: Float, color: Int, singleMode: Boolean) {
        iconPaint.color = color
        iconPaint.strokeWidth = r * 0.16f

        iconPath.reset()
        iconPath.moveTo(cx - r * 0.75f, cy + r * 0.45f)
        iconPath.quadTo(cx - r * 0.35f, cy - r * 0.1f, cx - r * 0.06f, cy + r * 0.05f)
        iconPath.moveTo(cx + r * 0.75f, cy - r * 0.45f)
        iconPath.quadTo(cx + r * 0.35f, cy - r * 0.25f, cx + r * 0.06f, cy - r * 0.05f)
        canvas.drawPath(iconPath, iconPaint)

        dotPaint.color = Color.WHITE
        canvas.drawCircle(cx, cy, r * 0.16f, dotPaint)

        iconPaint.strokeWidth = r * 0.11f
        iconPaint.alpha = 185
        val sparkRect1 = RectF(cx - r * 0.42f, cy - r * 0.42f, cx + r * 0.42f, cy + r * 0.42f)
        canvas.drawArc(sparkRect1, -65f, 50f, false, iconPaint)
        canvas.drawArc(sparkRect1, 115f, 50f, false, iconPaint)

        if (!singleMode) {
            val sparkRect2 = RectF(cx - r * 0.68f, cy - r * 0.68f, cx + r * 0.68f, cy + r * 0.68f)
            canvas.drawArc(sparkRect2, -65f, 50f, false, iconPaint)
            canvas.drawArc(sparkRect2, 115f, 50f, false, iconPaint)
        }
        iconPaint.alpha = 255
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val x = event.x
        val y = event.y
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                pressedZone = hitZone(x, y)
                invalidate()
                return true
            }
            MotionEvent.ACTION_UP -> {
                val upZone = hitZone(x, y)
                val triggered = pressedZone
                pressedZone = ZONE_NONE
                invalidate()
                if (triggered != ZONE_NONE && triggered == upZone) {
                    performClick()
                    when (triggered) {
                        ZONE_HERO -> onCenterClick()
                        ZONE_LEFT -> onTogglePinchMode()
                        ZONE_RIGHT -> onSettingsClick()
                        ZONE_TOP -> onTopPillClick()
                    }
                }
                return true
            }
            MotionEvent.ACTION_CANCEL -> {
                pressedZone = ZONE_NONE
                invalidate()
                return true
            }
        }
        return super.onTouchEvent(event)
    }

    override fun performClick(): Boolean {
        super.performClick()
        return true
    }

    private fun hitZone(x: Float, y: Float): Int {
        if (hypot(x - centerX, y - heroCenterY) <= heroRadius * 1.05f) return ZONE_HERO
        if (pillRectLeft.contains(x, y)) return ZONE_LEFT
        if (pillRectRight.contains(x, y)) return ZONE_RIGHT
        if (pillRectTop.contains(x, y)) return ZONE_TOP
        return ZONE_NONE
    }

    companion object {
        private const val ZONE_NONE = 0
        private const val ZONE_HERO = 1
        private const val ZONE_LEFT = 2
        private const val ZONE_RIGHT = 3
        private const val ZONE_TOP = 4
    }
}
