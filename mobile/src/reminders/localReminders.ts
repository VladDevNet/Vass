import { Platform } from 'react-native';
import * as Notifications from 'expo-notifications';
import * as SecureStore from 'expo-secure-store';
import { api, type ReminderEvent } from '../api/client';
import { log } from '../logging/remoteLogger';

const DEVICE_ID_KEY = 'vass_reminder_device_id';
const CHANNEL_ID = 'vass-reminders';

Notifications.setNotificationHandler({
  handleNotification: async () => ({
    shouldShowBanner: true,
    shouldShowList: true,
    shouldPlaySound: true,
    shouldSetBadge: false,
    priority: Notifications.AndroidNotificationPriority.HIGH,
  }),
});

export interface ReminderDeviceContext {
  deviceId: string;
  timeZoneId: string;
}

export async function getReminderDeviceContext(): Promise<ReminderDeviceContext> {
  let deviceId: string | null = null;
  try {
    deviceId = await SecureStore.getItemAsync(DEVICE_ID_KEY);
  } catch {
    // Web preview has no SecureStore. It cannot schedule native reminders,
    // but still gets a stable-enough ID for the current browser session.
  }

  if (!deviceId) {
    deviceId = `device-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 14)}`;
    try {
      await SecureStore.setItemAsync(DEVICE_ID_KEY, deviceId);
    } catch {
      // Native builds persist it; web preview intentionally does not.
    }
  }

  return {
    deviceId,
    timeZoneId: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC',
  };
}

async function ensureNotificationPermission(): Promise<void> {
  if (Platform.OS === 'web') throw new Error('Локальные напоминания доступны только на телефоне');

  if (Platform.OS === 'android') {
    await Notifications.setNotificationChannelAsync(CHANNEL_ID, {
      name: 'Напоминания Vass',
      description: 'Локальные напоминания, работающие без интернета',
      importance: Notifications.AndroidImportance.HIGH,
      sound: 'default',
      vibrationPattern: [0, 300, 180, 300],
      lockscreenVisibility: Notifications.AndroidNotificationVisibility.PUBLIC,
    });
  }

  let permission = await Notifications.getPermissionsAsync();
  if (!permission.granted) permission = await Notifications.requestPermissionsAsync();
  if (!permission.granted) throw new Error('Разрешите уведомления для локальных напоминаний');
}

async function scheduleLocal(reminder: ReminderEvent): Promise<string> {
  await ensureNotificationPermission();
  const dueAt = new Date(reminder.dueAtUtc);
  if (!Number.isFinite(dueAt.getTime()) || dueAt.getTime() <= Date.now())
    throw new Error('Время напоминания уже прошло');

  return Notifications.scheduleNotificationAsync({
    content: {
      title: 'Vass напоминает',
      body: reminder.text,
      sound: 'default',
      data: { reminderId: reminder.id },
    },
    trigger: {
      type: Notifications.SchedulableTriggerInputTypes.DATE,
      date: dueAt,
      channelId: CHANNEL_ID,
    },
  });
}

export async function scheduleAndAcknowledgeReminder(
  reminder: ReminderEvent,
  deviceId: string,
): Promise<{ success: boolean; error?: string }> {
  if (Platform.OS === 'web')
    return { success: false, error: 'Локальные напоминания доступны только на телефоне' };

  const scheduled = await Notifications.getAllScheduledNotificationsAsync();
  const existing = scheduled.find(request =>
    request.identifier === reminder.localNotificationId ||
    request.content.data?.reminderId === reminder.id);
  if (existing) {
    try {
      await api.markReminderScheduled(reminder.id, deviceId, existing.identifier);
    } catch {
      // The OS-owned alarm is still valid; startup reconciliation retries.
    }
    return { success: true };
  }

  let localNotificationId: string;
  try {
    localNotificationId = await scheduleLocal(reminder);
  } catch (err) {
    const error = err instanceof Error ? err.message : String(err);
    try {
      await api.markReminderFailed(reminder.id, deviceId, error);
    } catch {
      // The local failure remains visible to the user even if its server ack
      // cannot be sent. Reconciliation retries on the next app start.
    }
    log('error', 'app', 'local reminder scheduling failed', { reminderId: reminder.id, error });
    return { success: false, error };
  }

  try {
    await api.markReminderScheduled(reminder.id, deviceId, localNotificationId);
  } catch (err) {
    // The alarm is already owned by the OS and will still fire offline.
    // Reconciliation finds it by reminderId and retries this ack later.
    log('warn', 'app', 'local reminder scheduled but acknowledgement failed', {
      reminderId: reminder.id,
      error: err instanceof Error ? err.message : String(err),
    });
  }
  log('info', 'app', 'local reminder scheduled', {
    reminderId: reminder.id,
    dueAtUtc: reminder.dueAtUtc,
  });
  return { success: true };
}

export async function reconcileLocalReminders(): Promise<void> {
  if (Platform.OS === 'web') return;

  const { deviceId } = await getReminderDeviceContext();
  const [serverReminders, localRequests] = await Promise.all([
    api.getReminders(deviceId),
    Notifications.getAllScheduledNotificationsAsync(),
  ]);

  for (const reminder of serverReminders) {
    const local = localRequests.find(request =>
      request.identifier === reminder.localNotificationId ||
      request.content.data?.reminderId === reminder.id);
    if (reminder.status === 'cancelled') {
      if (local) await Notifications.cancelScheduledNotificationAsync(local.identifier);
      await api.markReminderCancelled(reminder.id, deviceId);
      continue;
    }

    if (local) {
      if (reminder.deliveryStatus !== 'scheduled' || reminder.localNotificationId !== local.identifier) {
        await api.markReminderScheduled(reminder.id, deviceId, local.identifier);
      }
      continue;
    }

    await scheduleAndAcknowledgeReminder(reminder, deviceId);
  }
}
