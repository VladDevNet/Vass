# Vass - Development Guide

Vass is a personal voice assistant web app. The assistant persona is Olga: a warm Russian-speaking voice companion for short, natural conversations, everyday help, and hands-free voice interaction.

The repository was originally copied from a Polish tutor prototype, so some domain names still say `Tutor`, `Vocabulary`, `Level`, or `Onboarding`. The current product direction is the Vass/Olga voice companion.

## Current Product

Core user flow:

1. User logs in with email/password.
2. Browser opens the chat screen and can auto-enter YOLO hands-free mode.
3. User speaks into the microphone.
4. Frontend uploads a `webm` audio clip to the API.
5. API converts `webm` to `wav` with `ffmpeg`.
6. Gemini transcribes the audio and generates Olga's response through SSE streaming.
7. Frontend speaks the response using OpenAI neural TTS when configured, otherwise Web Speech fallback.

Secondary features:

- Text chat with SSE streaming.
- Camera/image OCR through Gemini.
- Per-user API keys and custom system prompt.
- PWA shell with service worker.
- Legacy vocabulary panel for Polish learning experiments.

## Technology Stack

### Backend

| Area | Technology |
| --- | --- |
| Runtime | .NET 10 |
| Web API | ASP.NET Core |
| Auth | ASP.NET Core Identity + JWT |
| ORM | Entity Framework Core |
| Database | PostgreSQL |
| LLM chat | Gemini API |
| Audio transcription/analysis | Gemini API |
| Neural TTS | OpenAI Audio Speech API |
| Legacy vocabulary analysis | Anthropic SDK |
| Audio conversion | ffmpeg |

### Frontend

| Area | Technology |
| --- | --- |
| UI | HTML, CSS, vanilla JavaScript |
| App shell | Static files served by nginx |
| Streaming | Fetch + readable stream parsing of SSE-style events |
| Audio recording | MediaRecorder |
| Voice activity detection | Web Audio API analyser |
| TTS fallback | Web Speech API |
| PWA | manifest + service worker |

### Infrastructure

| Service | Purpose |
| --- | --- |
| `api` | ASP.NET Core backend on port `5000` |
| `db` | PostgreSQL 17 |
| `nginx` | Static frontend + `/api/*` reverse proxy |
| `audio` volume | Persist uploaded user audio in Docker |

## Repository Layout

```text
.
├── VoiceAssistant.API/
│   ├── Controllers/
│   │   ├── AuthController.cs
│   │   ├── ChatController.cs
│   │   ├── OnboardingController.cs      # legacy Polish-level flow
│   │   ├── SettingsController.cs
│   │   └── VocabularyController.cs      # legacy vocabulary feature
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── Entities/
│   ├── Migrations/
│   ├── Prompts/
│   │   ├── tutor-system.txt             # current Olga system prompt
│   │   ├── conductor-analysis.txt       # legacy nightly learner analysis
│   │   └── level-test.txt               # legacy Polish tutor prompt
│   ├── Services/
│   │   ├── GeminiService.cs
│   │   ├── AudioAnalysisService.cs
│   │   ├── OpenAiTtsService.cs
│   │   ├── AnthropicService.cs
│   │   └── NightlyAnalysisJob.cs
│   ├── Dockerfile
│   └── VoiceAssistant.API.csproj
├── frontend/
│   ├── app.html
│   ├── index.html
│   ├── settings.html
│   ├── sw.js
│   ├── css/styles.css
│   └── js/
│       ├── api.js
│       ├── auth.js
│       ├── chat.js
│       ├── voice.js
│       ├── yolo.js
│       ├── settings.js
│       └── vocabulary.js
├── docker-compose.yml
├── nginx.conf
├── .env.example
└── DEVELOPMENT.md
```

## Backend Architecture

### Startup

`VoiceAssistant.API/Program.cs` configures:

- PostgreSQL `AppDbContext`.
- ASP.NET Identity with relaxed password rules for early development.
- JWT bearer authentication.
- Shared `IHttpClientFactory`.
- Application services: Gemini, OpenAI TTS, Anthropic, tutor prompt helper, audio analysis.
- Hosted nightly analysis job.
- Controller routing and `/health`.
- EF Core migrations on startup.

### Active Controllers

| Controller | Base route | Purpose |
| --- | --- | --- |
| `AuthController` | `/api/auth` | Register, login, current user |
| `ChatController` | `/api/chat` | Sessions, SSE chat, audio upload/playback, TTS, OCR |
| `SettingsController` | `/api/settings` | Per-user profile, API keys, custom system prompt |
| `VocabularyController` | `/api/vocabulary` | Legacy vocabulary CRUD and analysis |
| `OnboardingController` | `/api/onboarding` | Legacy Polish level selection |

## Main API Surface

### Auth

| Method | Path | Notes |
| --- | --- | --- |
| `POST` | `/api/auth/register` | Creates user and returns JWT |
| `POST` | `/api/auth/login` | Returns JWT |
| `GET` | `/api/auth/me` | Current user data |

### Chat And Voice

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/chat/sessions` | Returns the single current dialog session |
| `POST` | `/api/chat/sessions` | Creates or returns the existing dialog session |
| `GET` | `/api/chat/sessions/{id}` | Loads messages |
| `PATCH` | `/api/chat/sessions/{id}` | Renames a session |
| `DELETE` | `/api/chat/sessions/{id}` | Deletes session and linked audio files |
| `POST` | `/api/chat/send` | Streams assistant response as SSE-style `data:` events |
| `POST` | `/api/chat/upload-audio` | Uploads `audio/*` up to 5 MB |
| `GET` | `/api/chat/audio/{fileName}` | Returns user-owned uploaded audio |
| `POST` | `/api/chat/tts` | Generates MP3 speech with OpenAI |
| `POST` | `/api/chat/ocr-image` | Extracts text from image with Gemini |

### Settings

| Method | Path | Notes |
| --- | --- | --- |
| `GET` | `/api/settings` | Loads masked API keys and preferences |
| `PUT` | `/api/settings` | Saves profile, keys, and custom prompt |
| `GET` | `/api/settings/default-prompt` | Returns Olga's default system prompt |

## Streaming Events

`POST /api/chat/send` writes SSE-style lines:

```text
data: {"transcription":"..."}
data: {"text":"..."}
data: [DONE]
data: {"stats":{"convertMs":0,"transcribeMs":0,"llmFirstTokenMs":0,"llmTotalMs":0,"translationMs":0}}
```

The frontend parses these events in `frontend/js/api.js`.

## Audio Pipeline

Normal YOLO flow:

```text
Browser microphone
  -> MediaRecorder webm chunks
  -> POST /api/chat/upload-audio
  -> save webm under configured audio directory
  -> POST /api/chat/send with audioFileName
  -> ffmpeg webm -> wav
  -> Gemini audio transcription
  -> Gemini streaming answer
  -> OpenAI TTS or Web Speech fallback
```

Audio storage:

- Docker default: `/app/audio`, mounted as the `audio` volume.
- Local default: `VoiceAssistant.API/audio`.
- Override with `Audio:Path` or `Audio__Path`.

## Configuration

Environment variables used by Docker:

```env
DB_PASSWORD=your_secure_password
JWT_SECRET=your_jwt_secret_min_32_chars_long_here
ANTHROPIC_API_KEY=sk-ant-...
OPENAI_API_KEY=sk-...
GEMINI_API_KEY=AIza...
```

Key usage:

- `GEMINI_API_KEY` is required for chat, audio transcription, and OCR unless the user saves a personal Gemini key in settings.
- `OPENAI_API_KEY` is required for neural TTS; the frontend falls back to browser Web Speech when unavailable.
- `ANTHROPIC_API_KEY` is currently used by legacy vocabulary analysis and nightly learner analysis.
- User-level keys in `/settings` override server keys for the corresponding services.

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

Health check:

```powershell
curl http://127.0.0.1:4001/api/health
```

## Database

The app uses EF Core migrations in `VoiceAssistant.API/Migrations`.

Startup currently runs:

```csharp
db.Database.Migrate();
```

That is convenient during development. Revisit this before production if startup migration control becomes important.

Useful commands:

```powershell
dotnet ef migrations add MigrationName --project VoiceAssistant.API
dotnet ef database update --project VoiceAssistant.API
```

## Frontend Runtime Notes

The frontend is plain static HTML/JS. There is no bundler.

Important scripts:

- `api.js`: authenticated requests and streaming chat parser.
- `chat.js`: chat view, session loading, message sending, OCR attachment flow.
- `voice.js`: manual recording and TTS playback.
- `yolo.js`: hands-free mode, VAD, interruption handling, wake lock.
- `settings.js`: user settings and API keys.
- `vocabulary.js`: legacy vocabulary panel.

YOLO mode depends on:

- microphone permission;
- browser MediaRecorder support;
- Web Audio API;
- server-side `ffmpeg`;
- Gemini API key for audio transcription and chat;
- OpenAI API key only for neural TTS.

## Known Legacy Areas

These are intentionally left visible because they still exist in code:

- `OnboardingController` and `onboarding.js` are from the old Polish level test flow.
- `VocabularyController`, `UserWord`, and vocabulary UI are from the tutor prototype.
- `Lesson`, `Exercise`, `LearningPlan`, `LearnerError`, and `TutorInstruction` are still in the data model.
- Some service names still contain `Tutor`.
- `NightlyAnalysisJob` still performs learner-style analysis using Anthropic.

When hardening the product, decide whether to remove these areas or reframe them as real Vass features.

## Recent Product Direction

Recent commits moved the project toward:

- Vass/Olga branding.
- Gemini as the low-latency chat brain.
- OpenAI neural TTS with browser fallback.
- YOLO hands-free conversation.
- iOS/iPad audio unlock and Web Audio playback fixes.
- A single default dialog session instead of a lesson/session workflow.

## Pre-commit Checklist

```powershell
dotnet build VoiceAssistant.API/VoiceAssistant.API.csproj
git status --short
```

For browser-facing changes, also test:

- login/register;
- opening `app.html`;
- text message streaming;
- audio recording;
- YOLO mode start/stop;
- TTS playback;
- settings save with empty and populated API keys.
