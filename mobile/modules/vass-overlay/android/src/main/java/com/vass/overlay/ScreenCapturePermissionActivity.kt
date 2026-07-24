package com.vass.overlay

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import androidx.core.content.ContextCompat

class ScreenCapturePermissionActivity : Activity() {
  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    if (savedInstanceState != null) return
    if (intent.getStringExtra(OverlayContract.EXTRA_CAPTURE_REQUEST_ID).isNullOrBlank()) {
      finish()
      return
    }
    val manager = getSystemService(MediaProjectionManager::class.java)
    @Suppress("DEPRECATION")
    startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_CAPTURE)
  }

  @Deprecated("Deprecated in Java")
  override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
    super.onActivityResult(requestCode, resultCode, data)
    if (requestCode != REQUEST_CAPTURE) return
    val requestId = intent.getStringExtra(OverlayContract.EXTRA_CAPTURE_REQUEST_ID) ?: run {
      finish()
      return
    }
    if (resultCode != RESULT_OK || data == null) {
      ScreenCaptureStore.save(this, requestId, "cancelled")
      returnToVass()
      finish()
      return
    }
    ContextCompat.startForegroundService(
      this,
      Intent(this, VassMediaProjectionService::class.java)
        .putExtra(OverlayContract.EXTRA_CAPTURE_REQUEST_ID, requestId)
        .putExtra(OverlayContract.EXTRA_CAPTURE_RESULT_CODE, resultCode)
        .putExtra(OverlayContract.EXTRA_CAPTURE_RESULT_DATA, data),
    )
    // Do not bring Vass back over the content the person selected. On Android
    // 14+ the consent sheet can share one app window; reopening Vass here can
    // replace that window or capture the transition/system UI instead. The
    // foreground service keeps the one-shot capture alive, and JS reopens
    // Vass only after it has received a real frame.
    moveTaskToBack(true)
    finish()
  }

  private fun returnToVass() {
    packageManager.getLaunchIntentForPackage(packageName)?.let { launchIntent ->
      launchIntent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
      startActivity(launchIntent)
    }
  }

  companion object {
    private const val REQUEST_CAPTURE = 7103
  }
}
