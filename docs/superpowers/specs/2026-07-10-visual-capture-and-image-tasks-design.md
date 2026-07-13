# Фотофиксация и visual tasks — design

## Статус и implementation plan

- **Статус:** approved; реализация не начата.
- **Implementation plan:** [`../plans/2026-07-13-visual-capture-and-image-tasks.md`](../plans/2026-07-13-visual-capture-and-image-tasks.md).
- План уточняет устаревшие после появления общего `ConversationRuntime` детали: pending visual принадлежит runtime, клиент передает owner-scoped UUID asset, а storage filename не становится API-контрактом.

## Контекст

После добавления нового аватара следующий естественный слой для Vass — дать пользователю показать ассистенту реальный мир: сфотографировать предмет, сделать селфи, выбрать картинку/скриншот/изображение из галереи или файлов, задать голосом вопрос и получить действие/ответ в том же разговорном цикле.

Это не должно быть отдельной OCR-фичей. В старой web-версии уже есть `/api/v1/chat/ocr-image`, но он умеет только извлекать текст. Новая фича должна быть мультимодальной частью диалога:

- "Что это?"
- "Прочитай, что тут написано."
- "Переведи этот экран."
- "Что мне нажать дальше?"
- "Это выглядит нормально?"
- "Запомни, где я положил ключи."
- "Посмотри на это селфи, нормально ли выглядит?"
- "Что пользователь просит сделать на этом скриншоте?"

Основной принцип: пользователь прикрепляет одно изображение к следующей голосовой/текстовой реплике, а ассистент отвечает как обычно — с сохранением истории, TTS и текущей companion-поверхности.

## External Docs Checked

- Expo ImagePicker: `expo-image-picker` даёт системный UI для выбора изображений/видео и съёмки фото камерой; для SDK 57 рекомендованная версия `~57.0.2`.
- Expo DocumentPicker: `expo-document-picker` даёт выбор документов из провайдеров устройства; для SDK 57 рекомендованная версия `~57.0.0`; при работе с `expo-file-system` важно `copyToCacheDirectory: true`.
- Gemini Image Understanding: Gemini принимает изображения как input и поддерживает captioning, classification, visual question answering, OCR-подобные задачи; для небольших файлов можно передавать base64 inline image data, для больших/переиспользуемых — Files API.

## Product Direction

Фича должна ощущаться как "показать Ольге/Максиму картинку", а не как загрузчик файлов.

Главный сценарий:

1. Пользователь нажимает маленькую кнопку визуального ввода.
2. Выбирает источник: камера, селфи, галерея, файл/скриншот.
3. Видит компактный preview изображения.
4. Говорит, что нужно сделать с изображением.
5. Vass отправляет голосовую реплику + изображение одним conversational turn.
6. Ассистент отвечает голосом и текстом, как в обычном режиме.

Если пользователь прикрепил изображение и ничего не сказал, можно отправить default prompt: "Посмотри на изображение и кратко скажи, что на нём, и чем можешь помочь."

## UX

### Entry Point

Не ломать текущий `VoiceControlDock` с тремя большими зонами. Микрофон остаётся главным действием.

Новая кнопка:

- маленькая icon-first кнопка `camera/image`;
- размещение: рядом с `ConversationPeek` или над нижним dock справа;
- не должна конкурировать с центральной микрофонной кнопкой;
- скрывается/disabled только когда нет `sessionId`.

### Visual Source Sheet

По нажатию открывается dark bottom sheet:

| Action | Client API |
| --- | --- |
| `Сфотографировать` | `ImagePicker.launchCameraAsync({ mediaTypes: ['images'], cameraType: 'back' })` |
| `Селфи` | `ImagePicker.launchCameraAsync({ mediaTypes: ['images'], cameraType: 'front' })` |
| `Из галереи` | `ImagePicker.launchImageLibraryAsync({ mediaTypes: ['images'] })` |
| `Файл / скриншот` | `DocumentPicker.getDocumentAsync({ type: 'image/*', copyToCacheDirectory: true })` |

Первый релиз — одно изображение за раз. Multi-image сравнение ("сравни два фото") отдельно позже.

### Pending Image State

После выбора изображения:

- показываем маленький thumbnail;
- текст: `Изображение прикреплено`;
- secondary text: `Скажите, что сделать`;
- кнопка `×` для удаления pending image;
- optional quick action: `Разобрать изображение`.

Pending image живёт только до следующего успешного `/chat/send`.

Если пользователь уходит в настройки/историю, pending image сохраняется в состоянии `HomeScreen`, но не загружается заново после перезапуска приложения.

### Conversation States

| State | Behavior |
| --- | --- |
| `idle` / `recording` | pending image будет отправлен со следующей репликой |
| `thinking` | visual button disabled, чтобы не менять turn in flight |
| `speaking` | visual button можно оставить enabled, но выбранное изображение станет pending для следующего turn |
| `paused` | visual button enabled; изображение можно подготовить, но отправка произойдёт только после resume/следующего voice turn |

## Architecture

### Keep Voice Loop Primary

`useVoiceChat` уже отвечает за сложный audio/VAD/turn-taking цикл. Не надо переписывать его в "chat composer".

Минимальное изменение:

```ts
interface PendingVisualInput {
  imageFileName: string;
  localUri: string;
  mimeType: string;
}
```

`HomeScreen` хранит `pendingVisual`.

`useVoiceChat` получает опциональный pending visual:

```ts
useVoiceChat(sessionId, {
  pendingVisual,
  onPendingVisualConsumed,
})
```

При следующем `sendMessage(...)` hook добавляет `imageFileName` в `SendMessageParams`. После успешного send вызывает `onPendingVisualConsumed()`.

Альтернатива — держать visual полностью снаружи и иметь отдельный `sendVisualTurn`, но тогда появится второй conversation pipeline. Для первого релиза лучше встроить image attachment в существующий `/chat/send`.

### Mobile Dependencies

Добавить:

```text
expo-image-picker
expo-document-picker
```

Опционально позже:

```text
expo-image-manipulator
```

`expo-image-manipulator` нужен только если реальные фото часто превышают лимит/слишком долго грузятся. В MVP достаточно `ImagePicker` quality/compression + backend size limit.

### Mobile API

`mobile/src/api/client.ts`:

```ts
export interface UploadedImage {
  fileName: string;
  mimeType: string;
  sizeBytes: number;
}

uploadImage(uri: string, mimeType?: string, originalName?: string): Promise<UploadedImage>
```

Как и `uploadAudio`, использовать:

- `new File(uri).bytes()`;
- `new ExpoBlob([bytes], { type: mimeType })`;
- `Object.defineProperty(blob, 'name', { value: originalName || 'image.jpg' })`;
- multipart field name: `file`.

`SendMessageParams` расширить:

```ts
export interface SendMessageParams {
  sessionId: number;
  message: string;
  audioFileName?: string;
  imageFileName?: string;
}
```

### Backend Endpoints

#### `POST /api/v1/chat/upload-image`

Multipart:

```text
file: image/*
```

Rules:

- authenticated;
- size `1 byte .. 10 MB` for MVP;
- allow only `image/jpeg`, `image/png`, `image/webp`;
- `image/heic` / `image/heif`: reject with user-friendly message until we add conversion;
- store under a new configured media path, not inside `wwwroot`;
- filename is generated GUID, not user-provided;
- return `{ fileName, mimeType, sizeBytes }`.

Why not reuse `/ocr-image`: that endpoint performs immediate OCR and returns text; visual task needs attachment persistence and later use with `/chat/send`.

#### Extend `POST /api/v1/chat/send`

Current:

```csharp
public record SendRequest(int SessionId, string Message, string? AudioFileName = null);
```

New:

```csharp
public record SendRequest(
    int SessionId,
    string Message,
    string? AudioFileName = null,
    string? ImageFileName = null
);
```

Flow:

1. If `AudioFileName` exists and `Message` is empty, transcribe audio exactly as now.
2. If `ImageFileName` exists:
   - validate safe file name;
   - validate file exists;
   - validate mime type from stored metadata or extension map;
   - read bytes and pass to Gemini as image part;
   - prepend a system/task instruction that the image is part of the user's current request.
3. Save user message with text plus a lightweight image marker.
4. Stream assistant response through the same SSE contract.
5. Save assistant response.

No separate "analyze image first, then ask chat" step. That would lose the user's intent and make the assistant sound like OCR software.

### Gemini Service

Current `GeminiService.StreamResponseAsync` is text-only:

```csharp
parts = new[] { new { text = m.Content } }
```

Add a multimodal path instead of hacking image into text:

```csharp
public record GeminiPart(string? Text, string? MimeType, byte[]? Data);
public record GeminiContent(string Role, IReadOnlyList<GeminiPart> Parts);

StreamMultimodalResponseAsync(
  string systemPrompt,
  List<GeminiContent> contents,
  ...
)
```

For MVP, use the same REST family already used in the project and inline base64 image data:

```json
{
  "role": "user",
  "parts": [
    { "inline_data": { "mime_type": "image/jpeg", "data": "..." } },
    { "text": "Пользователь просит: ..." }
  ]
}
```

Later, if images become large/reused, switch image transport to Gemini Files API.

### Prompting

For image turns, add a short multimodal instruction before the normal companion prompt:

```text
Пользователь приложил изображение к текущей реплике.
Отвечай на просьбу пользователя с учётом изображения.
Если пользователь просит распознать текст — извлеки только релевантный текст и объясни его простыми словами.
Если пользователь просит действие — помоги выполнить ближайший безопасный шаг.
Если на изображении не хватает информации — скажи, чего не видно, и задай один уточняющий вопрос.
Не утверждай с уверенностью то, что плохо видно.
```

Examples:

| User asks | Expected behavior |
| --- | --- |
| "Что это?" | describe object/context briefly, ask if user needs help |
| "Прочитай" | OCR-like extraction with structure |
| "Переведи" | extract text then translate |
| "Что нажать?" | UI/screenshot guidance |
| "Запомни, где это лежит" | summarize visual fact in text; future memory integration later |
| "Как я выгляжу?" | gentle non-medical/non-identity-sensitive feedback |

## Data Model

Prefer a new attachment table rather than adding many nullable columns to `Message`.

```csharp
public class MessageAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public string Kind { get; set; } = "image";
    public string FileName { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string? OriginalFileName { get; set; }
    public string? VisualSummary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

`Message`:

```csharp
public ICollection<MessageAttachment> Attachments { get; set; } = [];
```

`VisualSummary` is generated in the same call or immediately after response generation. It should be short and factual, for future history/memory:

```text
Фото: тёмный пульт на столе; пользователь спрашивал, какая кнопка включает меню.
```

History API should return attachments so `ChatHistoryScreen` can show thumbnails later. MVP can show only text marker first.

## Persistence And Cleanup

Use a separate storage root:

```text
Visual:Path=/app/data/images
```

Docker:

- add a volume for images, similar in spirit to audio;
- do not serve image files publicly through nginx.

Access:

- `GET /api/v1/chat/image/{fileName}` checks current user owns the attachment through `ChatSession -> Message -> Attachment`;
- no public file URLs.

Orphans:

- first release can tolerate rare orphan files when upload succeeds but send is cancelled, same pattern as audio;
- add explicit `DELETE /api/v1/chat/image/{fileName}` when user removes pending image before send;
- background orphan cleanup is not required in MVP.

## Privacy And Safety

This feature will receive screenshots, faces, documents and personal rooms. Treat it as sensitive:

1. Never auto-upload camera frames.
2. Upload only after explicit user selection/capture.
3. Show thumbnail before the image becomes part of a turn.
4. Let user remove pending image before sending.
5. Keep images authenticated and user-owned.
6. Log file metadata, not image contents/base64.
7. Avoid face recognition / identity claims.
8. For medical/legal/financial images, explain uncertainty and suggest professional verification.

## UI Components

### `VisualInputButton`

Small icon button on Home screen.

Props:

```ts
interface VisualInputButtonProps {
  disabled: boolean;
  onPress: () => void;
}
```

### `VisualSourceSheet`

Dark action sheet with four choices:

- camera;
- selfie;
- gallery;
- file/screenshot.

### `PendingVisualPreview`

Thumbnail + short state:

```text
Изображение прикреплено
Скажите, что сделать
```

Actions:

- remove;
- optional send default analysis.

### `VisualUploadState`

A tiny state machine in `HomeScreen`:

```ts
type VisualUploadState =
  | { status: 'idle' }
  | { status: 'picking' }
  | { status: 'uploading'; localUri: string }
  | { status: 'ready'; localUri: string; imageFileName: string; mimeType: string }
  | { status: 'error'; message: string };
```

## Implementation Phases

### Phase 1: Upload + Pending UI

1. Add `expo-image-picker`, `expo-document-picker`.
2. Add `VisualInputButton`, `VisualSourceSheet`, `PendingVisualPreview`.
3. Implement `api.uploadImage`.
4. Implement `POST /chat/upload-image`.
5. No Gemini integration yet: upload + preview + remove only.

### Phase 2: Multimodal Send

1. Extend `SendMessageParams` / `SendRequest`.
2. Add multimodal Gemini service path.
3. Send pending image with next voice turn.
4. Stream response through existing SSE parser.
5. Save user/assistant messages as today.

### Phase 3: History + Attachments

1. Add `MessageAttachment` EF migration.
2. Save image attachment metadata.
3. Return attachments from `GET /chat/sessions/{id}`.
4. Show thumbnail/marker in `ChatHistoryScreen`.

### Phase 4: Polish

1. User-friendly permission errors.
2. HEIC handling or explicit unsupported-format message.
3. Optional client-side compression/downscale.
4. Better prompt presets: "прочитай", "переведи", "что нажать".
5. Visual summary integration with medium-term/long-term memory later.

## Not In This Feature

- Continuous camera/live video.
- Multi-image comparison.
- Background object monitoring.
- Real external actions like ordering/purchasing without separate tool integrations.
- Face identity recognition.
- Medical diagnosis from images.
- Full document/PDF understanding; image screenshots only for this pass.

## Acceptance Criteria

1. User can take a photo with rear camera.
2. User can take a selfie with front camera.
3. User can choose an image from gallery.
4. User can choose an image/screenshot from file provider.
5. Selected image appears as pending preview and can be removed before sending.
6. Next voice turn sends the image and the transcribed request together.
7. Assistant answers the actual user request, not only OCR.
8. Existing voice tap/long-press/pause/resume behavior remains unchanged.
9. Images are stored per-user and are not public.
10. Unsupported/too-large images fail with a clear Russian message.
11. `npx tsc --noEmit` passes in `mobile/`.
12. `dotnet build` passes for API.
13. Manual test on device confirms camera/gallery permissions and real upload.

## Source References

- Expo ImagePicker: https://docs.expo.dev/versions/latest/sdk/imagepicker/
- Expo DocumentPicker: https://docs.expo.dev/versions/latest/sdk/document-picker/
- Gemini image understanding: https://ai.google.dev/gemini-api/docs/image-understanding
