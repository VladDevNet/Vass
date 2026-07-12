package com.vass.overlay

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.core.app.NotificationCompat

internal object OverlayNotification {
  fun ensureChannel(context: Context) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
    val manager = context.getSystemService(NotificationManager::class.java)
    manager.createNotificationChannel(
      NotificationChannel(
        OverlayContract.NOTIFICATION_CHANNEL_ID,
        "Vass поверх приложений",
        NotificationManager.IMPORTANCE_LOW,
      ).apply {
        description = "Постоянное управление плавающим аватаром Vass"
        setShowBadge(false)
      },
    )
  }

  fun build(context: Context, state: String): Notification {
    val openIntent = context.packageManager.getLaunchIntentForPackage(context.packageName)?.apply {
      addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
    }
    val openPendingIntent = openIntent?.let {
      PendingIntent.getActivity(context, 1, it, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }
    val stopPendingIntent = servicePendingIntent(context, 3, OverlayContract.ACTION_STOP)

    return NotificationCompat.Builder(context, OverlayContract.NOTIFICATION_CHANNEL_ID)
      .setSmallIcon(R.drawable.vass_overlay_notification)
      .setContentTitle("Vass работает поверх приложений")
      .setContentText(stateLabel(state))
      .setContentIntent(openPendingIntent)
      .setOngoing(true)
      .setOnlyAlertOnce(true)
      .setCategory(NotificationCompat.CATEGORY_SERVICE)
      .setPriority(NotificationCompat.PRIORITY_LOW)
      .addAction(0, "Открыть", openPendingIntent)
      .addAction(0, "Остановить", stopPendingIntent)
      .build()
  }

  private fun servicePendingIntent(context: Context, requestCode: Int, action: String): PendingIntent =
    PendingIntent.getService(
      context,
      requestCode,
      Intent(context, VassOverlayService::class.java).setAction(action),
      PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

  private fun stateLabel(state: String): String = when (state) {
    "recording" -> "Слушает"
    "thinking" -> "Думает"
    "speaking" -> "Говорит"
    "paused" -> "На паузе"
    "error" -> "Нужно внимание"
    else -> "Готов к разговору"
  }
}
