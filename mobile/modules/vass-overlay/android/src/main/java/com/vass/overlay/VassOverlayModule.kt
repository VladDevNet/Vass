package com.vass.overlay

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.ContextCompat
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition

class VassOverlayModule : Module() {
  override fun definition() = ModuleDefinition {
    Name("VassOverlay")
    Events("onOverlayEvent")

    OnStartObserving {
      OverlayEventBridge.listener = { event -> sendEvent("onOverlayEvent", event) }
    }

    OnStopObserving {
      OverlayEventBridge.listener = null
    }

    AsyncFunction("canDrawOverlays") {
      val context = requireContext()
      Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && Settings.canDrawOverlays(context)
    }

    AsyncFunction("getStatus") {
      val context = requireContext()
      val preferences = context.getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE)
      mapOf(
        "available" to (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O),
        "permissionGranted" to (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && Settings.canDrawOverlays(context)),
        "enabled" to preferences.getBoolean(OverlayContract.PREF_ENABLED, false),
        "running" to VassOverlayService.isRunning,
      )
    }

    AsyncFunction("requestOverlayPermission") {
      val context = requireContext()
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !Settings.canDrawOverlays(context)) {
        val intent = Intent(
          Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
          Uri.parse("package:${context.packageName}"),
        ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
      }
      null
    }

    AsyncFunction("start") { snapshot: Map<String, Any?>, appVisible: Boolean ->
      val context = requireContext()
      if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
        throw IllegalStateException("Android overlay requires API 26 or newer")
      }
      if (!Settings.canDrawOverlays(context)) {
        throw SecurityException("Overlay permission has not been granted")
      }
      val intent = OverlayContract.putSnapshot(
        Intent(context, VassOverlayService::class.java).setAction(OverlayContract.ACTION_START),
        snapshot,
      ).putExtra(OverlayContract.EXTRA_APP_VISIBLE, appVisible)
      ContextCompat.startForegroundService(context, intent)
    }

    Function("update") { snapshot: Map<String, Any?> ->
      sendServiceCommand(
        OverlayContract.putSnapshot(
          Intent(requireContext(), VassOverlayService::class.java).setAction(OverlayContract.ACTION_UPDATE),
          snapshot,
        ),
      )
    }

    Function("setAppVisible") { visible: Boolean ->
      sendServiceCommand(
        Intent(requireContext(), VassOverlayService::class.java)
          .setAction(OverlayContract.ACTION_VISIBILITY)
          .putExtra(OverlayContract.EXTRA_APP_VISIBLE, visible),
      )
    }

    AsyncFunction("stop") {
      val context = requireContext()
      context.getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE)
        .edit()
        .putBoolean(OverlayContract.PREF_ENABLED, false)
        .apply()
      sendServiceCommand(Intent(context, VassOverlayService::class.java).setAction(OverlayContract.ACTION_STOP))
    }
  }

  private fun requireContext(): Context =
    appContext.reactContext?.applicationContext
      ?: throw IllegalStateException("React context is not available")

  private fun sendServiceCommand(intent: Intent) {
    if (!VassOverlayService.isRunning) return
    requireContext().startService(intent)
  }
}
