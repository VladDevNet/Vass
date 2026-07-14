package com.vass.overlay

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import java.util.concurrent.atomic.AtomicBoolean

// Owns Android ACTION_SEND independently of Expo's MainActivity lifecycle.
// The source URI permission is valid while this activity is foregrounded, so
// copy first, then bring the regular Vass task to the front.
class ShareReceiverActivity : Activity() {
  private val processing = AtomicBoolean(false)

  override fun onCreate(savedInstanceState: Bundle?) {
    super.onCreate(savedInstanceState)
    stageAndOpen(intent)
  }

  override fun onNewIntent(intent: Intent) {
    super.onNewIntent(intent)
    setIntent(intent)
    stageAndOpen(intent)
  }

  private fun stageAndOpen(incoming: Intent?) {
    val shareIntent = incoming ?: run {
      finish()
      return
    }
    if (shareIntent.action !in setOf(Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE)) {
      finish()
      return
    }
    if (!processing.compareAndSet(false, true)) return

    Thread({
      SharedImageStore.capture(applicationContext, shareIntent)
      runOnUiThread {
        packageManager.getLaunchIntentForPackage(packageName)?.let { launchIntent ->
          launchIntent.addFlags(
            Intent.FLAG_ACTIVITY_CLEAR_TOP or
              Intent.FLAG_ACTIVITY_SINGLE_TOP,
          )
          startActivity(launchIntent)
        }
        finish()
      }
    }, "VassShareReceiver").start()
  }
}
