# Напоминания: server-managed, locally scheduled

Статус: одноразовые напоминания и protocol-v2 периодические напоминания
реализованы в коде; требуется физическая проверка Android/iOS после новой
native-сборки.

## Архитектура

MCP не используется. Основная модель вызывает allowlisted инструменты:

- `reminder_create(text, dueAtLocal)` для одного срабатывания;
- `periodic_reminder_create(text, startAtLocal, rrule)` для повторения.

Tool broker независимо валидирует device/timezone, будущий первый запуск и
ограниченное RRULE-подмножество. Сервер хранит канонический `Reminder`, но за
срабатывание отвечает системный планировщик телефона через
`expo-notifications`.

```text
голос -> model tool proposal -> AssistantToolBroker -> ReminderService
      -> Reminder + pending ReminderDelivery
      -> SSE reminder | periodicReminder event
      -> expo-notifications scheduleNotificationAsync
      -> localNotificationId -> POST scheduled
      -> только после ack ассистент подтверждает установку
```

После успешной локальной установки интернет для срабатывания не нужен.
Уведомление принадлежит Android/iOS и может появиться при закрытом приложении
и выключенном экране. Полный динамический TTS при убитом приложении не
гарантируется ОС; уведомление использует системный звук и текст, а Vass
открывается по нажатию.

## Надёжность

- стабильный `deviceId` хранится в `expo-secure-store`;
- timezone берётся с устройства и передаётся parser-у;
- periodic event выдаётся только клиенту с `ReminderProtocolVersion >= 2`;
  старый клиент не может молча превратить серию в одно срабатывание;
- web не объявляет native reminder capability, даже если знает timezone;
- `clientTurnId` задаёт единый reminder side-effect slot: повтор HTTP-turn
  возвращает уже созданное расписание, даже если модель заново вычислила
  относительное время иначе;
- неоднозначное/несуществующее DST-время не планируется — ассистент просит
  уточнение;
- сервер ждёт device ack до 30 секунд (включая первый permission prompt) и не говорит «готово» без статуса
  `scheduled`;
- при потере ack локальный alarm остаётся действующим;
- на старте reconciliation сравнивает серверные записи с реальной очередью
  `getAllScheduledNotificationsAsync()` и восстанавливает потерянное;
- Android permission `RECEIVE_BOOT_COMPLETED` добавляется библиотекой;
- `SCHEDULE_EXACT_ALARM` объявлен для точного времени. Если special access не
  выдан, Expo использует inexact fallback, который может быть задержан Doze.

## Периодический контракт V1

Модель передаёт ближайший точный первый запуск `startAtLocal` в формате
`yyyy-MM-ddTHH:mm:ss` и RRULE без префикса `RRULE:`. Поддерживается только
подмножество, которое превращается ровно в один cross-platform OS trigger:

| RRULE | Native trigger | Ограничение |
|---|---|---|
| `FREQ=DAILY` | `DAILY` | каждый день в local wall-clock time |
| `FREQ=WEEKLY;BYDAY=SU` | `WEEKLY` | ровно один weekday |
| `FREQ=MONTHLY` | `MONTHLY` | день месяца 1–28 |
| `FREQ=YEARLY` | `YEARLY` | кроме 29 февраля |
| `FREQ=HOURLY;INTERVAL=N` | `TIME_INTERVAL` | N=1..168; elapsed time |
| `FREQ=MINUTELY;INTERVAL=N` | `TIME_INTERVAL` | N=15..10080; elapsed time |

Для calendar rules `startAtLocal` обязан быть ближайшим реальным occurrence.
Для hourly/minutely он должен находиться ровно через один interval: Expo не
умеет отдельно задавать далёкую дату старта повторяющегося interval.
Минимум 15 минут выбран из-за ограничения Android Doze: более частый cadence
нельзя честно гарантировать в фоне. Если первый interval уже пропущен, клиент
не сдвигает расписание молча, а отклоняет восстановление и просит создать его
заново.
`COUNT`, `UNTIL`, несколько `BYDAY`, каждый N-й день/неделю/месяц, последний
день месяца и произвольные RRULE отклоняются с уточнением. Они не игнорируются
молча.

На общем телефоне выход из аккаунта отменяет все Vass-owned локальные alarms.
Device-level delivery cancellation сначала надёжно кладётся в локальную
pending-очередь, а затем подтверждается до reconciliation при следующем входе
этого владельца; logout поэтому не зависит от сети. Каноническая запись и
другие устройства не затрагиваются. Обычная сетевая ошибка и истечение JWT
(`401`) не считаются logout: уже установленные alarms продолжают работать
локально.
Новые OS-записи содержат owner ID. При смене аккаунта reconciliation удаляет
записи другого владельца и также ставит их device cancellation в очередь.
Отсутствующие на сервере periodic series удаляются,
но отсутствие просроченного one-shot в sync-ответе не считается orphan-
сигналом: Android Doze всё ещё мог задержать его показ. Android использует
versioned `PRIVATE` notification channel, потому что privacy уже созданного
channel нельзя ужесточить in-place.

При повторном входе владельца future one-shot и calendar periodic series
восстанавливаются. Past elapsed series (`HOURLY`/`MINUTELY`) остаётся
остановленной на этом device: восстановить её фазу одним Expo trigger нельзя,
а незаметно начать новый отсчёт от момента входа небезопасно. Пользователь
может явно создать такую серию заново; другие devices не затрагиваются.

Calendar triggers следуют локальным часам устройства; interval triggers
измеряют elapsed seconds и могут смещаться относительно wall-clock после DST.
Регистрация в OS не означает абсолютную минутную точность: Android Doze/
exact-alarm policy и iOS Focus могут задерживать показ.

## API

| Метод | Endpoint | Назначение |
|---|---|---|
| `GET` | `/api/v1/reminders?deviceId=...&protocolVersion=2` | Reconciliation; active periodic series выдаются только v2-клиенту |
| `POST` | `/api/v1/reminders/{id}/scheduled` | Подтверждение локального alarm ID |
| `POST` | `/api/v1/reminders/{id}/failed` | Фиксация локальной ошибки |
| `POST` | `/api/v1/reminders/{id}/cancelled` | Подтверждение удаления local alarm / device suspension |
| `DELETE` | `/api/v1/reminders/{id}` | Отмена канонического напоминания |

## Физическая матрица перед закрытием

- Android: экран выключен, приложение background/смахнуто из recent apps;
- airplane mode после установки;
- перезагрузка телефона;
- Doze/battery saver с exact access и без него;
- отказ в notification permission;
- смена timezone;
- daily/weekly/monthly/yearly и `FREQ=HOURLY;INTERVAL=2`;
- несоответствие `startAtLocal` правилу, unsupported RRULE и rollback;
- iOS на физическом устройстве, когда появится доступный Mac.
