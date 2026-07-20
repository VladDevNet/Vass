package com.vass.overlay

import android.app.ActivityManager
import android.content.Context
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Intent
import android.media.AudioDeviceInfo
import android.media.AudioManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import expo.modules.kotlin.modules.Module
import expo.modules.kotlin.modules.ModuleDefinition
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest

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

    AsyncFunction("openAppDetails") {
      val context = requireContext()
      context.startActivity(
        Intent(
          Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
          Uri.parse("package:${context.packageName}"),
        ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
      )
      null
    }

    AsyncFunction("openApp") {
      val context = requireContext()
      val intent = context.packageManager.getLaunchIntentForPackage(context.packageName)?.apply {
        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
      } ?: throw IllegalStateException("Vass launch activity is unavailable")
      context.startActivity(intent)
      null
    }

    AsyncFunction("openExternalUrl") { rawUrl: String ->
      val context = requireContext()
      val uri = Uri.parse(rawUrl)
      if (uri.scheme !in setOf("http", "https")) {
        throw IllegalArgumentException("Only HTTP(S) external URLs are supported")
      }
      val genericIntent = Intent(Intent.ACTION_VIEW, uri).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
      val preferredIntent = if (
        uri.host?.endsWith("youtube.com", ignoreCase = true) == true ||
        uri.host?.endsWith("youtu.be", ignoreCase = true) == true
      ) {
        Intent(genericIntent).setPackage("com.google.android.youtube")
      } else {
        genericIntent
      }
      try {
        context.startActivity(preferredIntent)
      } catch (_: ActivityNotFoundException) {
        context.startActivity(genericIntent)
      }
      null
    }

    AsyncFunction("canRequestPackageInstalls") {
      val context = requireContext()
      Build.VERSION.SDK_INT < Build.VERSION_CODES.O || context.packageManager.canRequestPackageInstalls()
    }

    AsyncFunction("requestPackageInstallPermission") {
      val context = requireContext()
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !context.packageManager.canRequestPackageInstalls()) {
        try {
          context.startActivity(
            Intent(
              Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
              Uri.parse("package:${context.packageName}"),
            ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
          )
        } catch (error: ActivityNotFoundException) {
          throw IllegalStateException("Android cannot open the update-install permission settings", error)
        }
      }
      null
    }

    AsyncFunction("installUpdateApk") { rawUri: String, expectedSha256: String? ->
      val context = requireContext()
      if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !context.packageManager.canRequestPackageInstalls()) {
        throw SecurityException("Android has not allowed Vass to install updates")
      }

      val apk = resolveUpdateApk(context, rawUri)
      if (!apk.isFile || apk.length() == 0L) {
        throw IllegalArgumentException("Downloaded update APK is missing or empty")
      }
      verifySha256(apk, expectedSha256)

      val contentUri = FileProvider.getUriForFile(context, "${context.packageName}.vassupdates", apk)
      val installIntent = Intent(Intent.ACTION_VIEW)
        .setDataAndType(contentUri, "application/vnd.android.package-archive")
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
      installIntent.clipData = ClipData.newRawUri("vass-update", contentUri)
      try {
        context.startActivity(installIntent)
      } catch (error: ActivityNotFoundException) {
        throw IllegalStateException("Android package installer is unavailable", error)
      }
      null
    }

    AsyncFunction("getAudioOutputs") {
      getAudioOutputStatus(requireContext())
    }

    AsyncFunction("selectAudioOutput") { outputId: String ->
      selectAudioOutput(requireContext(), outputId)
    }

    AsyncFunction("clearAudioOutput") {
      clearAudioOutput(requireContext())
      null
    }

    AsyncFunction("requestScreenCapture") { requestId: String ->
      val context = requireContext()
      if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
        throw IllegalStateException("Screen analysis requires Android 8 or newer")
      }
      sendServiceCommand(Intent(context, VassOverlayService::class.java).setAction(OverlayContract.ACTION_CAPTURE_STARTED))
      context.startActivity(
        Intent(context, ScreenCapturePermissionActivity::class.java)
          .putExtra(OverlayContract.EXTRA_CAPTURE_REQUEST_ID, requestId)
          .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
      )
      null
    }

    AsyncFunction("getScreenCaptureResult") {
      ScreenCaptureStore.read(requireContext())
    }

    AsyncFunction("clearScreenCaptureResult") { requestId: String ->
      ScreenCaptureStore.clear(requireContext(), requestId)
      null
    }

    AsyncFunction("getSharedContent") {
      SharedContentStore.read(requireContext())
    }

    AsyncFunction("acknowledgeSharedContent") { requestId: String ->
      SharedContentStore.acknowledge(requireContext(), requestId)
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

    AsyncFunction("suspendForExternalMedia") {
      sendServiceCommand(
        Intent(requireContext(), VassOverlayService::class.java)
          .setAction(OverlayContract.ACTION_SUSPEND_EXTERNAL_MEDIA),
      )
    }

    AsyncFunction("stop") {
      val context = requireContext()
      context.getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE)
        .edit()
        .putBoolean(OverlayContract.PREF_ENABLED, false)
        .apply()
      sendServiceCommand(Intent(context, VassOverlayService::class.java).setAction(OverlayContract.ACTION_STOP_FROM_APP))
    }

    AsyncFunction("finishAppTask") {
      val context = requireContext()
      // Be self-contained: JS normally calls stop() first, but ending a
      // conversation must still remove both foreground services if the task
      // is closed while React is delayed or suspended.
      context.getSharedPreferences(OverlayContract.PREFS, Context.MODE_PRIVATE)
        .edit()
        .putBoolean(OverlayContract.PREF_ENABLED, false)
        .apply()
      context.stopService(Intent(context, VassMediaProjectionService::class.java))
      context.stopService(Intent(context, VassOverlayService::class.java))

      val activity = appContext.currentActivity
      if (activity != null) {
        activity.runOnUiThread { activity.finishAndRemoveTask() }
      } else {
        val manager = context.getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
        manager.appTasks
          .firstOrNull { task ->
            task.taskInfo.baseIntent.component?.packageName == context.packageName
          }
          ?.finishAndRemoveTask()
      }
      null
    }
  }

  private fun requireContext(): Context =
    appContext.reactContext?.applicationContext
      ?: throw IllegalStateException("React context is not available")

  private fun getAudioOutputStatus(context: Context): Map<String, Any?> {
    val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
      return mapOf(
        "available" to true,
        "supportsExplicitRouting" to false,
        "outputs" to listOf(speakerOutput()),
        "selectedId" to "speaker",
      )
    }

    val outputs = audioManager.availableCommunicationDevices
      .mapNotNull(::audioOutputForDevice)
      .distinctBy { it["id"] }
      .let { devices ->
        if (devices.any { it["id"] == "speaker" }) devices else listOf(speakerOutput()) + devices
      }
    val selectedId = audioManager.communicationDevice
      ?.let(::audioOutputForDevice)
      ?.get("id") as? String

    return mapOf(
      "available" to true,
      "supportsExplicitRouting" to true,
      "outputs" to outputs,
      "selectedId" to selectedId,
    )
  }

  private fun selectAudioOutput(context: Context, outputId: String): Map<String, Any?> {
    val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
      if (outputId != "speaker") {
        throw IllegalStateException("This Android version cannot explicitly select a headset output")
      }
      enableLegacySpeaker(audioManager)
      return getAudioOutputStatus(context)
    }

    audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
    val device = audioManager.availableCommunicationDevices
      .firstOrNull { audioOutputForDevice(it)?.get("id") == outputId }
      ?: throw IllegalArgumentException("Selected audio output is no longer connected")
    if (!audioManager.setCommunicationDevice(device)) {
      throw IllegalStateException("Android rejected the selected audio output")
    }
    return getAudioOutputStatus(context)
  }

  private fun clearAudioOutput(context: Context) {
    val audioManager = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
      audioManager.clearCommunicationDevice()
    } else {
      @Suppress("DEPRECATION")
      audioManager.isSpeakerphoneOn = false
      audioManager.mode = AudioManager.MODE_NORMAL
    }
  }

  @Suppress("DEPRECATION")
  private fun enableLegacySpeaker(audioManager: AudioManager) {
    audioManager.mode = AudioManager.MODE_IN_COMMUNICATION
    audioManager.isSpeakerphoneOn = true
  }

  private fun audioOutputForDevice(device: AudioDeviceInfo): Map<String, String>? {
    val kind = audioOutputKind(device.type) ?: return null
    return mapOf(
      "id" to if (kind == "speaker") "speaker" else "device:${device.id}",
      "kind" to kind,
      "label" to when (kind) {
        "speaker" -> "Динамик телефона"
        "wired" -> "Проводные наушники"
        else -> "Bluetooth-устройство"
      },
    )
  }

  private fun speakerOutput(): Map<String, String> = mapOf(
    "id" to "speaker",
    "kind" to "speaker",
    "label" to "Динамик телефона",
  )

  private fun audioOutputKind(deviceType: Int): String? = when (deviceType) {
    AudioDeviceInfo.TYPE_BUILTIN_SPEAKER,
    AudioDeviceInfo.TYPE_BUILTIN_SPEAKER_SAFE -> "speaker"
    AudioDeviceInfo.TYPE_WIRED_HEADSET,
    AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
    AudioDeviceInfo.TYPE_USB_HEADSET,
    AudioDeviceInfo.TYPE_USB_DEVICE,
    AudioDeviceInfo.TYPE_USB_ACCESSORY -> "wired"
    AudioDeviceInfo.TYPE_BLUETOOTH_SCO,
    AudioDeviceInfo.TYPE_BLUETOOTH_A2DP,
    AudioDeviceInfo.TYPE_HEARING_AID,
    AudioDeviceInfo.TYPE_BLE_HEADSET,
    AudioDeviceInfo.TYPE_BLE_SPEAKER,
    AudioDeviceInfo.TYPE_BLE_HEARING_AID -> "bluetooth"
    else -> null
  }

  private fun resolveUpdateApk(context: Context, rawUri: String): File {
    val uri = Uri.parse(rawUri)
    if (uri.scheme != "file") {
      throw IllegalArgumentException("Update APK must be a local file URI")
    }
    val path = uri.path ?: throw IllegalArgumentException("Update APK URI has no path")
    val apk = File(path).canonicalFile
    val updateDirectory = File(context.cacheDir, "vass-updates").canonicalFile
    if (apk.parentFile?.canonicalFile != updateDirectory) {
      throw SecurityException("Update APK is outside the approved cache directory")
    }
    return apk
  }

  private fun verifySha256(apk: File, expectedSha256: String?) {
    if (expectedSha256.isNullOrBlank()) return
    val expected = expectedSha256.lowercase()
    if (!SHA256_PATTERN.matches(expected)) {
      throw IllegalArgumentException("Server supplied an invalid update checksum")
    }

    val digest = MessageDigest.getInstance("SHA-256")
    FileInputStream(apk).use { stream ->
      val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
      while (true) {
        val read = stream.read(buffer)
        if (read < 0) break
        digest.update(buffer, 0, read)
      }
    }
    val actual = digest.digest().joinToString("") { byte ->
      (byte.toInt() and 0xff).toString(16).padStart(2, '0')
    }
    if (actual != expected) {
      throw SecurityException("Downloaded update checksum does not match the published release")
    }
  }

  private fun sendServiceCommand(intent: Intent) {
    if (!VassOverlayService.isRunning) return
    requireContext().startService(intent)
  }

  private companion object {
    val SHA256_PATTERN = Regex("^[0-9a-f]{64}$")
  }

}
