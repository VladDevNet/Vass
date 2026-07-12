package com.vass.overlay

import android.content.Context
import android.content.res.Configuration

internal class OverlayPositionStore(context: Context) {
  private val preferences = context.getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE)

  private fun suffix(configuration: Configuration): String =
    if (configuration.orientation == Configuration.ORIENTATION_LANDSCAPE) "landscape" else "portrait"

  fun readX(configuration: Configuration, fallback: Int): Int =
    preferences.getInt("x_${suffix(configuration)}", fallback)

  fun readY(configuration: Configuration, fallback: Int): Int =
    preferences.getInt("y_${suffix(configuration)}", fallback)

  fun save(configuration: Configuration, x: Int, y: Int) {
    preferences.edit()
      .putInt("x_${suffix(configuration)}", x)
      .putInt("y_${suffix(configuration)}", y)
      .apply()
  }
}
