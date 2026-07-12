package com.vass.overlay

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.view.GestureDetector
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.animation.AccelerateDecelerateInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import kotlin.math.abs

internal class OverlayAvatarView(
  context: Context,
  private val onDrag: (deltaX: Int, deltaY: Int, finished: Boolean) -> Unit,
  private val onSingleTap: () -> Unit,
  private val onLongPress: () -> Unit,
  private val onDoubleTap: () -> Unit,
) : FrameLayout(context) {
  private val density = resources.displayMetrics.density
  private val image = ImageView(context)
  private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
  private var downRawX = 0f
  private var downRawY = 0f
  private var lastRawX = 0f
  private var lastRawY = 0f
  private var dragging = false
  private var animator: ValueAnimator? = null

  private val detector = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
    override fun onDown(event: MotionEvent): Boolean = true

    override fun onSingleTapConfirmed(event: MotionEvent): Boolean {
      onSingleTap()
      return true
    }

    override fun onDoubleTap(event: MotionEvent): Boolean {
      onDoubleTap()
      return true
    }

    override fun onLongPress(event: MotionEvent) {
      if (!dragging) onLongPress()
    }
  })

  init {
    val haloPadding = (5 * density).toInt()
    setPadding(haloPadding, haloPadding, haloPadding, haloPadding)
    elevation = 12 * density
    clipChildren = false

    image.scaleType = ImageView.ScaleType.CENTER_CROP
    image.contentDescription = "Vass"
    image.background = GradientDrawable().apply {
      shape = GradientDrawable.OVAL
      setColor(Color.BLACK)
    }
    image.clipToOutline = true
    addView(image, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
    update("idle", "olga")
  }

  fun update(state: String, avatarId: String) {
    val resourceName = if (avatarId == "male") "vass_overlay_male" else "vass_overlay_olga"
    val resourceId = resources.getIdentifier(resourceName, "drawable", context.packageName)
    if (resourceId != 0) image.setImageResource(resourceId)

    val color = when (state) {
      "recording" -> Color.rgb(59, 130, 246)
      "thinking" -> Color.rgb(168, 85, 247)
      "speaking" -> Color.rgb(245, 158, 11)
      "paused" -> Color.rgb(100, 116, 139)
      "error" -> Color.rgb(239, 68, 68)
      else -> Color.rgb(245, 158, 11)
    }
    background = GradientDrawable().apply {
      shape = GradientDrawable.OVAL
      setColor(Color.argb(if (state == "paused") 90 else 155, Color.red(color), Color.green(color), Color.blue(color)))
      setStroke((2 * density).toInt(), color)
    }
    image.alpha = if (state == "paused") 0.62f else 1f
    updatePulse(state)
  }

  private fun updatePulse(state: String) {
    animator?.cancel()
    animator = null
    scaleX = 1f
    scaleY = 1f
    if (state !in setOf("recording", "thinking", "speaking")) return

    animator = ValueAnimator.ofFloat(0.96f, 1.04f).apply {
      duration = if (state == "thinking") 1200L else 760L
      repeatMode = ValueAnimator.REVERSE
      repeatCount = ValueAnimator.INFINITE
      interpolator = AccelerateDecelerateInterpolator()
      addUpdateListener {
        val scale = it.animatedValue as Float
        scaleX = scale
        scaleY = scale
      }
      start()
    }
  }

  override fun onTouchEvent(event: MotionEvent): Boolean {
    detector.onTouchEvent(event)
    when (event.actionMasked) {
      MotionEvent.ACTION_DOWN -> {
        parent?.requestDisallowInterceptTouchEvent(true)
        downRawX = event.rawX
        downRawY = event.rawY
        lastRawX = event.rawX
        lastRawY = event.rawY
        dragging = false
      }
      MotionEvent.ACTION_MOVE -> {
        if (!dragging && (abs(event.rawX - downRawX) > touchSlop || abs(event.rawY - downRawY) > touchSlop)) {
          dragging = true
        }
        if (dragging) {
          onDrag((event.rawX - lastRawX).toInt(), (event.rawY - lastRawY).toInt(), false)
          lastRawX = event.rawX
          lastRawY = event.rawY
        }
      }
      MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
        if (dragging) onDrag(0, 0, true)
        dragging = false
        parent?.requestDisallowInterceptTouchEvent(false)
      }
    }
    return true
  }

  override fun onDetachedFromWindow() {
    animator?.cancel()
    animator = null
    super.onDetachedFromWindow()
  }
}
