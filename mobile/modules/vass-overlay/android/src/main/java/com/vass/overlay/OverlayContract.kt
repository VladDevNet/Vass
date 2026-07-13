package com.vass.overlay

import android.content.Intent

internal object OverlayContract {
  const val ACTION_START = "com.vass.overlay.action.START"
  const val ACTION_UPDATE = "com.vass.overlay.action.UPDATE"
  const val ACTION_VISIBILITY = "com.vass.overlay.action.VISIBILITY"
  const val ACTION_PAUSE = "com.vass.overlay.action.PAUSE"
  const val ACTION_SUSPEND_EXTERNAL_MEDIA = "com.vass.overlay.action.SUSPEND_EXTERNAL_MEDIA"
  const val ACTION_STOP = "com.vass.overlay.action.STOP"
  const val ACTION_STOP_FROM_APP = "com.vass.overlay.action.STOP_FROM_APP"

  const val EXTRA_STATE = "state"
  const val EXTRA_AVATAR_ID = "avatarId"
  const val EXTRA_ENABLED = "enabled"
  const val EXTRA_APP_VISIBLE = "appVisible"

  const val PREFS = "vass_overlay"
  const val PREF_ENABLED = "enabled"
  const val PREF_STATE = "state"
  const val PREF_AVATAR_ID = "avatarId"

  const val NOTIFICATION_CHANNEL_ID = "vass_overlay"
  const val NOTIFICATION_ID = 7101

  const val EXPO_AUDIO_RECORDING_SERVICE = "expo.modules.audio.service.AudioRecordingService"
  const val EXPO_AUDIO_STOP_RECORDING = "expo.modules.audio.action.STOP_RECORDING"

  fun putSnapshot(intent: Intent, snapshot: Map<String, Any?>): Intent = intent.apply {
    putExtra(EXTRA_STATE, snapshot[EXTRA_STATE] as? String ?: "idle")
    putExtra(EXTRA_AVATAR_ID, snapshot[EXTRA_AVATAR_ID] as? String ?: "olga")
    putExtra(EXTRA_ENABLED, snapshot[EXTRA_ENABLED] as? Boolean ?: true)
  }
}

internal object OverlayEventBridge {
  @Volatile
  var listener: ((Map<String, Any?>) -> Unit)? = null

  fun emit(type: String, extras: Map<String, Any?> = emptyMap()) {
    listener?.invoke(mapOf("type" to type) + extras)
  }
}
