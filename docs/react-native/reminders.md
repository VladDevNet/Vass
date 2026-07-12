# Напоминания: server-managed, locally scheduled

Статус: `implemented`, требуется физическая проверка Android после новой
native-сборки.

## Архитектура

MCP не используется. Gemini — только интерпретатор естественного языка:
обычный backend side-call возвращает строгий JSON с текстом и локальной
датой. Сервер хранит канонический `Reminder`, но за срабатывание отвечает
системный планировщик телефона через `expo-notifications`.

```text
голос -> ChatController -> ReminderService/Gemini JSON
      -> Reminder + pending ReminderDelivery
      -> SSE reminder event
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

Сейчас поддерживаются одноразовые напоминания с однозначными датой и временем.
Повторения требуют уточнения и не создаются молча.

## API

| Метод | Endpoint | Назначение |
|---|---|---|
| `GET` | `/api/v1/reminders?deviceId=...` | Reconciliation для устройства |
| `POST` | `/api/v1/reminders/{id}/scheduled` | Подтверждение локального alarm ID |
| `POST` | `/api/v1/reminders/{id}/failed` | Фиксация локальной ошибки |
| `POST` | `/api/v1/reminders/{id}/cancelled` | Подтверждение удаления local alarm |
| `DELETE` | `/api/v1/reminders/{id}` | Отмена канонического напоминания |

## Физическая матрица перед закрытием

- Android: экран выключен, приложение background/смахнуто из recent apps;
- airplane mode после установки;
- перезагрузка телефона;
- Doze/battery saver с exact access и без него;
- отказ в notification permission;
- смена timezone;
- iOS на физическом устройстве, когда появится доступный Mac.
