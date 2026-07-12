# Vass - Development Guide

Vass is a personal voice assistant web app. The assistant persona is Olga: a warm Russian-speaking voice companion for short, natural conversations, everyday help, and hands-free voice interaction. Primary audience is older users — the design favors patience and simplicity over speed or feature density.

The repository was originally copied from a Polish tutor prototype. Legacy tutor/vocabulary/onboarding code has been removed (see `docs/AUDIT-LEGACY.md` for the full audit and what was deleted vs. deliberately kept). A React Native mobile app is planned; see `docs/react-native/`.

## Current Product

Core user flow (YOLO hands-free mode):

1. User logs in with email/password; browser opens the chat screen and auto-enters YOLO mode.
2. Continuous VAD (voice activity detection) listens on the microphone.
3. Frontend uploads the recorded `webm` clip to the API once the user is judged done speaking.
4. API converts `webm` to `wav` with `ffmpeg` and sends it to Gemini for transcription.
5. Gemini (3.5 Flash, with Google Search grounding) streams Olga's response over SSE.
6. The response is spoken via self-hosted Piper TTS, streamed as raw PCM and played sentence-by-sentence as it arrives — no waiting for the full reply.

Real turn-taking (the main thing that sets this apart from a fixed silence-timeout voice bot): instead of cutting the user off after a fixed short pause, the client tracks silence against a soft re-check interval and a hard 7-second ceiling. During a pause it asks a small, fast Gemini call ("is this utterance actually complete, or is the person still thinking?") — `POST /api/v1/chat/check-utterance`. If the model says "not complete" and the user is currently silent, a short human backchannel phrase plays ("да", "слушаю", "так", "хорошо", "понятно") to signal Olga is still listening, without ending the turn. See `frontend/js/yolo.js` for the state machine and `AudioAnalysisService.CheckUtteranceCompletionAsync` for the check itself.

Secondary features:

- Text chat with SSE streaming (`frontend/js/chat.js`), independent of YOLO mode.
- Camera/image OCR through Gemini.
- Per-user API keys (Gemini/OpenAI/Anthropic key fields exist in settings for future use, but only Gemini is actually consumed today) and a custom system prompt, which the assistant can update itself when a user asks it to remember a behavioral preference ("говори медленнее").
- PWA shell with service worker for offline app-shell caching.

## Technology Stack

### Backend

| Area | Technology |
| --- | --- |
| Runtime | .NET 10 |
| Web API | ASP.NET Core |
| Auth | ASP.NET Core Identity + JWT (issuer/audience `Vass`) |
| ORM | Entity Framework Core |
| Database | PostgreSQL 17 |
| LLM chat | Gemini API (`gemini-3.5-flash`, Google Search grounding) |
| Audio transcription/completeness check | Gemini API (`gemini-2.5-flash`) |
| Neural TTS | Self-hosted Piper (`ru_RU-irina-medium`), raw PCM streamed |
| Audio conversion | ffmpeg |

### Frontend

| Area | Technology |
| --- | --- |
| UI | HTML, CSS, vanilla JavaScript (no bundler, no framework) |
| App shell | Static files served by nginx |
| Streaming | Fetch + readable stream parsing of SSE-style events |
| Audio recording | MediaRecorder + Web Audio API `AnalyserNode` for VAD |
| TTS playback | Manual raw-PCM `AudioBuffer` scheduling (bypasses `decodeAudioData`, which needs a complete file) for gapless streamed playback |
| PWA | manifest + service worker |

### Infrastructure

| Service | Purpose |
| --- | --- |
| `api` | ASP.NET Core backend on port `5000`, has a real Docker healthcheck against `/api/health` |
| `db` | PostgreSQL 17 |
| `tts` | Self-hosted Piper TTS (Flask wrapper around the Piper CLI) |
| `nginx` | Static frontend + `/api/*` reverse proxy (prefix-match, not path rewrite — `/api/v1/...` passes through unchanged) |
| `audio` volume | Persists uploaded user audio |

`speaker-id` (SpeechBrain-based voice identification) is in `docker-compose.yml` but opt-in only (`profiles: ["speaker-id"]` -- `docker compose up` alone never starts it, and `api` does not `depends_on` it); the standalone Silero TTS quality comparison that used to live in `silero-tts/` was removed entirely, having lost its comparison against Piper (PROJECT-AUDIT-2026-07-10 OPS-02). See below.

## Repository Layout

```text
.
├── VoiceAssistant.API/
│   ├── Controllers/
│   │   ├── AuthController.cs        # api/v1/auth — register, login, current user
│   │   ├── ChatController.cs        # api/v1/chat — sessions, SSE chat, audio, TTS, OCR
│   │   └── SettingsController.cs    # api/v1/settings — per-user profile, API keys, custom prompt
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Entities/                # ChatSession, Message, User, UserSettings, SpeakerProfile
│   ├── Migrations/
│   ├── Prompts/
│   │   └── companion-system.txt     # Olga's system prompt
│   ├── Services/
│   │   ├── GeminiService.cs         # LLM streaming + Google Search grounding
│   │   ├── PiperTtsService.cs       # self-hosted TTS, buffered + streaming
│   │   ├── CompanionPromptService.cs
│   │   ├── AudioAnalysisService.cs  # transcription + utterance-completeness check
│   │   ├── SpeakerIdService.cs      # paused feature — see "Paused Features" below
│   │   ├── SpeakerPendingStore.cs
│   │   └── SpeakerRegistryService.cs
│   ├── Dockerfile
│   └── VoiceAssistant.API.csproj
├── piper-tts/                       # Flask wrapper around the Piper TTS CLI
├── speaker-id/                      # SpeechBrain ECAPA-TDNN voice-ID microservice (paused feature, opt-in compose profile)
├── frontend/
│   ├── app.html                     # main chat/YOLO screen
│   ├── index.html                   # login/register
│   ├── settings.html
│   ├── sw.js
│   ├── audio/fillers/               # pre-synthesized greeting + backchannel WAV clips
│   ├── css/styles.css
│   └── js/
│       ├── api.js                   # authenticated requests, SSE parser, baseUrl /api/v1
│       ├── auth.js
│       ├── chat.js                  # text chat view, session list, OCR attach
│       ├── voice.js                 # raw-PCM TTS playback, TTS queue
│       ├── yolo.js                  # hands-free mode: VAD, turn-taking, interruption
│       └── settings.js
├── docker-compose.yml
├── nginx.conf
├── .env.example
├── docs/
│   ├── AUDIT-LEGACY.md              # legacy-cleanup audit (mostly executed)
│   └── react-native/                # mobile app plan, backlog, WSL build guide
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
- Controller routing and `/api/health`.
- EF Core migrations on startup (`db.Database.Migrate()`).

### Active Controllers

| Controller | Base route | Purpose |
| --- | --- | --- |
| `AuthController` | `/api/v1/auth` | Register, login, current user |
| `ChatController` | `/api/v1/chat` | Sessions, SSE chat, audio upload/playback, TTS, utterance-completeness check, OCR |
| `SettingsController` | `/api/v1/settings` | Per-user profile, API keys, custom system prompt |

## Main API Surface

### Auth

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/v1/auth/register` | Creates user and returns JWT |
| `POST` | `/api/v1/auth/login` | Returns JWT |
| `GET` | `/api/v1/auth/me` | Current user data |

### Chat And Voice

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/chat/sessions` | Returns the single current dialog session |
| `POST` | `/api/v1/chat/sessions` | Creates or returns the existing dialog session |
| `GET` | `/api/v1/chat/sessions/{id}` | Loads messages |
| `PATCH` | `/api/v1/chat/sessions/{id}` | Renames a session |
| `DELETE` | `/api/v1/chat/sessions/{id}` | Deletes session and linked audio files |
| `POST` | `/api/v1/chat/send` | Streams assistant response as SSE-style `data:` events |
| `POST` | `/api/v1/chat/upload-audio` | Uploads `audio/*` up to 5 MB |
| `GET` | `/api/v1/chat/audio/{fileName}` | Returns user-owned uploaded audio |
| `POST` | `/api/v1/chat/tts` | Generates a full WAV clip via Piper |
| `POST` | `/api/v1/chat/tts_stream` | Streams raw PCM as it's synthesized |
| `POST` | `/api/v1/chat/check-utterance` | Transcribes a snapshot and judges complete vs. still-thinking (turn-taking) |
| `POST` | `/api/v1/chat/ocr-image` | Extracts text from image with Gemini |

### Settings

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/v1/settings` | Loads masked API keys and preferences |
| `PUT` | `/api/v1/settings` | Saves profile, keys, and custom prompt |
| `GET` | `/api/v1/settings/default-prompt` | Returns Olga's current system prompt |

### Health

Two separate endpoints (PROJECT-AUDIT-2026-07-10 REL-03) — note that a bare `/health` (no `/api` prefix) is **not** reachable through the public nginx port; it falls through to the SPA catch-all and silently returns `index.html`.

- `GET /api/health` — liveness. Always returns a plain `"healthy"` body if the process is up; no dependency checks. This is what the Docker healthcheck and `docker-compose.yml`'s `depends_on: condition: service_healthy` gates point at — deliberately cheap so a slow/degraded dependency never makes Docker restart an otherwise-healthy container.
- `GET /api/health/ready` — readiness. Checks real DB connectivity, that the audio volume is actually writable, and Gemini configuration presence (never a paid API call) — any of those failing returns HTTP 503 with `"status":"not_ready"`. TTS is checked too but reported as its own `checks.tts` field (`"ok"`/`"degraded"`) without affecting the overall status. This is what **external** uptime monitoring should point at instead of `/api/health`, since liveness alone can't tell you the user-facing chat flow actually works.

## Streaming Events

`POST /api/v1/chat/send` writes SSE-style lines. Happy path (PROJECT-AUDIT-2026-07-10 REL-02 -- note `stats` and `[DONE]` were reordered from an earlier version of this doc: the assistant reply is saved and `stats` is sent BEFORE `[DONE]`, since some clients stop reading the instant they see it):

```text
data: {"transcription":"..."}
data: {"preamble":"..."}
data: {"text":"..."}
data: {"stats":{"convertMs":0,"transcribeMs":0,"speakerIdMs":0,"llmFirstTokenMs":0,"llmTotalMs":0,"translationMs":0}}
data: [DONE]
```

On an AI/provider infrastructure failure (missing key, non-2xx from Gemini, connection error -- PROJECT-AUDIT-2026-07-10 REL-04), the stream instead ends with a single error event and no `[DONE]`:

```text
data: {"error":"...","retryable":true}
```

`preamble` is an optional short "hold on, let me think/search" phrase, spoken if a fast side-call decides the real response will take a while (search grounding, complex reasoning). The frontend parses these events in `frontend/js/api.js`.

## Audio Pipeline

YOLO hands-free flow:

```text
Browser microphone (continuous VAD)
  -> silence-duration tracking against a soft re-check interval and a 7s hard ceiling
  -> periodic POST /api/v1/chat/check-utterance snapshot (non-destructive) while still listening
  -> not complete + currently silent -> play a backchannel filler, keep listening
  -> complete, or hard ceiling reached -> finalize
  -> POST /api/v1/chat/upload-audio, then POST /api/v1/chat/send with audioFileName
  -> ffmpeg webm -> wav -> Gemini transcription -> Gemini streaming answer
  -> Piper TTS streamed as raw PCM, played sentence-by-sentence
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
```

Key usage:

- `GEMINI_API_KEY` is required for chat, audio transcription, utterance-completeness checks, and OCR, unless the user saves a personal Gemini key in settings.
- `JWT_SECRET`/`Jwt:Issuer`/`Jwt:Audience` (`Vass`) — changing the issuer/audience invalidates all previously-issued tokens.
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

### Full Docker Stack

```powershell
copy .env.example .env
# edit .env
docker compose up --build
```

Default nginx mapping:

```text
http://127.0.0.1:4001
```

Health check (real backend response, not the SPA fallback):

```powershell
curl http://127.0.0.1:4001/api/health
```

## Database

The app uses EF Core migrations in `VoiceAssistant.API/Migrations`.

Startup currently runs:

```csharp
db.Database.Migrate();
```

Convenient during development; `Program.cs` applies migrations unconditionally on every container start, including in production.

**Deploying to production**: use `scripts/deploy.sh` rather than pulling/rebuilding by hand — it takes a `pg_dump` backup first (aborting before touching anything if the backup looks wrong), then pulls/rebuilds/restarts, then polls `GET /api/health/ready` as a post-deploy smoke test before reporting success (PROJECT-AUDIT-2026-07-10 OPS-01). Backups land in `backups/pre-deploy-<timestamp>.sql` on the VPS.

Useful commands:

```powershell
dotnet ef migrations add MigrationName --project VoiceAssistant.API
dotnet ef database update --project VoiceAssistant.API
```

## Frontend Runtime Notes

The frontend is plain static HTML/JS. There is no bundler.

Important scripts:

- `api.js`: authenticated requests (`baseUrl: '/api/v1'`) and streaming chat parser.
- `chat.js`: chat view, session loading, message sending, OCR attachment flow.
- `voice.js`: raw-PCM TTS playback (`playPcmStream`), sequential TTS queue (`createTtsQueue`), static clip playback for greeting/backchannel WAVs.
- `yolo.js`: hands-free mode — VAD state machine, real turn-taking (completeness checks, backchannel fillers), shadow-capture (validates a real interruption before disturbing an in-flight response), wake lock.
- `settings.js`: user settings and API keys.

YOLO mode depends on:

- microphone permission;
- browser MediaRecorder support;
- Web Audio API;
- server-side `ffmpeg`;
- a Gemini API key for transcription, completeness checks, and chat;
- the `tts` (Piper) container for speech output.

## Paused Features

- **Speaker identification** (`SpeakerIdService`, `SpeakerRegistryService`, `SpeakerProfile` entity, the `speaker-id` SpeechBrain microservice): built, then paused for two independent reasons -- real short phone-mic clips scored too close to the noise-floor similarity seen between different speakers to trust yet, and `SpeakerProfile`/`SpeakerPendingStore` have no per-user/household isolation (PROJECT-AUDIT-2026-07-10 SEC-02) -- turning this on today would mix voices/names across unrelated accounts. The call site is real code, gated by `Features:SpeakerIdentificationEnabled` (default `false`). The `speaker-id` compose service is defined but opt-in (`profiles: ["speaker-id"]`, OPS-02) -- run `docker compose --profile speaker-id up` to stand it up for testing. Do not flip the feature flag on before adding tenant isolation.

## Recent Product Direction

- Voice-first, turn-taking-aware companion for older users (not a lesson/tutor flow).
- Self-hosted Piper TTS with sentence-level streaming, replacing the earlier OpenAI TTS integration.
- Legacy Polish-tutor code (vocabulary, onboarding, level tests, nightly analysis) removed — see `docs/AUDIT-LEGACY.md`.
- `/api/v1/` versioning ahead of a planned React Native mobile client — see `docs/react-native/`.

## Pre-commit Checklist

```powershell
dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj
git status --short
```

For browser-facing changes, also test:

- login/register;
- opening `app.html`;
- text message streaming;
- audio recording and YOLO mode start/stop, including a pause long enough to trigger a completeness check;
- TTS playback;
- settings save with empty and populated API keys.
