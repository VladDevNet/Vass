package com.vass.overlay

import android.app.Activity
import android.content.Intent
import android.os.Bundle

// Owns Android ACTION_SEND independently of Expo's MainActivity lifecycle.
// The source URI permission is valid while this activity is foregrounded, so
// copy first, then bring the regular Vass task to the front.
class ShareReceiverActivity : Activity() {
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
    if (incoming?.action != Intent.ACTION_SEND) {
      finish()
      return
    }

    Thread {
      SharedImageStore.capture(applicationContext, incoming)
      runOnUiThread {
        packageManager.getLaunchIntentForPackage(packageName)?.let { launchIntent ->
          launchIntent.addFlags(
            Intent.FLAG_ACTIVITY_NEW_TASK or
              Intent.FLAG_ACTIVITY_CLEAR_TOP or
              Intent.FLAG_ACTIVITY_SINGLE_TOP,
          )
          startActivity(launchIntent)
        }
        finish()
      }
    }.start()
  }
}
