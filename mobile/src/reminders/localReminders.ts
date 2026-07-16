import { Platform } from 'react-native';
import * as Notifications from 'expo-notifications';
import * as SecureStore from 'expo-secure-store';
import {
  api,
  type PeriodicReminderEvent,
  type ReminderEvent,
  type ReminderSyncItem,
} from '../api/client';
import { log } from '../logging/remoteLogger';

const DEVICE_ID_KEY = 'vass_reminder_device_id';
const PENDING_CANCELLATIONS_KEY = 'vass_pending_reminder_cancellations_v1';
// Android channel privacy cannot be tightened in-place after a channel has
// been created. A versioned ID makes upgraded installs use PRIVATE as well.
const CHANNEL_ID = 'vass-reminders-private-v2';
const PERIODIC_START_TOLERANCE_MS = 90_000;
const MINIMUM_MINUTE_INTERVAL = 15;

type SchedulableReminder = ReminderEvent | PeriodicReminderEvent | ReminderSyncItem;
type ReminderScheduleResult = { success: boolean; error?: string };

let reminderLifecycleQueue: Promise<void> = Promise.resolve();
const blockedReminderOwners = new Set<string>();
let allReminderOwnersBlocked = false;

function enqueueReminderLifecycle<T>(operation: () => Promise<T>): Promise<T> {
  const result = reminderLifecycleQueue.then(operation);
  reminderLifecycleQueue = result.then(() => undefined, () => undefined);
  return result;
}

interface LocalStartParts {
  month: number;
  day: number;
  hour: number;
  minute: number;
}

interface PendingReminderCancellation {
  ownerId: string;
  deviceId: string;
  reminderId: number;
}

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

async function loadPendingCancellations(): Promise<PendingReminderCancellation[]> {
  try {
    const raw = await SecureStore.getItemAsync(PENDING_CANCELLATIONS_KEY);
    if (!raw) return [];
    const values = JSON.parse(raw) as unknown;
    if (!Array.isArray(values)) return [];
    return values.filter((value): value is PendingReminderCancellation => {
      if (!value || typeof value !== 'object') return false;
      const item = value as Record<string, unknown>;
      return typeof item.ownerId === 'string' && typeof item.deviceId === 'string' &&
        typeof item.reminderId === 'number' && Number.isSafeInteger(item.reminderId) && item.reminderId > 0;
    }).slice(-100);
  } catch {
    return [];
  }
}

async function savePendingCancellations(items: PendingReminderCancellation[]): Promise<void> {
  try {
    if (items.length === 0) {
      await SecureStore.deleteItemAsync(PENDING_CANCELLATIONS_KEY);
    } else {
      await SecureStore.setItemAsync(PENDING_CANCELLATIONS_KEY, JSON.stringify(items.slice(-100)));
    }
  } catch {
    // Best effort: the OS alarm is already gone; elapsed intervals are still
    // never silently reanchored by reconciliation.
  }
}

async function rememberPendingCancellation(item: PendingReminderCancellation): Promise<void> {
  const items = await loadPendingCancellations();
  if (items.some(existing => existing.ownerId === item.ownerId &&
      existing.deviceId === item.deviceId && existing.reminderId === item.reminderId)) return;
  items.push(item);
  await savePendingCancellations(items);
}

async function flushPendingCancellations(ownerId: string, deviceId: string): Promise<Set<number>> {
  const items = await loadPendingCancellations();
  if (items.length === 0) return new Set();
  const remaining: PendingReminderCancellation[] = [];
  const stillPendingForDevice = new Set<number>();
  for (const item of items) {
    if (item.ownerId !== ownerId || item.deviceId !== deviceId) {
      remaining.push(item);
      continue;
    }
    try {
      await api.markReminderCancelled(item.reminderId, deviceId);
    } catch {
      remaining.push(item);
      stillPendingForDevice.add(item.reminderId);
    }
  }
  await savePendingCancellations(remaining);
  return stillPendingForDevice;
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
      lockscreenVisibility: Notifications.AndroidNotificationVisibility.PRIVATE,
    });
  }

  let permission = await Notifications.getPermissionsAsync();
  if (!permission.granted) permission = await Notifications.requestPermissionsAsync();
  if (!permission.granted) throw new Error('Разрешите уведомления для локальных напоминаний');
}

function isPeriodicReminder(reminder: SchedulableReminder): boolean {
  return 'rrule' in reminder || 'recurrenceRule' in reminder && reminder.recurrenceRule !== null;
}

function getRecurrenceRule(reminder: SchedulableReminder): string | null {
  if ('rrule' in reminder) return reminder.rrule;
  if ('recurrenceRule' in reminder) return reminder.recurrenceRule;
  return null;
}

function getStartAtUtc(reminder: SchedulableReminder): string {
  return 'startAtUtc' in reminder ? reminder.startAtUtc : reminder.dueAtUtc;
}

function getLocalStartParts(startAtUtc: string, timeZoneId: string): LocalStartParts {
  const start = new Date(startAtUtc);
  if (!Number.isFinite(start.getTime())) throw new Error('Некорректное время первого напоминания');
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: timeZoneId,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(start);
  const read = (type: Intl.DateTimeFormatPartTypes) => {
    const value = Number(parts.find(part => part.type === type)?.value);
    if (!Number.isInteger(value)) throw new Error('Не удалось прочитать локальное расписание');
    return value;
  };
  return { month: read('month'), day: read('day'), hour: read('hour'), minute: read('minute') };
}

function parseRule(rule: string): Record<string, string> {
  if (!rule || rule.length > 200 || /\s/.test(rule)) throw new Error('Неподдерживаемое правило повторения');
  const result: Record<string, string> = {};
  for (const part of rule.toUpperCase().split(';')) {
    const separator = part.indexOf('=');
    if (separator <= 0 || separator === part.length - 1) throw new Error('Неподдерживаемое правило повторения');
    const key = part.slice(0, separator);
    if (key in result) throw new Error('Неподдерживаемое правило повторения');
    result[key] = part.slice(separator + 1);
  }
  return result;
}

function isElapsedIntervalRule(rule: string | null): boolean {
  if (!rule) return false;
  const frequency = parseRule(rule).FREQ;
  return frequency === 'HOURLY' || frequency === 'MINUTELY';
}

function getScheduledReminderId(request: Notifications.NotificationRequest): number | null {
  const raw = request.content.data?.reminderId;
  const id = typeof raw === 'number' ? raw : typeof raw === 'string' && /^\d+$/.test(raw) ? Number(raw) : NaN;
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

function getTriggerChannelId(request: Notifications.NotificationRequest): string | null {
  const trigger = request.trigger;
  if (!trigger || typeof trigger !== 'object' || !('channelId' in trigger)) return null;
  return typeof trigger.channelId === 'string' ? trigger.channelId : null;
}

function buildPeriodicTrigger(
  reminder: SchedulableReminder,
): Notifications.SchedulableNotificationTriggerInput {
  const recurrenceRule = getRecurrenceRule(reminder);
  if (!recurrenceRule) throw new Error('Отсутствует правило повторения');
  const rule = parseRule(recurrenceRule);
  const start = getLocalStartParts(getStartAtUtc(reminder), reminder.timeZoneId);
  const keys = Object.keys(rule).sort().join(',');

  if (rule.FREQ === 'DAILY' && keys === 'FREQ') {
    return {
      type: Notifications.SchedulableTriggerInputTypes.DAILY,
      hour: start.hour,
      minute: start.minute,
      channelId: CHANNEL_ID,
    };
  }

  if (rule.FREQ === 'WEEKLY' && keys === 'BYDAY,FREQ') {
    const weekday = { SU: 1, MO: 2, TU: 3, WE: 4, TH: 5, FR: 6, SA: 7 }[rule.BYDAY];
    if (!weekday) throw new Error('Неподдерживаемый день недели');
    return {
      type: Notifications.SchedulableTriggerInputTypes.WEEKLY,
      weekday,
      hour: start.hour,
      minute: start.minute,
      channelId: CHANNEL_ID,
    };
  }

  if (rule.FREQ === 'MONTHLY' && keys === 'BYMONTHDAY,FREQ') {
    const day = Number(rule.BYMONTHDAY);
    if (!Number.isInteger(day) || day < 1 || day > 28 || day !== start.day)
      throw new Error('Неподдерживаемый день месяца');
    return {
      type: Notifications.SchedulableTriggerInputTypes.MONTHLY,
      day,
      hour: start.hour,
      minute: start.minute,
      channelId: CHANNEL_ID,
    };
  }

  if (rule.FREQ === 'YEARLY' && keys === 'BYMONTH,BYMONTHDAY,FREQ') {
    const month = Number(rule.BYMONTH);
    const day = Number(rule.BYMONTHDAY);
    if (!Number.isInteger(month) || month < 1 || month > 12 || month !== start.month ||
        !Number.isInteger(day) || day < 1 || day > 31 || day !== start.day ||
        month === 2 && day === 29) {
      throw new Error('Неподдерживаемая ежегодная дата');
    }
    return {
      type: Notifications.SchedulableTriggerInputTypes.YEARLY,
      month: month - 1,
      day,
      hour: start.hour,
      minute: start.minute,
      channelId: CHANNEL_ID,
    };
  }

  if ((rule.FREQ === 'HOURLY' || rule.FREQ === 'MINUTELY') && keys === 'FREQ,INTERVAL') {
    const interval = Number(rule.INTERVAL);
    const valid = Number.isInteger(interval) && (
      rule.FREQ === 'HOURLY'
        ? interval >= 1 && interval <= 168
        : interval >= MINIMUM_MINUTE_INTERVAL && interval <= 10_080
    );
    if (!valid) throw new Error('Неподдерживаемый интервал повторения');
    return {
      type: Notifications.SchedulableTriggerInputTypes.TIME_INTERVAL,
      seconds: interval * (rule.FREQ === 'HOURLY' ? 3600 : 60),
      repeats: true,
      channelId: CHANNEL_ID,
    };
  }

  throw new Error('Это правило повторения пока не поддерживается на телефоне');
}

async function scheduleLocal(reminder: SchedulableReminder, ownerId: string): Promise<string> {
  await ensureNotificationPermission();
  const startAt = new Date(getStartAtUtc(reminder));
  if (!Number.isFinite(startAt.getTime())) throw new Error('Некорректное время напоминания');

  const periodic = isPeriodicReminder(reminder);
  const trigger: Notifications.SchedulableNotificationTriggerInput = periodic
    ? buildPeriodicTrigger(reminder)
    : {
        type: Notifications.SchedulableTriggerInputTypes.DATE,
        date: startAt,
        channelId: CHANNEL_ID,
      };
  if (!periodic && startAt.getTime() <= Date.now()) throw new Error('Время напоминания уже прошло');

  // Expo TIME_INTERVAL always anchors its first fire to the moment the OS
  // trigger is registered. Once the original start has passed, recreating it
  // would silently shift every future occurrence.
  if (periodic && startAt.getTime() <= Date.now() && isElapsedIntervalRule(getRecurrenceRule(reminder))) {
    throw new Error('Первый запуск интервального напоминания уже прошёл; создайте расписание заново');
  }

  if (periodic && startAt.getTime() > Date.now()) {
    const nextTrigger = await Notifications.getNextTriggerDateAsync(trigger);
    if (nextTrigger === null || Math.abs(nextTrigger - startAt.getTime()) > PERIODIC_START_TOLERANCE_MS) {
      throw new Error('Первый запуск не соответствует правилу повторения');
    }
  }

  return Notifications.scheduleNotificationAsync({
    content: {
      title: 'Vass напоминает',
      body: reminder.text,
      sound: 'default',
      data: {
        reminderId: reminder.id,
        ownerId,
        vassType: periodic ? 'periodic_reminder' : 'reminder',
        recurrenceRule: getRecurrenceRule(reminder),
      },
    },
    trigger,
  });
}

async function scheduleAndAcknowledgeReminderInternal(
  reminder: SchedulableReminder,
  deviceId: string,
  ownerId: string,
): Promise<ReminderScheduleResult> {
  if (allReminderOwnersBlocked || blockedReminderOwners.has(ownerId))
    return { success: false, error: 'Локальные напоминания остановлены после выхода из аккаунта' };
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
    localNotificationId = await scheduleLocal(reminder, ownerId);
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
    startAtUtc: getStartAtUtc(reminder),
    recurrenceRule: getRecurrenceRule(reminder),
  });
  return { success: true };
}

export function scheduleAndAcknowledgeReminder(
  reminder: SchedulableReminder,
  deviceId: string,
  ownerId: string,
): Promise<ReminderScheduleResult> {
  if (allReminderOwnersBlocked || blockedReminderOwners.has(ownerId))
    return Promise.resolve({ success: false, error: 'Локальные напоминания остановлены после выхода из аккаунта' });
  return enqueueReminderLifecycle(() => scheduleAndAcknowledgeReminderInternal(reminder, deviceId, ownerId));
}

async function cancelLocalReminderInternal(reminderId: number, ownerId: string): Promise<void> {
  if (Platform.OS === 'web') return;
  const { deviceId } = await getReminderDeviceContext();
  const [requests, presented] = await Promise.all([
    Notifications.getAllScheduledNotificationsAsync(),
    Notifications.getPresentedNotificationsAsync(),
  ]);

  for (const request of requests) {
    if (getScheduledReminderId(request) !== reminderId) continue;
    const requestOwnerId = typeof request.content.data?.ownerId === 'string'
      ? request.content.data.ownerId
      : null;
    if (requestOwnerId !== null && requestOwnerId !== ownerId) continue;
    await Notifications.cancelScheduledNotificationAsync(request.identifier);
  }

  for (const notification of presented) {
    if (getScheduledReminderId(notification.request) !== reminderId) continue;
    const notificationOwnerId = typeof notification.request.content.data?.ownerId === 'string'
      ? notification.request.content.data.ownerId
      : null;
    if (notificationOwnerId !== null && notificationOwnerId !== ownerId) continue;
    await Notifications.dismissNotificationAsync(notification.request.identifier);
  }

  try {
    await api.markReminderCancelled(reminderId, deviceId);
  } catch {
    await rememberPendingCancellation({ ownerId, deviceId, reminderId });
  }
  log('info', 'app', 'local reminder cancelled by Vass', { reminderId });
}

// Used both by the management screen and by the server-driven tool receipt.
// The server is authoritative about cancellation; this function only removes
// this device's OS notifications and persists a retryable acknowledgement.
export function cancelLocalReminder(reminderId: number, ownerId: string): Promise<void> {
  return enqueueReminderLifecycle(() => cancelLocalReminderInternal(reminderId, ownerId));
}

async function cancelAllLocalRemindersForSignedOutUserInternal(
  ownerId: string | null,
): Promise<void> {
  if (Platform.OS === 'web') return;
  const { deviceId } = await getReminderDeviceContext();
  const [requests, presented] = await Promise.all([
    Notifications.getAllScheduledNotificationsAsync(),
    Notifications.getPresentedNotificationsAsync(),
  ]);
  for (const request of requests) {
    const reminderId = getScheduledReminderId(request);
    if (reminderId === null) continue;
    const cancellationOwnerId = typeof request.content.data?.ownerId === 'string'
      ? request.content.data.ownerId
      : ownerId;
    try {
      await Notifications.cancelScheduledNotificationAsync(request.identifier);
      log('info', 'app', 'local reminder cancelled after sign-out', { reminderId });
      // Persist locally before auth state disappears. The owner-scoped ACK is
      // flushed before this owner ever reconciles again, so logout stays fast
      // and works offline without reanchoring elapsed intervals later.
      if (cancellationOwnerId) {
        await rememberPendingCancellation({ ownerId: cancellationOwnerId, deviceId, reminderId });
      }
    } catch (err) {
      log('warn', 'app', 'could not cancel local reminder after sign-out', {
        reminderId,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
  for (const notification of presented) {
    const reminderId = getScheduledReminderId(notification.request);
    if (reminderId === null) continue;
    try {
      await Notifications.dismissNotificationAsync(notification.request.identifier);
    } catch (err) {
      log('warn', 'app', 'could not dismiss presented reminder after sign-out', {
        reminderId,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}

export function cancelAllLocalRemindersForSignedOutUser(
  ownerId: string | null,
): Promise<void> {
  if (ownerId) blockedReminderOwners.add(ownerId);
  else allReminderOwnersBlocked = true;
  return enqueueReminderLifecycle(() =>
    cancelAllLocalRemindersForSignedOutUserInternal(ownerId));
}

export function blockReminderSchedulingForOwner(ownerId: string | null): void {
  if (ownerId) blockedReminderOwners.add(ownerId);
  else allReminderOwnersBlocked = true;
}

async function reconcileLocalRemindersInternal(ownerId: string): Promise<void> {
  if (Platform.OS === 'web') return;
  if (allReminderOwnersBlocked || blockedReminderOwners.has(ownerId)) return;

  const { deviceId } = await getReminderDeviceContext();
  const pendingCancellationIds = await flushPendingCancellations(ownerId, deviceId);
  const [serverReminders, localRequests] = await Promise.all([
    api.getReminders(deviceId),
    Notifications.getAllScheduledNotificationsAsync(),
  ]);

  // The device ID is intentionally stable across account switches. New OS
  // requests carry their owner, so a previous family member's alarms cannot
  // survive a switch. Periodic rows are fully represented by protocol v2 and
  // can also be orphan-cleaned. One-shot rows are intentionally different:
  // the server omits sufficiently old rows while Android Doze may still have
  // a delayed alarm pending, so absence from this list is not proof that a
  // one-shot is orphaned.
  const currentReminderIds = new Set(serverReminders.map(reminder => reminder.id));
  for (const request of localRequests) {
    const reminderId = getScheduledReminderId(request);
    const requestOwnerId = typeof request.content.data?.ownerId === 'string'
      ? request.content.data.ownerId
      : null;
    const isPeriodic = request.content.data?.vassType === 'periodic_reminder';
    const belongsToAnotherUser = requestOwnerId !== null && requestOwnerId !== ownerId;
    const isCurrentPeriodicOrphan = requestOwnerId === ownerId && isPeriodic &&
      reminderId !== null && !currentReminderIds.has(reminderId);
    if (reminderId !== null && (belongsToAnotherUser || isCurrentPeriodicOrphan)) {
      await Notifications.cancelScheduledNotificationAsync(request.identifier);
      if (belongsToAnotherUser && requestOwnerId !== null) {
        await rememberPendingCancellation({ ownerId: requestOwnerId, deviceId, reminderId });
      }
      log('info', 'app', 'orphaned local reminder cancelled', { reminderId });
    }
  }

  for (const reminder of serverReminders) {
    if (pendingCancellationIds.has(reminder.id)) continue;
    let local = localRequests.find(request =>
      request.identifier === reminder.localNotificationId ||
      getScheduledReminderId(request) === reminder.id);

    if (reminder.status === 'cancelled') {
      if (local) await Notifications.cancelScheduledNotificationAsync(local.identifier);
      await api.markReminderCancelled(reminder.id, deviceId);
      continue;
    }

    // Existing Android channels keep their original visibility forever.
    // Reschedule current alarms onto the versioned PRIVATE channel.
    const canMigrateChannel = new Date(reminder.dueAtUtc).getTime() > Date.now() ||
      reminder.recurrenceRule !== null && !isElapsedIntervalRule(reminder.recurrenceRule);
    if (local && Platform.OS === 'android' && getTriggerChannelId(local) !== CHANNEL_ID && canMigrateChannel) {
      await Notifications.cancelScheduledNotificationAsync(local.identifier);
      log('info', 'app', 'local reminder channel migrated', { reminderId: reminder.id });
      local = undefined;
    }

    if (local) {
      if (reminder.deliveryStatus !== 'scheduled' || reminder.localNotificationId !== local.identifier) {
        await api.markReminderScheduled(reminder.id, deviceId, local.identifier);
      }
      continue;
    }

    const schedule: SchedulableReminder = reminder.recurrenceRule
      ? {
          contractVersion: 2,
          id: reminder.id,
          text: reminder.text,
          startAtUtc: reminder.dueAtUtc,
          timeZoneId: reminder.timeZoneId,
          rrule: reminder.recurrenceRule,
          localNotificationId: reminder.localNotificationId,
        }
      : {
          id: reminder.id,
          text: reminder.text,
          dueAtUtc: reminder.dueAtUtc,
          timeZoneId: reminder.timeZoneId,
          localNotificationId: reminder.localNotificationId,
        };
    await scheduleAndAcknowledgeReminderInternal(schedule, deviceId, ownerId);
  }
}

export function reconcileLocalReminders(ownerId: string): Promise<void> {
  allReminderOwnersBlocked = false;
  blockedReminderOwners.delete(ownerId);
  return enqueueReminderLifecycle(() => reconcileLocalRemindersInternal(ownerId));
}
