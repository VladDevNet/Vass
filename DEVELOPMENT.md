# Polish Tutor App — Development Guide

## Технологічний стек

### Backend: .NET 10 (LTS, підтримка до Nov 2028)
| Компонент | Пакет | Версія |
|-----------|-------|--------|
| Runtime | .NET 10 | 10.0.3 (Feb 2026) |
| Web API | ASP.NET Core 10 | 10.0.3 |
| Auth | Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.3 |
| ORM | Microsoft.EntityFrameworkCore | 10.0.x |
| PostgreSQL | Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.0 |
| Claude API | Anthropic (official C# SDK) | 12.4.0 (beta) |
| MCP | ModelContextProtocol (official C# SDK, by Microsoft) | preview |

### Frontend: Vanilla JS (без фреймворків)
| Компонент | Технологія | Нотатки |
|-----------|-----------|---------|
| UI | HTML5 + CSS3 + vanilla JS | Мінімум залежностей |
| Speech-to-Text | Web Speech API (браузер) | Безкоштовно, Chrome/Edge |
| Text-to-Speech | SpeechSynthesis API (браузер) | Польські голоси доступні |
| Streaming | EventSource (SSE) | Нативний API |
| Альтернатива STT (v2) | Whisper API або Voxtral | Краща якість для PL |

### Infrastructure
| Компонент | Технологія | Версія |
|-----------|-----------|--------|
| Container | Docker | latest |
| Orchestration | docker-compose | v2 |
| Reverse proxy | nginx | alpine |
| Database | PostgreSQL | 17 |

---

## Архітектура

```
┌─────────────────────────────────────────────────────┐
│                    nginx (443/80)                     │
│          static files + reverse proxy                │
├──────────────┬──────────────────────────────────────┤
│   Frontend   │         /api/* → Backend              │
│  (HTML/JS)   │         /mcp/* → MCP Server           │
└──────────────┴──────────────────────────────────────┘
                          │
              ┌───────────▼───────────┐
              │   .NET 10 Web API     │
              │                       │
              │  ├─ Auth (Identity+JWT)│
              │  ├─ Chat (SSE stream) │
              │  ├─ Tests             │
              │  ├─ Lessons CRUD      │
              │  ├─ Progress tracking │
              │  └─ MCP Server        │
              └───────┬───────┬───────┘
                      │       │
              ┌───────▼┐  ┌──▼──────────┐
              │ PgSQL  │  │ Claude API  │
              │  :5432 │  │ (Anthropic) │
              └────────┘  └─────────────┘
```

---

## Структура проекту

```
app/
├── PolishTutor.Api/
│   ├── Controllers/
│   │   ├── AuthController.cs         # POST /api/auth/register, /login, /me
│   │   ├── ChatController.cs         # POST /api/chat/send (SSE), GET /sessions
│   │   ├── TestController.cs         # GET /api/test/start, POST /submit
│   │   ├── LessonController.cs       # CRUD /api/lessons
│   │   └── ProgressController.cs     # GET /api/progress
│   ├── Services/
│   │   ├── AnthropicService.cs       # Claude API wrapper (streaming)
│   │   ├── TutorService.cs           # System prompts, context assembly
│   │   ├── TestService.cs            # Level assessment logic
│   │   ├── ProgressService.cs        # Error tracking, stats
│   │   └── PlanService.cs            # Adaptive learning plans
│   ├── Mcp/
│   │   ├── McpServerSetup.cs         # MCP registration in DI
│   │   └── Tools/
│   │       ├── UserTools.cs          # get_users, get_user_progress
│   │       ├── LessonTools.cs        # create/update_lesson, create_exercise
│   │       ├── PlanTools.cs          # set_user_plan, get_analytics
│   │       └── ChatTools.cs          # get_chat_sessions, get_user_errors
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   ├── Entities/
│   │   │   ├── User.cs
│   │   │   ├── ChatSession.cs
│   │   │   ├── Message.cs
│   │   │   ├── Lesson.cs
│   │   │   ├── Exercise.cs
│   │   │   ├── TestResult.cs
│   │   │   └── LearningPlan.cs
│   │   └── Migrations/
│   ├── Prompts/
│   │   ├── tutor-system.txt          # головний system prompt тьютора
│   │   ├── test-grading.txt          # prompt для оцінювання тестів
│   │   └── error-analysis.txt        # prompt для аналізу помилок
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── PolishTutor.Api.csproj
│   └── Dockerfile
├── frontend/
│   ├── index.html                    # Landing + auth forms
│   ├── app.html                      # Main SPA shell
│   ├── css/
│   │   └── styles.css
│   └── js/
│       ├── auth.js                   # Login/register, JWT storage
│       ├── chat.js                   # Chat UI + SSE streaming
│       ├── voice.js                  # STT/TTS integration
│       ├── test.js                   # Level test UI
│       ├── progress.js               # Dashboard
│       └── api.js                    # HTTP client wrapper
├── docker-compose.yml
├── nginx.conf
├── .env.example
└── DEVELOPMENT.md                    # ← цей файл
```

---

## API Endpoints

### Auth
| Method | Path | Опис |
|--------|------|------|
| POST | `/api/auth/register` | Реєстрація (email, password, nativeLang) |
| POST | `/api/auth/login` | Логін → JWT token |
| GET | `/api/auth/me` | Поточний юзер + рівень |

### Chat
| Method | Path | Опис |
|--------|------|------|
| POST | `/api/chat/send` | Надіслати повідомлення → SSE stream відповіді |
| GET | `/api/chat/sessions` | Список сесій користувача |
| POST | `/api/chat/sessions` | Нова сесія (mode: dialog/lesson/situation) |
| GET | `/api/chat/sessions/{id}` | Історія повідомлень сесії |

### Tests
| Method | Path | Опис |
|--------|------|------|
| GET | `/api/test/start?type=level` | Почати тест рівня |
| POST | `/api/test/submit` | Відправити відповіді → результат + рівень |
| GET | `/api/test/history` | Історія тестів |

### Lessons & Progress
| Method | Path | Опис |
|--------|------|------|
| GET | `/api/lessons` | Список уроків (фільтр по рівню) |
| GET | `/api/lessons/{id}` | Деталі уроку |
| GET | `/api/progress` | Прогрес + рекомендації |
| GET | `/api/progress/errors` | Типові помилки користувача |

---

## MCP Tools (для Claude Code)

```csharp
[McpServerTool("get_users")]
// Повертає список юзерів з рівнями та останньою активністю

[McpServerTool("get_user_progress")]
// userId → детальний прогрес: пройдені уроки, помилки, рівень

[McpServerTool("get_user_errors")]
// userId → часті помилки з діалогів (граматика, лексика)

[McpServerTool("create_lesson")]
// title, level, content(md), exercises → створює урок в БД

[McpServerTool("update_lesson")]
// lessonId, content → оновлює контент

[McpServerTool("create_exercise")]
// lessonId, type(fill_gap/translate/choice), data(json) → вправа

[McpServerTool("create_test")]
// level, questions(json) → тест для рівня

[McpServerTool("set_user_plan")]
// userId, plan(json) → оновити навчальний план

[McpServerTool("get_chat_sessions")]
// userId, limit → останні діалоги з повідомленнями

[McpServerTool("get_analytics")]
// загальна статистика: активні юзери, середній рівень, прогрес
```

---

## Голосове спілкування

### Speech-to-Text (STT)
```
Браузер (мікрофон) → Web Speech API → текст → POST /api/chat/send
```
- `SpeechRecognition` API з `lang: 'pl-PL'`
- Підтримка: Chrome, Edge (найкраща якість)
- Fallback: ручне введення тексту

### Text-to-Speech (TTS)
```
SSE відповідь → текст → SpeechSynthesis API → звук
```
- `speechSynthesis.speak()` з голосом `pl-PL`
- Автоматичне програвання відповідей тьютора

### Апгрейд (v2)
- STT: OpenAI Whisper API або Voxtral (краща якість для польської)
- TTS: OpenAI TTS або ElevenLabs (природніший голос)

---

## Docker

### docker-compose.yml
```yaml
services:
  api:
    build: ./PolishTutor.Api
    environment:
      - ConnectionStrings__Default=Host=db;Database=polishtutor;Username=app;Password=${DB_PASSWORD}
      - Anthropic__ApiKey=${ANTHROPIC_API_KEY}
    depends_on:
      - db

  db:
    image: postgres:17-alpine
    volumes:
      - pgdata:/var/lib/postgresql/data
    environment:
      - POSTGRES_DB=polishtutor
      - POSTGRES_USER=app
      - POSTGRES_PASSWORD=${DB_PASSWORD}

  nginx:
    image: nginx:alpine
    ports:
      - "80:80"
    volumes:
      - ./frontend:/usr/share/nginx/html
      - ./nginx.conf:/etc/nginx/conf.d/default.conf
    depends_on:
      - api

volumes:
  pgdata:
```

### .env.example
```
ANTHROPIC_API_KEY=sk-ant-...
DB_PASSWORD=your_secure_password
JWT_SECRET=your_jwt_secret
```

---

## Фази розробки

### Фаза 1: Скелет ✦ ПОТОЧНА
- [ ] `dotnet new webapi` з .NET 10
- [ ] NuGet пакети (Identity, EF Core, Npgsql, Anthropic)
- [ ] Entities + DbContext + міграції
- [ ] Auth: register/login/JWT
- [ ] Chat: відправка → Claude API → SSE streaming
- [ ] Фронтенд: login + chat UI
- [ ] Docker compose + nginx

### Фаза 2: Голос + тести
- [ ] Web Speech API інтеграція (STT/TTS)
- [ ] Тест рівня (A1-B2)
- [ ] Визначення рівня → адаптація system prompt

### Фаза 3: Контент + адаптивність
- [ ] Lessons CRUD
- [ ] Завантаження існуючих md-матеріалів
- [ ] Error tracking з діалогів
- [ ] Адаптивний план

### Фаза 4: MCP
- [ ] MCP Server (ModelContextProtocol SDK)
- [ ] Tools для керування контентом
- [ ] Підключення до Claude Code

---

## Команди розробки

```bash
# Створення проекту
dotnet new webapi -n PolishTutor.Api --framework net10.0

# Додати пакети
dotnet add package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Anthropic
dotnet add package ModelContextProtocol --prerelease

# Міграції
dotnet ef migrations add InitialCreate
dotnet ef database update

# Запуск
dotnet run

# Docker
docker-compose up --build
```
