# Второй аватар (мужской) + разметка голосов по полу — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать пользователю переключаться между двумя аватарами (Ольга / мужской), с ручной разметкой голосов по полу и автопереключением на первый голос нужного пола при смене аватара.

**Architecture:** Один параметризованный React Native компонент `LayeredAvatar` вместо дублирования (переименование существующего `OlgaLayeredAvatar.tsx`). Выбор аватара — свойство аккаунта (бэкенд, `UserSettings.AvatarId`, как `AssistantName`), синхронизируется между устройствами. Теги пола голосов — локально на устройстве (`SecureStore`, как голосовое предпочтение), поскольку набор голосов зависит от конкретного телефона.

**Tech Stack:** React Native 0.86 / Expo SDK 57 / TypeScript (мобильный клиент), ASP.NET Core 10 / EF Core / PostgreSQL 17 (бэкенд).

## Global Constraints

- Проверить https://docs.expo.dev/versions/v57.0.0/ перед правкой кода, если меняется что-то за пределами уже проверенных в этом плане API (`mobile/AGENTS.md`).
- `npx tsc --noEmit` чистый после каждого мобильного таска; `dotnet build` чистый после бэкенд-таска.
- `expo-speech`'s `Voice` тип не содержит поля пола (проверено: `node_modules/expo-speech/build/Speech.types.d.ts` — `{identifier, name, quality, language}`, ничего больше) — автоопределение пола голоса программно невозможно, только ручная разметка. Не пытаться угадывать пол по `identifier`.
- `PUT /settings` перезаписывает `displayName`/`assistantName`/`customSystemPrompt`/`avatarId` целиком телом запроса (кроме API-ключей, у которых отдельная "уже замаскировано" защита) — см. существующий комментарий над `Settings` в `client.ts`. Любой новый вызов, который обновляет `avatarId`, должен round-trip'ить весь объект настроек, а не только изменённое поле.
- Спек: [`docs/superpowers/specs/2026-07-10-male-avatar-and-voice-gender-design.md`](../specs/2026-07-10-male-avatar-and-voice-gender-design.md).

---

## Task 1: Бэкенд — поле `AvatarId` в `UserSettings`

**Files:**
- Modify: `VoiceAssistant.API/Data/Entities/UserSettings.cs`
- Modify: `VoiceAssistant.API/Data/AppDbContext.cs:42-51`
- Modify: `VoiceAssistant.API/Controllers/SettingsController.cs`
- Create: миграция через `dotnet ef migrations add` (см. Step 3)

**Interfaces:**
- Produces: `GET /api/v1/settings` и `PUT /api/v1/settings` теперь отдают/принимают `avatarId: string | null` (JSON, camelCase — ASP.NET Core сериализует `AvatarId` в `avatarId` автоматически, как уже происходит с `AssistantName`→`assistantName`).

- [ ] **Step 1: Добавить свойство в сущность**

Modify `VoiceAssistant.API/Data/Entities/UserSettings.cs` — после строки `public string? AssistantName { get; set; }`:

```csharp
    public string? DisplayName { get; set; }
    public string? AssistantName { get; set; }
    public string? AvatarId { get; set; }
    public string InterfaceLanguage { get; set; } = "uk";
```

- [ ] **Step 2: Fluent-конфигурация**

Modify `VoiceAssistant.API/Data/AppDbContext.cs` — в блоке `builder.Entity<UserSettings>(e => {...})` (сейчас строки 42-51), после строки `e.Property(s => s.AssistantName).HasMaxLength(100);`:

```csharp
        builder.Entity<UserSettings>(e =>
        {
            e.HasOne(s => s.User).WithOne()
                .HasForeignKey<UserSettings>(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId).IsUnique();
            e.Property(s => s.InterfaceLanguage).HasMaxLength(5).HasDefaultValue("uk");
            e.Property(s => s.DisplayName).HasMaxLength(100);
            e.Property(s => s.AssistantName).HasMaxLength(100);
            e.Property(s => s.AvatarId).HasMaxLength(20);
            e.Property(s => s.FullTranslation).HasDefaultValue(false);
        });
```

`HasMaxLength(20)`, не 100 — `AvatarId` это `"olga"`/`"male"`, фиксированный небольшой словарь значений (сопоставимо с `ClientLogEntries.Category`, тоже `varchar(20)`), не свободный пользовательский текст.

- [ ] **Step 3: Сгенерировать миграцию**

Run (из `VoiceAssistant.API/`):
```bash
cd VoiceAssistant.API
dotnet ef migrations add AddAvatarId
```

Expected: создаст `Migrations/<timestamp>_AddAvatarId.cs` и `.Designer.cs`, обновит `Migrations/AppDbContextModelSnapshot.cs`. Открыть сгенерированный `.cs` и свериться, что `Up()` содержит ровно один `migrationBuilder.AddColumn<string>(name: "AvatarId", table: "UserSettings", type: "character varying(20)", maxLength: 20, nullable: true);`, а `Down()` — соответствующий `DropColumn`. Если сгенерировалось что-то ещё (лишние изменения в других таблицах) — значит модель разошлась с последней применённой миграцией по каким-то другим причинам; в этой задаче трогать только `AvatarId` — остальное расследовать отдельно, не коммитить как часть этой миграции.

- [ ] **Step 4: Прокинуть в контроллер**

Modify `VoiceAssistant.API/Controllers/SettingsController.cs` — оба record'а и оба конструктора `SettingsResponse`:

```csharp
    public record SettingsResponse(
        string? DisplayName,
        string? AssistantName,
        string? AvatarId,
        string InterfaceLanguage,
        string? OpenAiApiKey,
        string? AnthropicApiKey,
        string? GeminiApiKey,
        string? CustomSystemPrompt,
        bool FullTranslation);

    public record SettingsUpdateRequest(
        string? DisplayName,
        string? AssistantName,
        string? AvatarId,
        string? InterfaceLanguage,
        string? OpenAiApiKey,
        string? AnthropicApiKey,
        string? GeminiApiKey,
        string? CustomSystemPrompt,
        bool? FullTranslation);
```

В `Get()` (сейчас строки 51-60) — добавить `settings?.AvatarId` вторым аргументом:

```csharp
        return Ok(new SettingsResponse(
            settings?.DisplayName,
            settings?.AssistantName,
            settings?.AvatarId,
            settings?.InterfaceLanguage ?? "uk",
            MaskKey(settings?.OpenAiApiKey),
            MaskKey(settings?.AnthropicApiKey),
            MaskKey(settings?.GeminiApiKey),
            settings?.CustomSystemPrompt,
            settings?.FullTranslation ?? false
        ));
```

В `Update()` (сейчас строки 85-86) — добавить присвоение после `settings.AssistantName = req.AssistantName;`:

```csharp
        settings.DisplayName = req.DisplayName;
        settings.AssistantName = req.AssistantName;
        settings.AvatarId = req.AvatarId;
        if (req.InterfaceLanguage is not null)
            settings.InterfaceLanguage = req.InterfaceLanguage;
```

И в финальном `return Ok(new SettingsResponse(...))` (сейчас строки 107-116) — добавить `settings.AvatarId`:

```csharp
        return Ok(new SettingsResponse(
            settings.DisplayName,
            settings.AssistantName,
            settings.AvatarId,
            settings.InterfaceLanguage,
            MaskKey(settings.OpenAiApiKey),
            MaskKey(settings.AnthropicApiKey),
            MaskKey(settings.GeminiApiKey),
            settings.CustomSystemPrompt,
            settings.FullTranslation
        ));
```

Валидация длины (как у `DisplayName`/`AssistantName`, строки 71-74) НЕ нужна — `AvatarId` не набирается пользователем текстом, приходит из фиксированного набора значений мобильного клиента (`"olga"`/`"male"`), 20 символов достаточно с большим запасом даже для будущих значений.

- [ ] **Step 5: Проверить сборку**

Run: `cd VoiceAssistant.API && dotnet build`
Expected: `Build succeeded`, без ошибок.

- [ ] **Step 6: Commit**

```bash
cd VoiceAssistant.API
git add Data/Entities/UserSettings.cs Data/AppDbContext.cs Controllers/SettingsController.cs Migrations/
git commit -m "Add AvatarId field to UserSettings"
```

---

## Task 2: Мобильный клиент — API + AuthContext

**Files:**
- Modify: `mobile/src/api/client.ts`
- Modify: `mobile/src/context/AuthContext.tsx`

**Interfaces:**
- Consumes: `GET/PUT /api/v1/settings`'s `avatarId` field (Task 1).
- Produces: `api.updateAvatarId(avatarId: string): Promise<void>`; `useAuth()` возвращает `avatarId: string | null` рядом с `assistantName`.

- [ ] **Step 1: Добавить поле в `Settings` и метод обновления**

Modify `mobile/src/api/client.ts` — интерфейс `Settings` (сейчас строки 190-199):

```ts
export interface Settings {
  displayName: string | null;
  assistantName: string | null;
  avatarId: string | null;
  interfaceLanguage: string;
  openAiApiKey: string | null;
  anthropicApiKey: string | null;
  geminiApiKey: string | null;
  customSystemPrompt: string | null;
  fullTranslation: boolean;
}
```

Добавить новый метод в объект `api` — сразу после `updateNames` (сейчас заканчивается на строке 243):

```ts
  // Тот же round-trip-всего-объекта паттерн, что updateNames — см. Settings'
  // комментарий выше про то, почему PUT нельзя слать частично.
  updateAvatarId: async (avatarId: string): Promise<void> => {
    const current = await request<Settings>('/settings');
    await request<Settings>('/settings', {
      method: 'PUT',
      body: JSON.stringify({ ...current, avatarId }),
    });
  },
```

- [ ] **Step 2: Прокинуть через AuthContext**

Modify `mobile/src/context/AuthContext.tsx`:

Расширить `AuthContextValue` (сейчас строки 11-27), добавить поле после `assistantName`:

```ts
interface AuthContextValue {
  isLoading: boolean;
  user: CurrentUser | null;
  displayName: string | null;
  assistantName: string | null;
  // 'olga' | 'male' — держится как string, не AvatarId-union: этот файл не
  // импортирует компонент аватара, чтобы не тянуть зависимость от UI-слоя
  // в контекст. Резолюция дефолта/неизвестных значений — на стороне
  // потребителей (HomeScreen.tsx, ProfileScreen.tsx), не здесь.
  avatarId: string | null;
  refreshProfile: () => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<void>;
  loginWithDeviceCode: (code: string) => Promise<void>;
  logout: () => Promise<void>;
}
```

Добавить state (после строки `const [assistantName, setAssistantName] = useState<string | null>(null);`):

```ts
  const [avatarId, setAvatarId] = useState<string | null>(null);
```

В `refreshProfile()` (сейчас строки 37-46) — добавить `setAvatarId`:

```ts
  async function refreshProfile() {
    try {
      const settings = await api.getSettings();
      setDisplayName(settings.displayName);
      setAssistantName(settings.assistantName);
      setAvatarId(settings.avatarId);
    } catch {
      // Best-effort — worst case the onboarding prompt just asks again
      // next time, same as if the name were genuinely never set.
    }
  }
```

В `setUnauthorizedHandler`'s callback (сейчас строки 58-62) и в `logout()` (сейчас строки 97-102) — добавить `setAvatarId(null)` рядом с существующими сбросами:

```ts
    setUnauthorizedHandler(() => {
      setUser(null);
      setDisplayName(null);
      setAssistantName(null);
      setAvatarId(null);
    });
```

```ts
  async function logout() {
    await setToken(null);
    setUser(null);
    setDisplayName(null);
    setAssistantName(null);
    setAvatarId(null);
  }
```

В возвращаемом `<AuthContext.Provider value={{...}}>` (сейчас строки 105-117) — добавить `avatarId`:

```ts
      value={{
        isLoading,
        user,
        displayName,
        assistantName,
        avatarId,
        refreshProfile,
        login,
        register,
        loginWithDeviceCode,
        logout,
      }}
```

- [ ] **Step 3: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 4: Commit**

```bash
cd mobile
git add src/api/client.ts src/context/AuthContext.tsx
git commit -m "Add avatarId to Settings API and AuthContext"
```

---

## Task 3: `LayeredAvatar` — параметризация под два аватара

**Files:**
- Create: `mobile/src/components/LayeredAvatar.tsx`
- Delete: `mobile/src/components/OlgaLayeredAvatar.tsx` (переименование, не отдельный файл)

**Interfaces:**
- Produces: `export type AvatarId = 'olga' | 'male';`, `export function LayeredAvatar(props: LayeredAvatarProps)` с `LayeredAvatarProps = { avatarId: AvatarId; state: VoiceState; sleeping: boolean; disabled?: boolean; onLoadError?: () => void }`.

- [ ] **Step 1: Создать `LayeredAvatar.tsx`**

Create `mobile/src/components/LayeredAvatar.tsx` — полное содержимое (перенос анимационной логики `OlgaLayeredAvatar.tsx` без изменений, плюс параметризация ассетов):

```tsx
import { useEffect, useRef } from 'react';
import { Animated, Image, StyleSheet, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { haloByState } from '../theme/amoled';

const AVATAR_SIZE = 320;

export type AvatarId = 'olga' | 'male';

interface AvatarAssetSet {
  base: number;
  eyesClosedOverlay: number;
  mouthOpenSmallOverlay: number;
  mouthOpenBigOverlay: number;
  // Мужской набор без бровей-оверлея — thinking у него отличается только
  // цветом halo, см. docs/designs/male_avatar_asset_plan.md's State Mapping
  // и docs/superpowers/specs/2026-07-10-male-avatar-and-voice-gender-design.md.
  browsThinkingOverlay?: number;
}

const AVATAR_ASSETS: Record<AvatarId, AvatarAssetSet> = {
  olga: {
    base: require('../../assets/avatar/olga_base.png'),
    eyesClosedOverlay: require('../../assets/avatar/olga_eyes_closed_overlay.png'),
    mouthOpenSmallOverlay: require('../../assets/avatar/olga_mouth_open_small_overlay.png'),
    mouthOpenBigOverlay: require('../../assets/avatar/olga_mouth_open_big_overlay.png'),
    browsThinkingOverlay: require('../../assets/avatar/olga_brows_thinking_overlay.png'),
  },
  male: {
    base: require('../../assets/avatar/male_base.png'),
    eyesClosedOverlay: require('../../assets/avatar/male_eyes_closed_overlay.png'),
    mouthOpenSmallOverlay: require('../../assets/avatar/male_mouth_open_small_overlay.png'),
    mouthOpenBigOverlay: require('../../assets/avatar/male_mouth_open_big_overlay.png'),
  },
};

interface LayeredAvatarProps {
  avatarId: AvatarId;
  state: VoiceState;
  sleeping: boolean;
  disabled?: boolean;
  onLoadError?: () => void;
}

export function LayeredAvatar({ avatarId, state, sleeping, disabled, onLoadError }: LayeredAvatarProps) {
  const assets = AVATAR_ASSETS[avatarId];
  const mouthSmall = useRef(new Animated.Value(0)).current;
  const mouthBig = useRef(new Animated.Value(0)).current;
  const blink = useRef(new Animated.Value(0)).current; // 0 = открыты, 1 = закрыты

  // Цикл рта во время speaking — то же чередование двух кадров, что
  // AvatarFace.tsx уже использует для своего нарисованного рта, только
  // кросс-фейдом между двумя overlay-картинками вместо scaleY фигуры.
  useEffect(() => {
    if (state === 'speaking') {
      const loop = Animated.loop(
        Animated.sequence([
          Animated.timing(mouthSmall, { toValue: 1, duration: 130, useNativeDriver: true }),
          Animated.timing(mouthSmall, { toValue: 0, duration: 0, useNativeDriver: true }),
          Animated.timing(mouthBig, { toValue: 1, duration: 130, useNativeDriver: true }),
          Animated.timing(mouthBig, { toValue: 0, duration: 0, useNativeDriver: true }),
        ])
      );
      loop.start();
      return () => loop.stop();
    }
    mouthSmall.setValue(0);
    mouthBig.setValue(0);
  }, [state, mouthSmall, mouthBig]);

  // Моргание — непрерывно вне зависимости от state, как в AvatarFace.tsx,
  // КРОМЕ sleeping, где closed-eyes overlay держится полностью открытым
  // (opacity: 1) статично, без моргания поверх него.
  useEffect(() => {
    if (sleeping) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const scheduleBlink = () => {
      const delay = 2200 + Math.random() * 2600;
      timer = setTimeout(() => {
        if (stopped) return;
        Animated.sequence([
          Animated.timing(blink, { toValue: 1, duration: 90, useNativeDriver: true }),
          Animated.timing(blink, { toValue: 0, duration: 130, useNativeDriver: true }),
        ]).start(() => scheduleBlink());
      }, delay);
    };
    scheduleBlink();
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [sleeping, blink]);

  const halo = haloByState[state];

  return (
    <View style={[styles.wrapper, disabled && styles.disabled]}>
      {/* Затухает, но не пропадает — на чистом AMOLED-чёрном фоне полное
          отсутствие halo + затемнённый портрет выглядело неотличимо от
          выключенного экрана (реальный физический тест: "экран
          отключается"), хотя useKeepAwake() исправно держит дисплей
          активным (см. HomeScreen.tsx) — проблема была чисто визуальная. */}
      <HaloGlow color={halo.color} intensity={sleeping ? halo.intensity * 0.25 : halo.intensity} size={AVATAR_SIZE} />
      <Image
        source={assets.base}
        style={[
          styles.portrait,
          sleeping && styles.sleepingPortrait,
          state === 'paused' && !sleeping && styles.pausedPortrait,
        ]}
        onError={onLoadError}
      />
      {sleeping ? (
        <Image source={assets.eyesClosedOverlay} style={styles.portrait} />
      ) : (
        <Animated.Image source={assets.eyesClosedOverlay} style={[styles.portrait, { opacity: blink }]} />
      )}
      {state === 'speaking' && (
        <>
          <Animated.Image source={assets.mouthOpenSmallOverlay} style={[styles.portrait, { opacity: mouthSmall }]} />
          <Animated.Image source={assets.mouthOpenBigOverlay} style={[styles.portrait, { opacity: mouthBig }]} />
        </>
      )}
      {state === 'thinking' && assets.browsThinkingOverlay && (
        <Image source={assets.browsThinkingOverlay} style={styles.portrait} />
      )}
    </View>
  );
}

// Halo как стопка полупрозрачных кругов за портретом — приближение
// radial-gradient без библиотек градиентов (expo-linear-gradient/
// react-native-svg сознательно не добавлены, см. spec). `color` — строка
// вида "R,G,B" без обёртки rgb(...), собирается в rgba() здесь.
function HaloGlow({ color, intensity, size }: { color: string; intensity: number; size: number }) {
  const rings = [
    { extra: 90, alpha: intensity * 0.15 },
    { extra: 55, alpha: intensity * 0.3 },
    { extra: 24, alpha: intensity * 0.55 },
  ];
  return (
    <View style={styles.haloContainer} pointerEvents="none">
      {rings.map((ring) => {
        const d = size + ring.extra;
        return (
          <View
            key={ring.extra}
            style={[
              styles.haloRing,
              {
                width: d,
                height: d,
                borderRadius: d / 2,
                backgroundColor: `rgba(${color},${ring.alpha})`,
              },
            ]}
          />
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    width: AVATAR_SIZE,
    height: AVATAR_SIZE,
    alignItems: 'center',
    justifyContent: 'center',
  },
  disabled: {
    opacity: 0.6,
  },
  portrait: {
    position: 'absolute',
    width: AVATAR_SIZE,
    height: AVATAR_SIZE,
    borderRadius: AVATAR_SIZE / 2,
  },
  // Затемнение через opacity поверх ЧЁРНОГО фона — визуально близко к
  // brightness()-фильтру из веб-мокапа, но не тот же механизм (RN Image
  // не имеет built-in CSS filter). Разные значения — sleeping заметно
  // темнее paused, см. spec.
  sleepingPortrait: {
    opacity: 0.65,
  },
  pausedPortrait: {
    opacity: 0.8,
  },
  haloContainer: {
    position: 'absolute',
    alignItems: 'center',
    justifyContent: 'center',
  },
  haloRing: {
    position: 'absolute',
  },
});
```

- [ ] **Step 2: Удалить старый файл**

```bash
cd mobile
git rm src/components/OlgaLayeredAvatar.tsx
```

(Потребители обновляются в Task 5 — до этого момента `tsc` в этом таске будет ругаться на пропавший импорт в `HomeScreen.tsx`, это ожидаемо и чинится следующим шагом ниже.)

- [ ] **Step 3: Временно поправить импорт в HomeScreen.tsx, чтобы tsc прошёл в рамках этого таска**

Modify `mobile/src/screens/HomeScreen.tsx` — заменить импорт и единственное место использования (полная перепроводка происходит в Task 5, здесь — минимальная правка только чтобы компиляция не была сломана между тасками):

```ts
import { LayeredAvatar } from '../components/LayeredAvatar';
```

вместо

```ts
import { OlgaLayeredAvatar } from '../components/OlgaLayeredAvatar';
```

и

```tsx
              <LayeredAvatar
                avatarId="olga"
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
```

вместо

```tsx
              <OlgaLayeredAvatar
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
```

- [ ] **Step 4: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 5: Живая проверка в превью**

```
preview_start
```

Временно (не коммитить) заменить в `mobile/App.tsx`'s `Root()` первой строкой `return <HomeScreen />;` — тот же приём обхода авторизации, что использовался в предыдущих тасках этой сессии. Перезагрузить, скриншотом/`preview_eval`-чтением DOM подтвердить, что портрет Ольги рендерится как раньше (320×320, halo, моргание). Затем временно поменять `avatarId="olga"` на `avatarId="male"` в `HomeScreen.tsx`, перезагрузить, подтвердить, что теперь рендерится МУЖСКОЙ портрет (другое изображение, `male_base.png` в `<img src>`). Вернуть `avatarId="olga"` и убрать обход авторизации — оба временных изменения (`App.tsx` и `avatarId="male"`) должны исчезнуть, `git diff --stat mobile/App.tsx` пустой, `HomeScreen.tsx` заканчивается ровно на `avatarId="olga"` из Step 3.

- [ ] **Step 6: Commit**

```bash
cd mobile
git add src/components/LayeredAvatar.tsx src/components/OlgaLayeredAvatar.tsx src/screens/HomeScreen.tsx
git commit -m "Extract parameterized LayeredAvatar from OlgaLayeredAvatar"
```

---

## Task 4: `systemSpeech.ts` — разметка пола голосов

**Files:**
- Modify: `mobile/src/tts/systemSpeech.ts`

**Interfaces:**
- Produces: `export type VoiceGender = 'male' | 'female';`, `export async function getVoiceGenderTags(): Promise<Record<string, VoiceGender>>`, `export async function setVoiceGender(identifier: string, gender: VoiceGender): Promise<void>`.

- [ ] **Step 1: Добавить хранилище тегов**

Modify `mobile/src/tts/systemSpeech.ts` — добавить константу рядом с `VOICE_PREFERENCE_KEY` (строка 5):

```ts
const VOICE_PREFERENCE_KEY = 'vass_voice_id';
const VOICE_GENDER_TAGS_KEY = 'vass_voice_gender_tags';
```

Добавить тип и кэш рядом с `cachedRussianVoice` (строка 30):

```ts
// undefined = not looked up yet this session, null = no Russian voice found.
let cachedRussianVoice: string | null | undefined;

export type VoiceGender = 'male' | 'female';

// undefined = not loaded yet this session. Separate from cachedRussianVoice
// — a different SecureStore key, a different shape (map, not a single id).
let cachedGenderTags: Record<string, VoiceGender> | undefined;
```

- [ ] **Step 2: Добавить функции чтения/записи тегов**

Modify `mobile/src/tts/systemSpeech.ts` — добавить сразу после `setVoicePreference` (сейчас заканчивается на строке 77, `}`):

```ts
// expo-speech's Voice type has no gender field at all ({identifier, name,
// quality, language} — confirmed by reading Speech.types.d.ts directly, not
// assumed) — Android's TTS engine exposes nothing programmatically usable
// here (see isLikelyLocalVoice's comment for the same class of gap on the
// local/network signal). Manual, user-driven tagging is the only reliable
// option; this is pure local storage for that tagging, no attempt at
// automatic classification.
export async function getVoiceGenderTags(): Promise<Record<string, VoiceGender>> {
  if (cachedGenderTags) return cachedGenderTags;
  try {
    const raw = await SecureStore.getItemAsync(VOICE_GENDER_TAGS_KEY);
    cachedGenderTags = raw ? JSON.parse(raw) : {};
  } catch {
    cachedGenderTags = {};
  }
  return cachedGenderTags;
}

export async function setVoiceGender(identifier: string, gender: VoiceGender): Promise<void> {
  const tags = await getVoiceGenderTags();
  const updated = { ...tags, [identifier]: gender };
  cachedGenderTags = updated;
  try {
    await SecureStore.setItemAsync(VOICE_GENDER_TAGS_KEY, JSON.stringify(updated));
  } catch {
    // no-op on platforms without a working SecureStore (web preview only) —
    // matches setVoicePreference's own catch above.
  }
}
```

- [ ] **Step 3: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 4: Живая проверка в превью**

```
preview_start
```

Через `preview_eval` напрямую вызвать (модуль недоступен из консоли напрямую — временно, не коммитя, добавить в любой уже смонтированный компонент, например `HomeScreen.tsx`, отладочный вызов сразу после существующих хуков:

```ts
useEffect(() => {
  (async () => {
    console.log('GENDER_TAGS_DEBUG before:', JSON.stringify(await getVoiceGenderTags()));
    await setVoiceGender('test-voice-1', 'male');
    console.log('GENDER_TAGS_DEBUG after:', JSON.stringify(await getVoiceGenderTags()));
  })();
}, []);
```

(с соответствующим временным импортом `getVoiceGenderTags`/`setVoiceGender` из `../tts/systemSpeech`). Перезагрузить превью, через `preview_console_logs` прочитать оба лога — подтвердить, что "before" пустой объект `{}` (или содержит только то, что могло остаться от предыдущего ручного теста), "after" содержит `{"test-voice-1":"male"}`. Убрать весь временный код, `git diff --stat mobile/src/screens/HomeScreen.tsx` пустой.

- [ ] **Step 5: Commit**

```bash
cd mobile
git add src/tts/systemSpeech.ts
git commit -m "Add local voice gender tagging storage"
```

---

## Task 5: `HomeScreen.tsx` — использование `avatarId` из аккаунта

**Files:**
- Modify: `mobile/src/screens/HomeScreen.tsx`

**Interfaces:**
- Consumes: `useAuth().avatarId: string | null` (Task 2), `LayeredAvatar`/`AvatarId` из `../components/LayeredAvatar` (Task 3).

- [ ] **Step 1: Резолюция дефолта и подключение**

Modify `mobile/src/screens/HomeScreen.tsx` — импорт (заменить временную правку из Task 3 Step 3):

```ts
import { LayeredAvatar, type AvatarId } from '../components/LayeredAvatar';
```

Деструктуризация `useAuth()` (сейчас `const { assistantName } = useAuth();`):

```ts
  const { assistantName, avatarId } = useAuth();
```

Добавить резолюцию дефолта сразу после (существующие пользователи без сохранённого значения — до этой фичи — получают `'olga'`, без миграции данных):

```ts
  const displayAvatarId: AvatarId = avatarId === 'male' ? 'male' : 'olga';
```

- [ ] **Step 2: Дефолтное имя по аватару**

Modify `mobile/src/screens/HomeScreen.tsx` — строку с именем в JSX (сейчас `<Text style={styles.identityName}>{assistantName || 'Ольга'}</Text>`):

```tsx
              <Text style={styles.identityName}>
                {assistantName || (displayAvatarId === 'male' ? 'Максим' : 'Ольга')}
              </Text>
```

- [ ] **Step 3: Подключить `LayeredAvatar` с реальным `avatarId`**

Modify `mobile/src/screens/HomeScreen.tsx` — заменить временный `avatarId="olga"` (из Task 3 Step 3) на реальное значение:

```tsx
              <LayeredAvatar
                avatarId={displayAvatarId}
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
```

- [ ] **Step 4: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 5: Живая проверка в превью**

Обход авторизации, как в Task 3 Step 5. Подтвердить: без сохранённого `avatarId` (AuthContext даёт `null` в веб-превью, т.к. `refreshProfile` не может достучаться до бэкенда) рендерится Ольга и имя "Ольга" (дефолт сработал). Убрать временный обход, `git diff --stat mobile/App.tsx` пустой.

- [ ] **Step 6: Commit**

```bash
cd mobile
git add src/screens/HomeScreen.tsx
git commit -m "Wire avatarId from account into HomeScreen"
```

---

## Task 6: `ProfileScreen.tsx` — выбор аватара + группировка/разметка голосов

**Files:**
- Modify: `mobile/src/screens/ProfileScreen.tsx`

**Interfaces:**
- Consumes: `useAuth().avatarId`/`refreshProfile` (Task 2), `api.updateAvatarId` (Task 2), `getVoiceGenderTags`/`setVoiceGender`/`VoiceGender` (Task 4), `AvatarId` (Task 3).

- [ ] **Step 1: Импорты и новое состояние**

Modify `mobile/src/screens/ProfileScreen.tsx` — импорты (в начале файла):

```ts
import { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Image,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import type { Voice } from 'expo-speech';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';
import {
  getResolvedVoiceId,
  getVoiceGenderTags,
  isLikelyLocalVoice,
  listRussianVoices,
  previewVoice,
  setVoiceGender,
  setVoicePreference,
  type VoiceGender,
} from '../tts/systemSpeech';
import type { AvatarId } from '../components/LayeredAvatar';
```

Деструктуризация `useAuth()` — добавить `avatarId`:

```ts
  const { displayName, assistantName, avatarId, refreshProfile, logout } = useAuth();
```

Новое состояние — после существующего голосового блока (`const [voicesError, setVoicesError] = useState<string | null>(null);`):

```ts
  const [genderTags, setGenderTags] = useState<Record<string, VoiceGender>>({});
  const [avatarSwitchError, setAvatarSwitchError] = useState<string | null>(null);
```

- [ ] **Step 2: Загрузить теги вместе с голосами**

Modify `mobile/src/screens/ProfileScreen.tsx` — существующий `useEffect`, который грузит `voices`/`selectedVoiceId`:

```ts
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [list, resolved, tags] = await Promise.all([
          listRussianVoices(),
          getResolvedVoiceId(),
          getVoiceGenderTags(),
        ]);
        if (cancelled) return;
        setVoices(list);
        setSelectedVoiceId(resolved);
        setGenderTags(tags);
      } catch (err) {
        if (!cancelled) setVoicesError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);
```

- [ ] **Step 3: Обработчики выбора аватара и разметки голоса**

Modify `mobile/src/screens/ProfileScreen.tsx` — добавить новые функции рядом с `handleSelectVoice`:

```ts
  function handleTagVoiceGender(identifier: string, gender: VoiceGender) {
    setGenderTags((prev) => ({ ...prev, [identifier]: gender }));
    void setVoiceGender(identifier, gender);
  }

  async function handleSelectAvatar(id: AvatarId) {
    setAvatarSwitchError(null);
    try {
      await api.updateAvatarId(id);
      await refreshProfile();
    } catch (err) {
      setAvatarSwitchError(err instanceof Error ? err.message : String(err));
      return;
    }
    // Переключение голоса — best-effort поверх уже успешно сохранённого
    // аватара: если голосов нужного пола ещё нет, аватар всё равно
    // переключился, просто голос не трогаем (см. spec — «группа пустая с
    // подсказкой», не блокирующая ошибка).
    const targetGender: VoiceGender = id === 'male' ? 'male' : 'female';
    const match = (voices ?? []).find((v) => genderTags[v.identifier] === targetGender);
    if (match) {
      setSelectedVoiceId(match.identifier);
      void setVoicePreference(match.identifier);
      previewVoice(match.identifier);
    }
  }
```

- [ ] **Step 4: Секция выбора аватара в JSX**

Modify `mobile/src/screens/ProfileScreen.tsx` — вставить новый блок между кнопкой «Сохранить» (конец блока имён) и `<View style={styles.divider} />`, который сейчас идёт перед секцией голоса:

```tsx
      {nameError && <Text style={styles.error}>{nameError}</Text>}
      <Pressable
        style={[styles.button, savingName && styles.buttonDisabled]}
        onPress={handleSaveNames}
        disabled={savingName}
      >
        {savingName ? (
          <ActivityIndicator color="#fff" />
        ) : (
          <Text style={styles.buttonText}>Сохранить</Text>
        )}
      </Pressable>

      <View style={styles.divider} />

      <Text style={styles.label}>Аватар ассистента</Text>
      <View style={styles.avatarPickerRow}>
        <Pressable
          style={[styles.avatarOption, (avatarId ?? 'olga') !== 'male' && styles.avatarOptionSelected]}
          onPress={() => handleSelectAvatar('olga')}
        >
          <Image source={require('../../assets/avatar/olga_base.png')} style={styles.avatarThumb} />
          <Text style={styles.hint}>Ольга</Text>
        </Pressable>
        <Pressable
          style={[styles.avatarOption, avatarId === 'male' && styles.avatarOptionSelected]}
          onPress={() => handleSelectAvatar('male')}
        >
          <Image source={require('../../assets/avatar/male_base.png')} style={styles.avatarThumb} />
          <Text style={styles.hint}>Максим</Text>
        </Pressable>
      </View>
      {avatarSwitchError && <Text style={styles.error}>{avatarSwitchError}</Text>}

      <View style={styles.divider} />
```

(Существующий `<View style={styles.divider} />` перед секцией голоса теперь этот, только что добавленный — не дублировать ещё один следом.)

- [ ] **Step 5: Группировка голосов по полу**

Modify `mobile/src/screens/ProfileScreen.tsx` — заменить плоский рендер `displayVoices?.map(...)` на группированный. Текущий код (после блока `showingNetworkVoices`/`voicesError`/loading/empty-state, всё это остаётся без изменений):

```tsx
      {displayVoices?.map((voice, index) => {
        const selected = voice.identifier === selectedVoiceId;
        const needsNetwork = !isLikelyLocalVoice(voice.identifier);
        return (
          <Pressable
            key={voice.identifier}
            style={[styles.voiceRow, selected && styles.voiceRowSelected]}
            onPress={() => handleSelectVoice(voice)}
          >
            <Text style={[styles.voiceName, selected && styles.voiceNameSelected]}>
              Голос {index + 1}
              {needsNetwork ? ' 🌐' : ''}
            </Text>
            {selected && <Text style={styles.voiceCheck}>✓</Text>}
          </Pressable>
        );
      })}
```

Заменить на (вычисление групп — сразу после `const showingNetworkVoices = ...` строки, JSX-рендер — на месте старого блока выше):

Добавить после `const showingNetworkVoices = displayVoices?.some((v) => !isLikelyLocalVoice(v.identifier)) ?? false;`:

```ts
  const maleVoices = displayVoices?.filter((v) => genderTags[v.identifier] === 'male') ?? [];
  const femaleVoices = displayVoices?.filter((v) => genderTags[v.identifier] === 'female') ?? [];
  const untaggedVoices = displayVoices?.filter((v) => !genderTags[v.identifier]) ?? [];

  function renderVoiceGroup(label: string, groupVoices: Voice[]) {
    if (groupVoices.length === 0) return null;
    return (
      <View key={label}>
        <Text style={styles.voiceGroupLabel}>{label}</Text>
        {groupVoices.map((voice, index) => {
          const selected = voice.identifier === selectedVoiceId;
          const needsNetwork = !isLikelyLocalVoice(voice.identifier);
          const tag = genderTags[voice.identifier];
          return (
            <View key={voice.identifier} style={[styles.voiceRow, selected && styles.voiceRowSelected]}>
              <Pressable style={styles.voiceRowMain} onPress={() => handleSelectVoice(voice)}>
                <Text style={[styles.voiceName, selected && styles.voiceNameSelected]}>
                  Голос {index + 1}
                  {needsNetwork ? ' 🌐' : ''}
                </Text>
                {selected && <Text style={styles.voiceCheck}>✓</Text>}
              </Pressable>
              <View style={styles.genderToggle}>
                <Pressable
                  style={[styles.genderButton, tag === 'male' && styles.genderButtonActive]}
                  onPress={() => handleTagVoiceGender(voice.identifier, 'male')}
                >
                  <Text style={[styles.genderButtonText, tag === 'male' && styles.genderButtonTextActive]}>М</Text>
                </Pressable>
                <Pressable
                  style={[styles.genderButton, tag === 'female' && styles.genderButtonActive]}
                  onPress={() => handleTagVoiceGender(voice.identifier, 'female')}
                >
                  <Text style={[styles.genderButtonText, tag === 'female' && styles.genderButtonTextActive]}>Ж</Text>
                </Pressable>
              </View>
            </View>
          );
        })}
      </View>
    );
  }
```

И заменить сам JSX-блок рендера списка на:

```tsx
      {renderVoiceGroup('Мужские голоса', maleVoices)}
      {renderVoiceGroup('Женские голоса', femaleVoices)}
      {renderVoiceGroup('Не размечено', untaggedVoices)}
```

- [ ] **Step 6: Добавить стили**

Modify `mobile/src/screens/ProfileScreen.tsx` — добавить в `StyleSheet.create({...})`:

```ts
  avatarPickerRow: {
    flexDirection: 'row',
    gap: 16,
    marginBottom: 12,
  },
  avatarOption: {
    flex: 1,
    alignItems: 'center',
    padding: 12,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: '#ccc',
  },
  avatarOptionSelected: {
    borderColor: '#4a6fa5',
    backgroundColor: '#f0f4fa',
  },
  avatarThumb: {
    width: 72,
    height: 72,
    borderRadius: 36,
    marginBottom: 8,
  },
  voiceGroupLabel: {
    fontSize: 14,
    fontWeight: '700',
    color: '#666',
    marginTop: 8,
    marginBottom: 8,
  },
  voiceRowMain: {
    flex: 1,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  genderToggle: {
    flexDirection: 'row',
    gap: 6,
    marginLeft: 12,
  },
  genderButton: {
    width: 32,
    height: 32,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#ccc',
    alignItems: 'center',
    justifyContent: 'center',
  },
  genderButtonActive: {
    borderColor: '#4a6fa5',
    backgroundColor: '#4a6fa5',
  },
  genderButtonText: {
    fontSize: 13,
    fontWeight: '700',
    color: '#666',
  },
  genderButtonTextActive: {
    color: '#fff',
  },
```

`voiceRow`'s существующий стиль (`flexDirection: 'row', justifyContent: 'space-between', ...`) уже подходит как контейнер для нового `voiceRowMain` + `genderToggle` рядом — менять не нужно.

- [ ] **Step 7: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 8: Живая проверка в превью**

Обход авторизации (как в предыдущих тасках), прямой рендер `<ProfileScreen mode="settings" onDone={() => {}} />`. Подтвердить:
- Секция «Аватар ассистента» с двумя превью видна, тап по «Максим» вызывает `handleSelectAvatar('male')` (проверить через `preview_network` — должен уйти `PUT /settings`, ожидаемо 401/ошибка сети в веб-превью без реальной сессии — это `avatarSwitchError`, не блокер, тот же класс известного ограничения, что и у кнопки device-link кода).
- Голоса на устройстве превью (если есть хоть один в браузере) рендерятся под «Не размечено» изначально. Тап по «М» на любом голосе — голос перемещается под «Мужские голоса» на следующий рендер, тег сохраняется (`setVoiceGender` вызывается — можно подтвердить по `preview_console_logs`, если временно добавить `console.log`, или просто по факту перемещения строки между группами).

- [ ] **Step 9: Commit**

```bash
cd mobile
git add src/screens/ProfileScreen.tsx
git commit -m "Add avatar picker and voice gender grouping to ProfileScreen"
```

---

## Финальная проверка перед PR

- [ ] `cd mobile && npx tsc --noEmit` — чистый на финальном состоянии всех тасков вместе.
- [ ] `cd VoiceAssistant.API && dotnet build` — чистый.
- [ ] Полный проход по всем состояниям в веб-превью ещё раз, на финальной версии: переключение аватара, группировка/разметка голосов, автоподбор голоса при переключении (с хотя бы одним размеченным голосом каждого пола).
- [ ] `git log --oneline` показывает 6 коммитов подряд (по одному на таск).
- [ ] **После мержа — бэкенд ОБЯЗАТЕЛЬНО передеплоить на VPS** (`db.Database.Migrate()` в `Program.cs` применяет миграцию автоматически при старте контейнера, но сам контейнер нужно пересобрать/перезапустить с новым кодом) — без этого `PUT /settings` с `avatarId` будет либо падать, либо (если бэкенд просто игнорирует незнакомое поле) молча не сохранять выбор. Мобильная фича без задеплоенного бэкенда не работает даже частично.
- [ ] Собрать APK через WSL, передать на физический тест: читаемость мужского портрета на реальном экране, отличимость `thinking` без смены бровей, разметка голосов на реальном списке устройства, автопереключение голоса при смене аватара.
- [ ] Обновить `docs/react-native/BACKLOG.md` отдельным docs-PR после мержа, как заведено в этой сессии.
