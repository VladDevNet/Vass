package com.vass.overlay

import android.app.Activity
import android.app.Service
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.util.DisplayMetrics
import android.view.WindowManager
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

class VassMediaProjectionService : Service() {
  private val handler = Handler(Looper.getMainLooper())
  private var requestId: String? = null
  private var projection: MediaProjection? = null
  private var projectionCallback: MediaProjection.Callback? = null
  private var virtualDisplay: VirtualDisplay? = null
  private var imageReader: ImageReader? = null
  private var delivered = false
  private var frameCount = 0
  private var captureNotBeforeMs = 0L

  override fun onBind(intent: Intent?): IBinder? = null

  override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
    val id = intent?.getStringExtra(OverlayContract.EXTRA_CAPTURE_REQUEST_ID)
    val resultData = intent?.getParcelableExtra<Intent>(OverlayContract.EXTRA_CAPTURE_RESULT_DATA)
    val resultCode = intent?.getIntExtra(OverlayContract.EXTRA_CAPTURE_RESULT_CODE, Activity.RESULT_CANCELED)
    if (id.isNullOrBlank() || resultData == null || resultCode != Activity.RESULT_OK) {
      finishCapture(id, "error", error = "invalid_consent")
      return START_NOT_STICKY
    }
    requestId = id
    ScreenCaptureNotification.ensureChannel(this)
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
      startForeground(ScreenCaptureNotification.NOTIFICATION_ID, ScreenCaptureNotification.build(this),
        android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
    } else {
      startForeground(ScreenCaptureNotification.NOTIFICATION_ID, ScreenCaptureNotification.build(this))
    }

    try {
      // PermissionActivity moves Vass behind the selected app immediately
      // after it starts this service. Give Android one short transition to
      // settle before creating the virtual display, otherwise the first
      // frame can be the consent sheet or Vass itself.
      handler.postDelayed({ startProjection(id, resultCode, resultData) }, CAPTURE_START_DELAY_MS)
    } catch (exception: Exception) {
      failCapture(id, "capture_start_failed", exception)
    }
    return START_NOT_STICKY
  }

  private fun startProjection(id: String, resultCode: Int, resultData: Intent) {
    if (delivered) return
    try {
      val manager = getSystemService(MediaProjectionManager::class.java)
      projection = manager.getMediaProjection(resultCode, resultData)
        ?: throw IllegalStateException("MediaProjection token was rejected")
      val callback = object : MediaProjection.Callback() {
        override fun onStop() {
          finishCapture(requestId, "error", error = "projection_stopped")
        }
      }
      projectionCallback = callback
      projection!!.registerCallback(callback, handler)

      val metrics = DisplayMetrics()
      @Suppress("DEPRECATION")
      getSystemService(WindowManager::class.java).defaultDisplay.getRealMetrics(metrics)
      imageReader = ImageReader.newInstance(metrics.widthPixels, metrics.heightPixels, PixelFormat.RGBA_8888, 2)
      imageReader!!.setOnImageAvailableListener({ reader -> onImage(reader) }, handler)
      virtualDisplay = projection!!.createVirtualDisplay(
        "VassOneShotScreenCapture", metrics.widthPixels, metrics.heightPixels, metrics.densityDpi,
        DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, imageReader!!.surface, null, handler,
      )
      captureNotBeforeMs = SystemClock.elapsedRealtime() + INITIAL_FRAME_SETTLE_MS
      handler.postDelayed({ finishCapture(requestId, "error", error = "capture_timeout") }, CAPTURE_TIMEOUT_MS)
    } catch (exception: Exception) {
      failCapture(id, "capture_unavailable", exception)
    }
  }

  private fun onImage(reader: ImageReader) {
    if (delivered) return
    val image = reader.acquireLatestImage() ?: return
    try {
      frameCount += 1
      if (frameCount < MINIMUM_STABLE_FRAMES || SystemClock.elapsedRealtime() < captureNotBeforeMs) return
      val uri = writeJpeg(image)
      finishCapture(requestId, "ready", uri)
    } catch (exception: Exception) {
      failCapture(requestId, "capture_encode_failed", exception)
    } finally {
      image.close()
    }
  }

  private fun writeJpeg(image: Image): String {
    val plane = image.planes[0]
    val pixelStride = plane.pixelStride
    val rowPadding = plane.rowStride - pixelStride * image.width
    val padded = Bitmap.createBitmap(image.width + rowPadding / pixelStride, image.height, Bitmap.Config.ARGB_8888)
    padded.copyPixelsFromBuffer(plane.buffer)
    val cropped = Bitmap.createBitmap(padded, 0, 0, image.width, image.height)
    if (padded !== cropped) padded.recycle()
    val maxSide = maxOf(cropped.width, cropped.height)
    val output = if (maxSide > MAX_LONG_SIDE) {
      val scale = MAX_LONG_SIDE.toFloat() / maxSide
      Bitmap.createScaledBitmap(cropped, (cropped.width * scale).toInt(), (cropped.height * scale).toInt(), true).also { cropped.recycle() }
    } else cropped
    val directory = File(cacheDir, "vass-screen-captures").apply { mkdirs() }
    val file = File(directory, "screen-${UUID.randomUUID()}.jpg")
    FileOutputStream(file).use { stream ->
      if (!output.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, stream)) throw IllegalStateException("JPEG encoding failed")
    }
    output.recycle()
    if (file.length() == 0L || file.length() > MAX_OUTPUT_BYTES) {
      file.delete()
      throw IllegalStateException("JPEG output outside upload bounds")
    }
    return "file://${file.absolutePath}"
  }

  private fun finishCapture(id: String?, status: String, uri: String? = null, error: String? = null) {
    if (delivered) return
    delivered = true
    handler.removeCallbacksAndMessages(null)
    try { virtualDisplay?.release() } catch (_: Exception) { }
    try { imageReader?.close() } catch (_: Exception) { }
    projectionCallback?.let { callback -> try { projection?.unregisterCallback(callback) } catch (_: Exception) { } }
    try { projection?.stop() } catch (_: Exception) { }
    virtualDisplay = null
    imageReader = null
    projection = null
    projectionCallback = null
    if (!id.isNullOrBlank()) ScreenCaptureStore.save(this, id, status, uri, error)
    if (VassOverlayService.isRunning) {
      startService(Intent(this, VassOverlayService::class.java).setAction(OverlayContract.ACTION_CAPTURE_FINISHED))
    }
    stopForeground(STOP_FOREGROUND_REMOVE)
    stopSelf()
  }

  private fun failCapture(id: String?, code: String, exception: Exception) {
    Log.w(LOG_TAG, "Screen capture failed: $code (${exception.javaClass.simpleName})", exception)
    finishCapture(id, "error", error = "$code:${exception.javaClass.simpleName}")
  }

  override fun onDestroy() {
    finishCapture(requestId, "error", error = "service_destroyed")
    super.onDestroy()
  }

  companion object {
    private const val LOG_TAG = "VassScreenCapture"
    private const val CAPTURE_START_DELAY_MS = 650L
    private const val INITIAL_FRAME_SETTLE_MS = 500L
    private const val MINIMUM_STABLE_FRAMES = 3
    private const val CAPTURE_TIMEOUT_MS = 12_000L
    private const val MAX_LONG_SIDE = 2048
    private const val JPEG_QUALITY = 85
    private const val MAX_OUTPUT_BYTES = 10L * 1024L * 1024L
  }
}
