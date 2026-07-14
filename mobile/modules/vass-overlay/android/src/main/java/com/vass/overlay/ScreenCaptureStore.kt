package com.vass.overlay

import android.content.Context

internal object ScreenCaptureStore {
  private const val PREFS = "vass_screen_capture"
  private const val KEY_REQUEST_ID = "requestId"
  private const val KEY_STATUS = "status"
  private const val KEY_URI = "uri"
  private const val KEY_ERROR = "error"

  fun save(context: Context, requestId: String, status: String, uri: String? = null, error: String? = null) {
    context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
      .putString(KEY_REQUEST_ID, requestId)
      .putString(KEY_STATUS, status)
      .putString(KEY_URI, uri)
      .putString(KEY_ERROR, error)
      .apply()
    OverlayEventBridge.emit("screenCapture", mapOf("requestId" to requestId, "status" to status, "uri" to uri, "error" to error))
  }

  fun read(context: Context): Map<String, String?> {
    val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    return mapOf(
      "requestId" to prefs.getString(KEY_REQUEST_ID, null),
      "status" to prefs.getString(KEY_STATUS, null),
      "uri" to prefs.getString(KEY_URI, null),
      "error" to prefs.getString(KEY_ERROR, null),
    )
  }

  fun clear(context: Context, requestId: String) {
    val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    if (prefs.getString(KEY_REQUEST_ID, null) == requestId) prefs.edit().clear().apply()
  }
}
