# Visual Capture и image tasks - Implementation Plan

> План реализации принятой design-spec `docs/superpowers/specs/2026-07-10-visual-capture-and-image-tasks-design.md`. Чекбоксы обновляются по мере выполнения, а не все разом в конце.

**Goal:** Пользователь может сфотографировать объект или себя, выбрать изображение из галереи/файлов, прикрепить его к следующей голосовой реплике и получить обычный потоковый ответ Vass с учетом изображения. Изображение остается приватным, принадлежит пользователю, появляется в истории и в будущем переиспользуется 7.4 screen analysis.

**Architecture:** Изображение сначала становится отдельным user-owned `VisualAsset` с непрозрачным UUID. `MessageAttachment` связывает asset с каждой пользовательской репликой, которая реально отправляла это изображение. Backend передает только текущее изображение в Gemini как `inline_data`; старая история остается текстовой. Pending visual хранится в `ConversationRuntimeProvider`, а `useVoiceChat` захватывает его в начале конкретной попытки и очищает только после успешного terminal `[DONE]` актуальной попытки.

**Tech Stack:** ASP.NET Core 10, EF Core 10, PostgreSQL, Gemini REST/SSE, React Native 0.86, Expo SDK 57, `expo-image-picker`, `expo-document-picker`, `expo-file-system`, `expo/fetch`, authenticated file streaming.

## Implementation Status (2026-07-13)

- [x] Backend persistence, private authenticated storage, upload/content/delete API and database migration.
- [x] Multimodal Gemini request for the current turn only, with attachment history and session cleanup.
- [x] Mobile pickers, pending-image runtime bridge, voice-turn attachment and history thumbnails.
- [x] Backend unit/integration tests, TypeScript and Expo configuration checks, signed arm64 release APK.
- [ ] Deploy the backend migration to VPS and smoke-test capture on a physical device.

## Decisions That Supersede The Older Draft

- Клиент передает `visualAssetId: UUID`, а не серверное имя файла. Storage filename никогда не является публичным API-контрактом.
- Ownership создается при upload, до появления `Message`. Это закрывает cross-user reuse/read/delete и дает безопасное удаление pending asset.
- Используются две сущности: `VisualAsset` (файл и владелец) и `MessageAttachment` (связь с сообщением). Один asset можно безопасно повторно сослать из superseding attempts текущего voice turn.
- Pending visual принадлежит `ConversationRuntimeProvider`, а не `HomeScreen`: настройки, история и overlay не создают второй pipeline и не теряют выбранное изображение.
- Attachment очищается на клиенте только после `[DONE]` текущей попытки. Оборванный SSE или model error оставляет preview для повтора.
- Исторические изображения не отправляются в Gemini повторно. В multimodal request попадает только asset текущего turn; текстовая память работает как раньше.
- `VisualSummary` и отдельный vision side-call не входят в первый релиз. Поле не добавляем до появления конкретного потребителя в memory/local-first runtime.
- Quick action без голоса (`Описать изображение`) не входит в первый релиз: он потребует отдельного text-turn entry в сложный voice state machine. Пользователь прикрепляет изображение и говорит задачу.
- Старый `/chat/ocr-image` пока остается для совместимости, но mobile Visual Capture его не использует.
- MediaProjection и анализ чужого экрана - отдельная фаза 7.4. Этот план обязан оставить reusable upload/runtime contract для ее реализации.

## Global Constraints

- Один asset за один пользовательский turn. Multi-image comparison не входит в MVP.
- Допустимые форматы upload: JPEG, PNG, WebP. GIF, HEIC/HEIF, SVG, PDF и файлы с поддельным `Content-Type` отклоняются понятной ошибкой.
- Максимальный размер содержимого: 10 MiB. Request-body limit учитывает multipart overhead: `MaxVisualRequestBodySize = MaxVisualSize + 64 KiB`.
- Сервер проверяет magic bytes, UUID, owner и containment path. Имя клиента хранится только как metadata с ограничением длины и никогда не участвует в построении пути.
- Файлы хранятся вне `wwwroot`, не публикуются nginx и читаются только через authenticated endpoint с ownership check.
- В логах разрешены asset ID, MIME, размер и timing. Запрещены base64, байты, thumbnail и распознанный приватный текст.
- Camera/gallery/file picker запускаются только после явного действия пользователя. Никакого background capture или автоматической загрузки.
- Текущие VAD, interruption, continuation, overlay pause/resume и TTS semantics не меняются.
- Все новые backend endpoints покрываются integration tests. Gemini JSON contract покрывается unit tests. Mobile MVP проходит TypeScript, Expo config и physical-device smoke.

---

## Task 1: Domain Model And Migration

**Files:**
- Create: `VoiceAssistant.API/Data/Entities/VisualAsset.cs`
- Create: `VoiceAssistant.API/Data/Entities/MessageAttachment.cs`
- Modify: `VoiceAssistant.API/Data/Entities/Message.cs`
- Modify: `VoiceAssistant.API/Data/AppDbContext.cs`
- Create: `VoiceAssistant.API/Migrations/<timestamp>_AddVisualAssets.cs`
- Create: `VoiceAssistant.API/Migrations/<timestamp>_AddVisualAssets.Designer.cs`
- Modify: `VoiceAssistant.API/Migrations/AppDbContextModelSnapshot.cs`

**Target model:**

```csharp
public class VisualAsset
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public User User { get; set; } = null!;
    public string StorageFileName { get; set; } = null!;
    public string MimeType { get; set; } = null!;
    public long SizeBytes { get; set; }
    public string? OriginalFileName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<MessageAttachment> MessageAttachments { get; set; } = [];
}

public class MessageAttachment
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public Guid VisualAssetId { get; set; }
    public VisualAsset VisualAsset { get; set; } = null!;
    public string Kind { get; set; } = "image";
}
```

- [ ] Add both `DbSet`s and `Message.Attachments`.
- [ ] Configure cascade `Message -> MessageAttachment`, restrict `VisualAsset -> MessageAttachment`, and cascade `User -> VisualAsset`.
- [ ] Add unique index `(MessageId, VisualAssetId)`, unique `StorageFileName`, and lookup index `(UserId, CreatedAt)`.
- [ ] Limit `MimeType` to 50, `StorageFileName` to 80, `OriginalFileName` to 255 and `Kind` to 20 chars.
- [ ] Generate `AddVisualAssets` migration; inspect `Up`, `Down`, FKs and indexes rather than editing snapshot by hand.
- [ ] Run `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj`.

**Commit:** `feat: add visual asset persistence`

---

## Task 2: Image Validation And Private Storage

**Files:**
- Create: `VoiceAssistant.API/Services/ImageContentInspector.cs`
- Create: `VoiceAssistant.API/Services/VisualAssetService.cs`
- Modify: `VoiceAssistant.API/Controllers/ChatController.cs`
- Modify: `VoiceAssistant.API/Program.cs`
- Modify: `VoiceAssistant.API/appsettings.json`
- Modify: `docker-compose.yml`
- Modify: `VoiceAssistant.API.Tests/ChatControllerTests.cs`
- Create: `VoiceAssistant.API.Tests/ImageContentInspectorTests.cs`
- Create: `VoiceAssistant.API.Tests/VisualAssetServiceTests.cs`

- [ ] Move magic-byte recognition out of `ChatController.TryDetectImageMimeType` into `ImageContentInspector`; keep `/ocr-image` on the same implementation so security rules do not diverge.
- [ ] Detect JPEG, PNG, WebP and GIF, but expose a Visual Capture allowlist containing only JPEG/PNG/WebP.
- [ ] Implement `VisualAssetService.ResolveRootPath`, safe generated filename/path resolution, atomic temp-write + move, read and delete.
- [ ] On DB failure after file write, delete the newly written file. On file-delete failure after DB cleanup, log metadata only and do not expose the path to the client.
- [ ] Add `Visual:Path` with local default `visual` and production `Visual__Path=/app/visual`.
- [ ] Add Docker named volume `visual:/app/visual`.
- [ ] Register the service in DI.
- [ ] Extend `/api/health/ready` with a separate `visual_storage` writable probe. Do not merge it into the existing audio `storage` field, so production diagnostics identify the failed volume.
- [ ] Unit-test valid signatures, spoofed content, too-short files, safe generated paths and traversal rejection.
- [ ] Run `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`.

**Commit:** `feat: add private visual asset storage`

---

## Task 3: Authenticated Visual Asset API

**Files:**
- Create: `VoiceAssistant.API/Controllers/VisualAssetsController.cs`
- Modify: `VoiceAssistant.API.IntegrationTests/TestWebApplicationFactory.cs`
- Create: `VoiceAssistant.API.IntegrationTests/VisualAssetsControllerTests.cs`

**API contract:**

```text
POST   /api/v1/chat/visual-assets
GET    /api/v1/chat/visual-assets/{id}/content
DELETE /api/v1/chat/visual-assets/{id}
```

Upload response:

```json
{
  "id": "f8b4...uuid",
  "mimeType": "image/jpeg",
  "sizeBytes": 123456
}
```

- [ ] Implement multipart upload with `[RequestSizeLimit(MaxVisualRequestBodySize)]` and a second `file.Length` limit.
- [ ] Read at most 10 MiB, validate actual bytes, generate UUID/storage filename and persist owner metadata.
- [ ] Return Russian user-facing errors for empty, oversized, unsupported and HEIC/HEIF inputs. Do not echo original filename in errors/logs.
- [ ] Implement authenticated content streaming by `(assetId, currentUserId)`; unknown and foreign assets both return `404` to avoid ownership disclosure.
- [ ] Implement DELETE only when the asset belongs to the user and has no `MessageAttachment`. Attached assets return `409`; unknown/foreign assets return `404`.
- [ ] Give integration tests an isolated temporary visual root through configuration and remove it in fixture disposal.
- [ ] Test unauthenticated upload, valid upload, spoofed content, oversized body, cross-user read/delete, pending delete and attached-delete conflict.
- [ ] Run `dotnet test VoiceAssistant.API.IntegrationTests/VoiceAssistant.API.IntegrationTests.csproj`.

**Commit:** `feat: expose authenticated visual asset api`

---

## Task 4: Multimodal Gemini Contract

**Files:**
- Modify: `VoiceAssistant.API/Services/GeminiService.cs`
- Modify: `VoiceAssistant.API.Tests/GeminiServiceTests.cs`
- Modify: `VoiceAssistant.API.IntegrationTests/FakeGeminiHandler.cs`

**Target contract:** Keep every existing text caller source-compatible while allowing structured parts for one message.

```csharp
public record GeminiPart(string? Text = null, string? MimeType = null, byte[]? Data = null);

public record GeminiMessage(string Role, IReadOnlyList<GeminiPart> Parts)
{
    public GeminiMessage(string role, string content)
        : this(role, [new GeminiPart(Text: content)]) { }
}
```

- [ ] Serialize text parts as `{ "text": "..." }` and image parts as `{ "inline_data": { "mime_type": "...", "data": "<base64>" } }`.
- [ ] Reject invalid parts before HTTP: empty text-only part, missing MIME/data pair, unsupported MIME and payload beyond 10 MiB.
- [ ] Preserve current role mapping, grounding, timeout, SSE parsing and `GeminiApiException` behavior.
- [ ] Add unit test proving the old text request JSON is unchanged.
- [ ] Add unit test proving a multimodal message contains text plus correct base64/MIME and never stringifies property names with the wrong casing.
- [ ] Make `FakeGeminiHandler` read text parts defensively instead of assuming every part has `text`; return a visual-specific canned reply when `inline_data` exists.
- [ ] Run both backend test projects.

**Commit:** `feat: support multimodal gemini messages`

---

## Task 5: Attach Visual Asset To The Existing Chat Turn

**Files:**
- Modify: `VoiceAssistant.API/Controllers/ChatController.cs`
- Modify: `VoiceAssistant.API.IntegrationTests/ChatControllerTests.cs`
- Modify: `VoiceAssistant.API.IntegrationTests/ChatControllerConcurrencyTests.cs`

**Request change:**

```csharp
public record SendRequest(
    int SessionId,
    string Message,
    string? AudioFileName = null,
    string? DeviceId = null,
    string? TimeZoneId = null,
    bool SupportsExternalActions = false,
    Guid? VisualAssetId = null);
```

- [ ] Resolve `VisualAssetId` only by `(id, currentUserId)` and verify its file before saving the user message. Unknown/foreign/missing-file assets return `400` without leaking ownership/path details.
- [ ] Persist `MessageAttachment` in the same `SaveChangesAsync` as the user `Message`.
- [ ] Build history as text-only exactly as today, then replace only the current user message with text + current image bytes for the main Gemini call.
- [ ] Add the visual-task instruction to `systemPrompt`: answer the actual request, acknowledge uncertainty, avoid face identity claims and avoid medical/legal/financial certainty.
- [ ] Keep reminder parsing, external-action classification, long-term-memory extraction and custom-instruction side calls text-only. They must not receive base64.
- [ ] Permit reuse of the same owned asset by superseding attempts in the same session; create one unique join per user message. This is required by the current continuation model.
- [ ] Return attachment DTOs from paginated `GET /chat/sessions/{id}`: `{ id, kind, mimeType, sizeBytes }` where `id` is the visual asset UUID.
- [ ] On session deletion, collect referenced asset IDs, delete the session, then delete files/asset rows that have no remaining joins. Shared/reused assets remain until the last join is gone.
- [ ] Add integration tests for multimodal send, attachment persistence/history, foreign asset rejection, missing file, superseding reuse and session cleanup.
- [ ] Verify terminal ordering remains `persist assistant -> stats -> [DONE]`.
- [ ] Run both backend test projects and `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj`.

**Commit:** `feat: add visual assets to chat turns`

---

## Task 6: Mobile Dependencies And API Client

**Files:**
- Modify: `mobile/package.json`
- Modify: `mobile/package-lock.json`
- Modify: `mobile/app.json`
- Modify: `mobile/src/api/client.ts`

- [ ] Install SDK-compatible picker modules with `npx expo install expo-image-picker expo-document-picker`.
- [ ] Install the existing-design-compatible icon path (`lucide-react-native` plus SDK-compatible `react-native-svg`) rather than adding new emoji controls.
- [ ] Configure Russian camera/photo permission copy in Expo config. Do not request camera permission at startup.
- [ ] Add `VisualAssetDto`, `ChatAttachment`, `ChatMessage.attachments` and `SendMessageParams.visualAssetId`.
- [ ] Add `api.uploadVisual(uri, mimeType?, originalName?)` using the proven `File.bytes()` + `ExpoBlob` multipart pattern from `uploadAudio`.
- [ ] Parse backend upload errors so unsupported/oversized formats show the server's Russian message instead of generic `Upload failed: 400`.
- [ ] Add `api.deletePendingVisual(id)` and an authenticated image source helper for React Native `<Image>`.
- [ ] Change `sendMessage` so EOF without terminal `[DONE]` throws an interrupted-stream error. A partial stream must not consume pending visual.
- [ ] Run `npx tsc --noEmit` and `npx expo config --type public`.

**Commit:** `feat: add mobile visual asset api`

---

## Task 7: Runtime-Owned Pending Visual State

**Files:**
- Create: `mobile/src/visual/types.ts`
- Create: `mobile/src/hooks/useVisualInput.ts`
- Modify: `mobile/src/context/ConversationRuntimeContext.tsx`
- Modify: `mobile/src/hooks/useVoiceChat.ts`

**State:**

```ts
type VisualInputState =
  | { status: 'idle' }
  | { status: 'picking' }
  | { status: 'uploading'; localUri: string }
  | { status: 'ready'; localUri: string; assetId: string; mimeType: string; sizeBytes: number }
  | { status: 'error'; message: string; previous?: PendingVisualInput };
```

- [ ] `useVisualInput` owns source picking, upload, replacement, remove and stale-operation guards. A late upload result may not overwrite a newer choice or an unmounted runtime.
- [ ] Keep the previous ready asset until a replacement upload succeeds; failed replacement must not silently discard it.
- [ ] Expose pending state/actions through `ConversationRuntimeValue`; do not put state in `HomeScreen`.
- [ ] Give `useVoiceChat` a ref-based bridge (`getPendingVisual`, `consumePendingVisual`) so async turn code reads the current asset without rebuilding recorder callbacks.
- [ ] Capture asset ID at the start of each send attempt and include it in both first and superseding requests while it remains current.
- [ ] Consume only after `sendMessage` receives `[DONE]`, the generation/turn is still current and the pending ID still equals the captured ID. A newer image selected during `speaking` must survive completion of the older turn.
- [ ] Keep pending visual on no-speech, upload error, Gemini error, aborted SSE, barge-in and superseded attempt.
- [ ] Remove calls DELETE only for a pending asset. Treat `409 already attached` as locally removable, because the server has already retained it with history.
- [ ] Expose one reusable `stageVisualAsset({ uri, mimeType, originalName })` method for future MediaProjection 7.4.
- [ ] Run `npx tsc --noEmit`.

**Commit:** `feat: integrate pending visual with conversation runtime`

---

## Task 8: Visual Input UI

**Files:**
- Create: `mobile/src/components/VisualInputButton.tsx`
- Create: `mobile/src/components/VisualSourceSheet.tsx`
- Create: `mobile/src/components/PendingVisualPreview.tsx`
- Modify: `mobile/src/screens/HomeScreen.tsx`
- Modify: `mobile/src/theme/amoled.ts`

- [ ] Add a compact icon-only image button near `ConversationPeek`, visually secondary to the central microphone. Provide accessibility label and tooltip/pressed feedback where supported.
- [ ] Implement a React Native `Modal` bottom sheet with Camera, Selfie, Gallery and File/Screenshot rows using Lucide icons.
- [ ] Use back camera, front camera, image library and document picker respectively; copy selected files to cache where required.
- [ ] Show a stable-aspect thumbnail, `Изображение прикреплено`, `Скажите, что сделать`, upload/error state and an icon-only remove action.
- [ ] Disable opening/replacing/removing while `thinking`, `picking` or `uploading`. Allow selection during `speaking` for the next turn and during `paused` for the next resumed turn.
- [ ] Preserve current three-zone `VoiceControlDock`, avatar tap/long-press and layout dimensions. The new control must not shift the avatar or dock when state changes.
- [ ] Handle permission denied/cancel as nonfatal state; cancel closes the sheet without an error banner.
- [ ] Verify small Android viewport, large font scaling and both avatars without overlap.
- [ ] Run `npx tsc --noEmit`.

**Commit:** `feat: add visual capture controls`

---

## Task 9: Attachments In History

**Files:**
- Create: `mobile/src/components/ChatAttachmentThumbnail.tsx`
- Modify: `mobile/src/screens/ChatHistoryScreen.tsx`
- Modify: `mobile/src/api/client.ts`

- [ ] Render image thumbnails inside the owning user bubble, before message text; do not create a nested card.
- [ ] Load through authenticated endpoint and show a fixed-size placeholder on 401/404/network failure without changing row height.
- [ ] Keep pagination unchanged and avoid re-fetch loops when FlatList recycles rows.
- [ ] Add accessibility label `Прикрепленное изображение` and optional full-screen preview only if it can be implemented without expanding MVP risk; thumbnail itself is required.
- [ ] Verify messages without attachments render byte-for-byte as before.
- [ ] Run `npx tsc --noEmit`.

**Commit:** `feat: show visual attachments in history`

---

## Task 10: End-To-End Verification And Deployment

**Automated gate:**

- [ ] `dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj`
- [ ] `dotnet test VoiceAssistant.API.IntegrationTests/VoiceAssistant.API.IntegrationTests.csproj`
- [ ] `dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj`
- [ ] `npx tsc --noEmit` from `mobile/`
- [ ] `npx expo config --type public` from `mobile/`
- [ ] `docker compose config`
- [ ] WSL Android release build for `arm64-v8a`
- [ ] `apksigner verify --verbose --print-certs`; signer must remain compatible with the currently installed APK.

**VPS deployment gate:**

- [ ] Back up PostgreSQL before migration.
- [ ] Deploy API + nginx/compose changes, run EF migration and verify `/api/health/ready` reports both `storage=ok` and `visual_storage=ok`.
- [ ] Verify visual Docker volume survives API container recreation.
- [ ] Verify nginx accepts a real 10 MiB-bound multipart request and does not serve `/app/visual` directly.
- [ ] Run authenticated production smoke with a synthetic non-sensitive image; verify cross-user URL access returns `404`.

**Physical-device acceptance:**

- [ ] Rear camera photo -> preview -> spoken question -> streamed text/TTS answer.
- [ ] Front camera selfie -> safe non-identity response.
- [ ] Gallery image and file-provider screenshot both work.
- [ ] Remove before send deletes pending server asset; cancel picker changes nothing.
- [ ] JPEG, PNG and WebP work; HEIC/HEIF, PDF, spoofed and >10 MiB inputs fail clearly.
- [ ] Silence/no-speech keeps attachment pending for retry.
- [ ] Barge-in, continuation, pause/resume and overlay transitions remain functional with a pending image.
- [ ] Selecting a second image during `speaking` keeps it for the next turn instead of clearing it with the previous turn.
- [ ] History shows authenticated thumbnail after app restart; another account cannot read it.
- [ ] 20-turn regression session contains ordinary voice turns before/after visual turns with no TTS or recorder degradation.

**Commit:** `docs: mark visual capture implementation complete` after all gates pass.

---

## Exit Criteria

1. All four sources (rear camera, selfie, gallery, file/screenshot) create one visible pending image.
2. The next successful voice turn sends text/audio intent and current image through the existing `/chat/send` SSE pipeline.
3. Gemini answers the user's actual visual task; TTS, interruption and continuation behavior remain intact.
4. Visual assets are UUID-addressed, owner-scoped, private, format/size validated and absent from logs.
5. History returns and renders authenticated attachments; session deletion cleans unreferenced files.
6. Backend unit/integration suites, mobile typecheck, release APK and VPS readiness pass.
7. The reusable staging/upload contract is ready for 7.4 MediaProjection without introducing another attachment API or conversation loop.

## Explicitly Out Of Scope

- MediaProjection/screen capture (7.4).
- Continuous camera or video understanding.
- Multiple images in one turn or image comparison.
- PDF/document parsing and HEIC conversion.
- Face identification, medical diagnosis or automatic high-stakes decisions.
- Visual memory summary/embedding and long-term image retrieval.
- Public image URLs, anonymous sharing or caregiver access.
- Local-first encrypted attachment migration (Phase 8).
