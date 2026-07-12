# Vass - Development Guide

Vass is a personal voice assistant. The assistant persona is Olga: a warm Russian-speaking voice companion for short, natural conversations, everyday help, and hands-free voice interaction. Primary audience is older users — the design favors patience and simplicity over speed or feature density.

The repository was originally copied from a Polish tutor prototype. Legacy tutor/vocabulary/onboarding code has been removed (see `docs/AUDIT-LEGACY.md` for the full audit and what was deleted vs. deliberately kept). The product client is a React Native mobile app (`mobile/`) — see `docs/react-native/` for its architecture, backlog, and build instructions. The old browser PWA was removed (PROJECT-AUDIT-2026-07-10 ARCH-01); the only browser UI is now the protected React admin SPA in `admin/`.

## Current Product

Core user flow: the user speaks, the mobile app records/detects voice activity, the backend transcribes and runs the turn through Gemini (with Google Search grounding), and the reply is spoken back — on-device via the system TTS engine (`expo-speech`), not a network round-trip. A self-hosted Piper TTS service remains as a server-side fallback (`POST /api/v1/chat/tts`, `/tts_stream`). Real turn-taking (deciding whether a pause means the user is done vs. still thinking) and interruption handling live entirely in the mobile client — see `docs/react-native/audio-and-vad.md` and `docs/react-native/tts-and-avatar.md` for the current algorithm, which has evolved through several PRs since the initial design.

Secondary features:

- Text chat with SSE streaming, independent of voice mode.
- Camera/image OCR through Gemini.
- Per-user long-term memory: Gemini embeddings + pgvector semantic retrieval, with user-owned view/delete endpoints.
- Local-first reminders: server-side language/date parsing plus OS-scheduled mobile notifications that fire offline.
- Per-user API keys (Gemini/OpenAI/Anthropic key fields exist in settings for future use, but only Gemini is actually consumed today, and are encrypted at rest — PROJECT-AUDIT-2026-07-10 SEC-03) and a custom system prompt, which the assistant can update itself when a user asks it to remember a behavioral preference ("говори медленнее").

## Technology Stack

### Backend

| Area | Technology |
| --- | --- |
| Runtime | .NET 10 |
| Web API | ASP.NET Core |
| Auth | ASP.NET Core Identity + JWT (issuer/audience `Vass`) |
| ORM | Entity Framework Core |
| Database | PostgreSQL 17 + pgvector 0.8.2 |
| LLM chat | Gemini API (`gemini-3.5-flash`, Google Search grounding) |
| Memory embeddings | Gemini API (`gemini-embedding-2`, 768 dimensions) |
| Audio transcription/completeness check | Gemini API (`gemini-2.5-flash`) |
| Neural TTS (server-side fallback) | Self-hosted Piper (`ru_RU-irina-medium`), raw PCM streamed |
| Audio conversion | ffmpeg |

### Mobile Client

Expo / React Native / TypeScript, in `mobile/`. See `docs/react-native/architecture.md` for the client-side stack and `docs/react-native/BUILD-WSL.md` / `docs/react-native/BUILD-MACOS.md` for building and installing on a physical device.

### Admin Client

React 19 / Vite / TypeScript, in `admin/`. It is a separate operational SPA served at `/admin/`, not a public product client. See `docs/ADMIN-PANEL.md` for access provisioning, endpoints, and local development.

### Infrastructure

| Service | Purpose |
| --- | --- |
| `api` | ASP.NET Core backend on port `5000`, has a real Docker healthcheck against `/api/health` |
| `db` | PostgreSQL 17 |
| `tts` | Self-hosted Piper TTS (Flask wrapper around the Piper CLI) — server-side fallback, mobile's primary TTS path is on-device |
| `admin` | nginx container serving the compiled React admin SPA |
| `nginx` | Gateway for `/api/*` and `/admin/`; the API prefix passes through unchanged and the admin prefix is stripped before proxying to its static container |
| `audio` volume | Persists uploaded user audio |

`speaker-id` (SpeechBrain-based voice identification) is in `docker-compose.yml` but opt-in only (`profiles: ["speaker-id"]` -- `docker compose up` alone never starts it, and `api` does not `depends_on` it); the standalone Silero TTS quality comparison that used to live in `silero-tts/` was removed entirely, having lost its comparison against Piper (PROJECT-AUDIT-2026-07-10 OPS-02). See below.

## Repository Layout

```text
.
├── VoiceAssistant.API/
│   ├── Controllers/
│   │   ├── AuthController.cs        # api/v1/auth — register, login, device-link, current user
│   │   ├── ChatController.cs        # api/v1/chat — sessions, SSE chat, audio, TTS, OCR
│   │   ├── ClientLogsController.cs  # api/v1/client-logs — mobile client log ingest (dev tooling)
│   │   ├── MemoryController.cs      # api/v1/memory — view/delete personal memory facts
│   │   ├── RemindersController.cs   # api/v1/reminders — per-device schedule sync/ack
│   │   └── SettingsController.cs    # api/v1/settings — per-user profile, API keys, custom prompt
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Entities/                # ChatSession, Message, UserSettings, MemoryFact, Reminder/Delivery
│   ├── Migrations/
│   ├── Prompts/
│   │   └── companion-system.txt     # Olga's system prompt
│   ├── Services/
│   │   ├── GeminiService.cs         # LLM streaming + Google Search grounding
│   │   ├── PiperTtsService.cs       # self-hosted TTS, buffered + streaming
│   │   ├── CompanionPromptService.cs
│   │   ├── LongTermMemoryService.cs # fact extraction + pgvector retrieval
│   │   ├── ReminderService.cs       # typed reminder parsing and delivery handshake
│   │   ├── AudioAnalysisService.cs  # transcription + utterance-completeness check
│   │   ├── SpeakerIdService.cs      # paused feature — see "Paused Features" below
│   │   ├── SpeakerPendingStore.cs
│   │   └── SpeakerRegistryService.cs
│   ├── Dockerfile
│   └── VoiceAssistant.API.csproj
├── VoiceAssistant.API.Tests/         # xUnit unit tests
├── VoiceAssistant.API.IntegrationTests/  # WebApplicationFactory-based integration tests (PROJECT-AUDIT-2026-07-10 QA-01)
├── piper-tts/                        # Flask wrapper around the Piper TTS CLI
├── speaker-id/                       # SpeechBrain ECAPA-TDNN voice-ID microservice (paused feature, opt-in compose profile)
├── postgres-vector/                  # Alpine PG17 + pgvector build, preserves existing volume UID
├── mobile/                           # React Native client — see docs/react-native/
├── admin/                            # React + Vite admin SPA — see docs/ADMIN-PANEL.md
├── docker-compose.yml
├── nginx.conf
├── .env.example
├── docs/
│   ├── AUDIT-LEGACY.md              # legacy-cleanup audit (mostly executed)
│   ├── ADMIN-PANEL.md                # admin access, API, operation, and future usage accounting
│   └── react-native/                # mobile app architecture, backlog, build guides
└── DEVELOPMENT.md
```

## Backend Architecture

### Startup

`VoiceAssistant.API/Program.cs` configures:

- PostgreSQL `AppDbContext`.
- ASP.NET Identity with relaxed password rules for early development.
- JWT bearer authentication (issuer/audience `Vass`).
- Shared `IHttpClientFactory`.
- Application services: Gemini, Piper TTS, companion prompt, audio analysis, speaker-ID (call site is real, uncommented code — `SpeakerRegistryService.IdentifyAsync` no-ops immediately unless `Features:SpeakerIdentificationEnabled` is set, see below).
- Controller routing, `/api/health`, and `/api/health/ready`.
- Admin role bootstrap from `Admin:Email`; no password is stored in configuration.
- EF Core migrations on startup (`db.Database.Migrate()`) — skipped under a `"Testing"` environment name, which the integration test project uses instead of a real Postgres database (PROJECT-AUDIT-2026-07-10 QA-01).

### Active Controllers

| Controller | Base route | Purpose |
| --- | --- | --- |
| `AuthController` | `/api/v1/auth` | Register, login, device-link (elderly-friendly no-password login), current user |
| `AdminController` | `/api/v1/admin` | Admin-only user activity, access approval, search, sorting, and pagination |
| `ChatController` | `/api/v1/chat` | Sessions, SSE chat, audio upload/playback, TTS, utterance-completeness check, OCR |
| `ClientLogsController` | `/api/v1/client-logs` | Batched log ingest from the mobile client — development-stage debugging tooling |
| `MemoryController` | `/api/v1/memory` | View and delete the authenticated user's long-term memory facts |
| `RemindersController` | `/api/v1/reminders` | Reconcile and acknowledge OS-scheduled reminders per device |
| `SettingsController` | `/api/v1/settings` | Per-user profile, API keys, custom system prompt |

## Main API Surface

### Auth

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/v1/auth/register` | Creates user and returns JWT |
| `POST` | `/api/v1/auth/login` | Returns JWT |
| `POST` | `/api/v1/auth/device-link` | Auth required. Generates a short-lived 6-digit code so an already-logged-in device can log a new one in without typing a password |
| `POST` | `/api/v1/auth/device-link/redeem` | Redeems a `device-link` code for a JWT |
| `GET` | `/api/v1/auth/me` | Current user data |

### Chat And Voice

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/chat/sessions` | Returns the single current dialog session |
| `GET` | `/api/v1/chat/sessions/{id}` | Loads messages (paginated) |
| `PATCH` | `/api/v1/chat/sessions/{id}` | Renames a session |
| `DELETE` | `/api/v1/chat/sessions/{id}` | Deletes session and linked audio files |
| `POST` | `/api/v1/chat/send` | Streams assistant response as SSE-style `data:` events |
| `POST` | `/api/v1/chat/upload-audio` | Uploads `audio/*` up to 5 MB |
| `GET` | `/api/v1/chat/audio/{fileName}` | Returns user-owned uploaded audio |
| `POST` | `/api/v1/chat/tts` | Generates a full WAV clip via Piper (server-side fallback) |
| `POST` | `/api/v1/chat/tts_stream` | Streams raw PCM as it's synthesized (server-side fallback) |
| `POST` | `/api/v1/chat/check-utterance` | Transcribes a snapshot and judges complete vs. still-thinking (turn-taking) |
| `POST` | `/api/v1/chat/ocr-image` | Extracts text from image with Gemini |

### Settings

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/settings` | Loads masked API keys and preferences |
| `PATCH` | `/api/v1/settings` | Partially updates profile/keys/custom prompt — only fields present in the request body are changed; omitted fields are left untouched server-side (PROJECT-AUDIT-2026-07-10 API-01, replacing a previous whole-object `PUT` that had a lost-update contract). An empty string explicitly clears `displayName`/`assistantName`/`customSystemPrompt`/a BYOK key to null; `null`/omitted leaves the field alone. |
| `GET` | `/api/v1/settings/default-prompt` | Returns Olga's current system prompt |

### Long-Term Memory

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/memory/facts` | Lists the authenticated user's active facts; embeddings are never exposed |
| `DELETE` | `/api/v1/memory/facts/{id}` | Deletes one user-owned fact |
| `DELETE` | `/api/v1/memory/facts` | Clears all long-term memory for the current user |

### Reminders

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/reminders?deviceId=...` | Active reminders plus this device's local schedule state |
| `POST` | `/api/v1/reminders/{id}/scheduled` | Confirms the OS-owned local notification identifier |
| `POST` | `/api/v1/reminders/{id}/failed` | Records a device scheduling failure |
| `DELETE` | `/api/v1/reminders/{id}` | Cancels the canonical reminder |

### Client Logs

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/v1/client-logs/batch` | Ingests a batch of log entries from the mobile client (`mobile/src/logging/remoteLogger.ts`) — fire-and-forget, development-stage debugging tooling for diagnosing the voice loop against a real device |

Entries are retained for 30 days (`ClientLogRetentionService`, PROJECT-AUDIT-2026-07-10 DATA-01) — a background service checked once on startup and every 24h thereafter, deleting anything older by its server-received timestamp.

### Administration

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/admin/overview` | User, activity, message, and character totals |
| `GET` | `/api/v1/admin/users` | Search, approval filter, sorting, and pagination |
| `PATCH` | `/api/v1/admin/users/{id}/approval` | Approves or blocks an account and invalidates its existing JWTs |

### Health

Two separate endpoints (PROJECT-AUDIT-2026-07-10 REL-03):

- `GET /api/health` — liveness. Always returns a plain `"healthy"` body if the process is up; no dependency checks. This is what the Docker healthcheck and `docker-compose.yml`'s `depends_on: condition: service_healthy` gates point at — deliberately cheap so a slow/degraded dependency never makes Docker restart an otherwise-healthy container.
- `GET /api/health/ready` — readiness. Checks real DB connectivity, that the audio volume is actually writable, and Gemini configuration presence (never a paid API call) — any of those failing returns HTTP 503 with `"status":"not_ready"`. TTS is checked too but reported as its own `checks.tts` field (`"ok"`/`"degraded"`) without affecting the overall status. This is what **external** uptime monitoring should point at instead of `/api/health`, since liveness alone can't tell you the user-facing chat flow actually works.

Anything outside `/api/` and `/admin/` returns nginx's own default 404 — there is no public static site behind it (ARCH-01).

## Streaming Events

`POST /api/v1/chat/send` writes SSE-style lines. Happy path (PROJECT-AUDIT-2026-07-10 REL-02 -- note `stats` and `[DONE]` were reordered from an earlier version of this doc: the assistant reply is saved and `stats` is sent BEFORE `[DONE]`, since some clients stop reading the instant they see it):

```text
data: {"transcription":"..."}
data: {"reminder":{"id":1,"text":"позвонить врачу","dueAtUtc":"...","timeZoneId":"Europe/Warsaw"}}
data: {"preamble":"..."}
data: {"text":"..."}
data: {"stats":{"convertMs":0,"transcribeMs":0,"speakerIdMs":0,"llmFirstTokenMs":0,"llmTotalMs":0,"translationMs":0}}
data: [DONE]
```

On an AI/provider infrastructure failure (missing key, non-2xx from Gemini, connection error -- PROJECT-AUDIT-2026-07-10 REL-04), the stream instead ends with a single error event and no `[DONE]`:

```text
data: {"error":"...","retryable":true}
```

`preamble` is an optional short "hold on, let me think/search" phrase, spoken if a fast side-call decides the real response will take a while (search grounding, complex reasoning). The mobile client parses these events in `mobile/src/api/client.ts`.

## Audio Pipeline

The mobile client owns the full voice loop (VAD, turn-taking, barge-in, shadow-capture) — see `docs/react-native/audio-and-vad.md` and `docs/react-native/tts-and-avatar.md` for the current client-side algorithm, which has changed significantly since the initial design (completeness-check-first turn-taking was replaced with optimistic sending — see `mobile/src/hooks/useVoiceChat.ts`). The backend's role in each turn is fixed regardless of how the client decides a turn is ready:

```text
POST /api/v1/chat/upload-audio, then POST /api/v1/chat/send with audioFileName
  -> ffmpeg webm -> wav -> Gemini transcription -> Gemini streaming answer
  -> spoken via on-device TTS (mobile) or streamed Piper PCM (server-side fallback)
```

Audio storage:

- Docker default: `/app/audio`, mounted as the `audio` volume.
- Local default: `VoiceAssistant.API/audio`.
- Override with `Audio:Path` or `Audio__Path`.

## Configuration

Environment variables used by Docker (see `.env.example`):

```env
GEMINI_API_KEY=AIza...
DB_PASSWORD=your_secure_password
JWT_SECRET=your_jwt_secret_min_32_chars_long_here
ENCRYPTION_KEY=your_encryption_secret_min_16_chars_here
REGISTRATION_AUTO_APPROVE=true
ADMIN_EMAIL=admin@example.com
LONG_TERM_MEMORY_ENABLED=true
```

Key usage:

- `GEMINI_API_KEY` is required for chat, audio transcription, utterance-completeness checks, OCR, fact extraction, and memory embeddings, unless the user saves a personal Gemini key in settings.
- `JWT_SECRET`/`Jwt:Issuer`/`Jwt:Audience` (`Vass`) — changing the issuer/audience invalidates all previously-issued tokens.
- `ENCRYPTION_KEY` encrypts per-user BYOK API keys at rest (PROJECT-AUDIT-2026-07-10 SEC-03).
- `REGISTRATION_AUTO_APPROVE` defaults to `true`; set it to `false` only after the mobile pending-approval UX is implemented.
- `ADMIN_EMAIL` promotes an already-registered account to the `Admin` role at API startup. Sign in again after promotion because its previous JWT is invalidated.
- `LONG_TERM_MEMORY_ENABLED` defaults to `true`; turning it off skips extraction and retrieval without deleting stored facts.
- User-level Gemini/OpenAI/Anthropic key fields exist in `/settings` (the latter two are stored but currently unused by any active service — kept for possible future use, not cleaned up because `AUDIT-LEGACY.md` didn't call for it).

## Local Development

### Backend Only

```powershell
cd VoiceAssistant.API
dotnet restore
dotnet build
dotnet run
```

The API listens according to `launchSettings.json` or `ASPNETCORE_URLS`.

### Mobile Client

See `docs/react-native/BUILD-WSL.md` (Windows) or `docs/react-native/BUILD-MACOS.md` (macOS) for building and installing on a physical device. Cloud EAS builds and store publication are not set up yet — local builds only.

### Admin Client

```powershell
cd admin
npm ci
npm run dev
```

Open `http://127.0.0.1:4173/admin/`. Vite proxies `/api` to `http://localhost:5000` by default; override it with `VITE_DEV_API_TARGET`.

### Full Docker Stack (backend + admin + db + tts)

```powershell
copy .env.example .env
# edit .env
docker compose up --build
```

Default nginx mapping:

```text
http://127.0.0.1:4001
```

Health check:

```powershell
curl http://127.0.0.1:4001/api/health
```

Admin panel:

```text
http://127.0.0.1:4001/admin/
```

## Database

The app uses EF Core migrations in `VoiceAssistant.API/Migrations`. The database image is built from `postgres-vector/Dockerfile`: pgvector 0.8.2 is compiled in a separate stage and copied into the same Alpine PostgreSQL runtime/UID used by the existing production volume.

Startup currently runs:

```csharp
db.Database.Migrate();
```

Convenient during development; `Program.cs` applies migrations unconditionally on every container start, including in production (skipped only under the integration test project's `"Testing"` environment — see Startup above).

**Deploying to production**: use `scripts/deploy.sh` rather than pulling/rebuilding by hand — it takes a `pg_dump` backup first (aborting before touching anything if the backup looks wrong), then pulls and rebuilds `api` plus `admin`, restarts the gateway, and smoke-tests both `GET /api/health/ready` and `/admin/` before reporting success (PROJECT-AUDIT-2026-07-10 OPS-01). Backups land in `backups/pre-deploy-<timestamp>.sql` on the VPS.

Useful commands:

```powershell
dotnet ef migrations add MigrationName --project VoiceAssistant.API
dotnet ef database update --project VoiceAssistant.API
```

## Paused Features

- **Speaker identification** (`SpeakerIdService`, `SpeakerRegistryService`, `SpeakerProfile` entity, the `speaker-id` SpeechBrain microservice): built, then paused for two independent reasons -- real short phone-mic clips scored too close to the noise-floor similarity seen between different speakers to trust yet, and `SpeakerProfile`/`SpeakerPendingStore` have no per-user/household isolation (PROJECT-AUDIT-2026-07-10 SEC-02) -- turning this on today would mix voices/names across unrelated accounts. The call site is real code, gated by `Features:SpeakerIdentificationEnabled` (default `false`). The `speaker-id` compose service is defined but opt-in (`profiles: ["speaker-id"]`, OPS-02) -- run `docker compose --profile speaker-id up` to stand it up for testing. Do not flip the feature flag on before adding tenant isolation.

## Recent Product Direction

- Voice-first, turn-taking-aware companion for older users (not a lesson/tutor flow).
- Self-hosted Piper TTS with sentence-level streaming, later demoted to a server-side fallback once mobile's on-device `expo-speech` TTS shipped (no network hop, more natural voice on most devices).
- Legacy Polish-tutor code (vocabulary, onboarding, level tests, nightly analysis) removed — see `docs/AUDIT-LEGACY.md`.
- `/api/v1/` versioning, added ahead of the React Native mobile client.
- Legacy public browser PWA removed; mobile (`mobile/`) remains the product client and `admin/` is a separate protected operations UI (PROJECT-AUDIT-2026-07-10 ARCH-01).

## Pre-commit Checklist

```powershell
dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj
dotnet test VoiceAssistant.API.Tests/VoiceAssistant.API.Tests.csproj
dotnet test VoiceAssistant.API.IntegrationTests/VoiceAssistant.API.IntegrationTests.csproj
git status --short
```

For admin changes:

```powershell
cd admin
npm ci
npm run typecheck
npm run build
npm audit --audit-level=high
```

For mobile changes:

```powershell
cd mobile
npx tsc --noEmit
```

Anything that depends on real microphone/speaker/device behavior (VAD sensitivity, barge-in, background audio) can't be verified this way — build an APK (`docs/react-native/BUILD-WSL.md` / `BUILD-MACOS.md`) and test on a physical device.
