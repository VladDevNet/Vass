package com.vass.overlay

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import androidx.core.app.NotificationCompat

internal object ScreenCaptureNotification {
  private const val CHANNEL_ID = "vass_screen_capture"
  const val NOTIFICATION_ID = 7102

  fun ensureChannel(context: Context) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
    context.getSystemService(NotificationManager::class.java).createNotificationChannel(
      NotificationChannel(CHANNEL_ID, "Разбор экрана Vass", NotificationManager.IMPORTANCE_LOW).apply {
        description = "Одноразовый захват экрана по вашей команде"
        setShowBadge(false)
      },
    )
  }

  fun build(context: Context): Notification = NotificationCompat.Builder(context, CHANNEL_ID)
    .setSmallIcon(R.drawable.vass_overlay_notification)
    .setContentTitle("Vass разбирает экран")
    .setContentText("Создаю один снимок экрана для вашего запроса")
    .setOngoing(true)
    .setOnlyAlertOnce(true)
    .setCategory(NotificationCompat.CATEGORY_SERVICE)
    .setPriority(NotificationCompat.PRIORITY_LOW)
    .build()
}
