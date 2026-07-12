package com.vass.overlay

import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.content.res.Configuration
import android.graphics.PixelFormat
import android.graphics.Rect
import android.os.Build
import android.os.IBinder
import android.provider.Settings
import android.view.Gravity
import android.view.WindowInsets
import android.view.WindowManager
import kotlin.math.max
import kotlin.math.min

class VassOverlayService : Service() {
  private lateinit var windowManager: WindowManager
  private lateinit var positionStore: OverlayPositionStore
  private var avatarView: OverlayAvatarView? = null
  private var layoutParams: WindowManager.LayoutParams? = null
  private var state = "idle"
  private var avatarId = "olga"
  private var appVisible = true
  private var enabled = false

  override fun onCreate() {
    super.onCreate()
    isRunning = true
    windowManager = getSystemService(WindowManager::class.java)
    positionStore = OverlayPositionStore(this)
    OverlayNotification.ensureChannel(this)
  }

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    when (intent?.action) {
      OverlayContract.ACTION_START -> handleStart(intent)
      OverlayContract.ACTION_UPDATE -> handleUpdate(intent)
      OverlayContract.ACTION_VISIBILITY -> {
        appVisible = intent.getBooleanExtra(OverlayContract.EXTRA_APP_VISIBLE, true)
        syncOverlayVisibility()
      }
      OverlayContract.ACTION_STOP -> stopFromUser()
      else -> if (!enabled) stopSelf()
    }
    return START_NOT_STICKY
  }

  override fun onBind(intent: Intent?): IBinder? = null

  override fun onConfigurationChanged(newConfig: Configuration) {
    super.onConfigurationChanged(newConfig)
    if (avatarView != null) {
      removeOverlay()
      showOverlay()
    }
  }

  override fun onDestroy() {
    removeOverlay()
    isRunning = false
    super.onDestroy()
  }

  private fun handleStart(intent: Intent) {
    readSnapshot(intent)
    enabled = intent.getBooleanExtra(OverlayContract.EXTRA_ENABLED, true)
    appVisible = intent.getBooleanExtra(OverlayContract.EXTRA_APP_VISIBLE, true)
    persistSnapshot()
    startInForeground()
    syncOverlayVisibility()
  }

  private fun handleUpdate(intent: Intent) {
    readSnapshot(intent)
    enabled = intent.getBooleanExtra(OverlayContract.EXTRA_ENABLED, enabled)
    persistSnapshot()
    avatarView?.update(state, avatarId)
    updateNotification()
    syncOverlayVisibility()
  }

  private fun readSnapshot(intent: Intent) {
    state = intent.getStringExtra(OverlayContract.EXTRA_STATE) ?: state
    avatarId = intent.getStringExtra(OverlayContract.EXTRA_AVATAR_ID) ?: avatarId
  }

  private fun persistSnapshot() {
    getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE).edit()
      .putBoolean(OverlayContract.PREF_ENABLED, enabled)
      .putString(OverlayContract.PREF_STATE, state)
      .putString(OverlayContract.PREF_AVATAR_ID, avatarId)
      .apply()
  }

  private fun startInForeground() {
    val notification = OverlayNotification.build(this, state)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
      startForeground(
        OverlayContract.NOTIFICATION_ID,
        notification,
        ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
      )
    } else {
      startForeground(OverlayContract.NOTIFICATION_ID, notification)
    }
  }

  private fun updateNotification() {
    getSystemService(NotificationManager::class.java).notify(
      OverlayContract.NOTIFICATION_ID,
      OverlayNotification.build(this, state),
    )
  }

  private fun syncOverlayVisibility() {
    val shouldShow = enabled && !appVisible && Settings.canDrawOverlays(this)
    if (shouldShow) showOverlay() else removeOverlay()
  }

  private fun showOverlay() {
    if (avatarView != null || !Settings.canDrawOverlays(this)) return
    val size = dp(84)
    val bounds = usableBounds()
    val configuration = resources.configuration
    val fallbackX = max(bounds.left, bounds.right - size - dp(12))
    val fallbackY = max(bounds.top, bounds.top + (bounds.height() - size) / 2)

    val params = WindowManager.LayoutParams(
      size,
      size,
      WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
      WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
        WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL or
        WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
      PixelFormat.TRANSLUCENT,
    ).apply {
      gravity = Gravity.TOP or Gravity.START
      x = positionStore.readX(configuration, fallbackX).coerceIn(bounds.left, max(bounds.left, bounds.right - size))
      y = positionStore.readY(configuration, fallbackY).coerceIn(bounds.top, max(bounds.top, bounds.bottom - size))
    }

    val view = OverlayAvatarView(
      context = this,
      onDrag = { deltaX, deltaY, finished -> moveOverlay(deltaX, deltaY, finished) },
      onSingleTap = { OverlayEventBridge.emit("controlPress") },
      // Phase 7.1 has no background voice control. Opening the full app is
      // the safe fallback until 7.2 wires this gesture to ConversationRuntime.
      onLongPress = { openApp() },
      onDoubleTap = { openApp() },
    ).apply {
      update(state, avatarId)
    }

    try {
      windowManager.addView(view, params)
      avatarView = view
      layoutParams = params
    } catch (_: SecurityException) {
      removeOverlay()
    }
  }

  private fun moveOverlay(deltaX: Int, deltaY: Int, finished: Boolean) {
    val view = avatarView ?: return
    val params = layoutParams ?: return
    val bounds = usableBounds()
    val maxX = max(bounds.left, bounds.right - params.width)
    val maxY = max(bounds.top, bounds.bottom - params.height)
    params.x = (params.x + deltaX).coerceIn(bounds.left, maxX)
    params.y = (params.y + deltaY).coerceIn(bounds.top, maxY)
    if (finished) {
      val center = params.x + params.width / 2
      params.x = if (center < bounds.centerX()) bounds.left else maxX
      positionStore.save(resources.configuration, params.x, params.y)
    }
    windowManager.updateViewLayout(view, params)
  }

  private fun removeOverlay() {
    val view = avatarView ?: return
    try {
      windowManager.removeView(view)
    } catch (_: IllegalArgumentException) {
      // The window was already detached by Android.
    } finally {
      avatarView = null
      layoutParams = null
    }
  }

  private fun openApp() {
    val intent = packageManager.getLaunchIntentForPackage(packageName)?.apply {
      addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
    } ?: return
    startActivity(intent)
    OverlayEventBridge.emit("openApp")
  }

  private fun stopFromUser() {
    enabled = false
    persistSnapshot()
    removeOverlay()
    OverlayEventBridge.emit("stopRequested")
    stopForeground(STOP_FOREGROUND_REMOVE)
    stopSelf()
  }

  private fun usableBounds(): Rect {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
      val metrics = windowManager.currentWindowMetrics
      val insets = metrics.windowInsets.getInsetsIgnoringVisibility(
        WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout(),
      )
      return Rect(
        metrics.bounds.left + insets.left,
        metrics.bounds.top + insets.top,
        metrics.bounds.right - insets.right,
        metrics.bounds.bottom - insets.bottom,
      )
    }

    @Suppress("DEPRECATION")
    val metrics = resources.displayMetrics
    return Rect(0, 0, metrics.widthPixels, metrics.heightPixels)
  }

  private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

  companion object {
    @Volatile
    var isRunning: Boolean = false
      private set
  }
}
