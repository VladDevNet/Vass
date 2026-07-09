# AMOLED-редизайн главного экрана + layered-аватар Ольги — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Заменить текущий светлый технический `HomeScreen` на premium AMOLED-чёрный экран с layered-PNG аватаром Ольги, сохранив всю существующую голосовую механику (tap/long-press, turn-taking, pause/resume) нетронутой.

**Architecture:** Пять новых файлов (тема, хук sleep-таймера, три презентационных компонента) + перестройка `HomeScreen.tsx`/`ProfileScreen.tsx`. `useVoiceChat.ts`/`useVad.ts`/`systemSpeech.ts` не меняются вообще — весь редизайн на уровне UI-слоя.

**Tech Stack:** React Native 0.86, Expo SDK 57, TypeScript. Новая зависимость: `react-native-safe-area-context` (см. Task 1 — текущий `SafeAreaView` из `react-native` deprecated в установленной версии).

## Global Constraints

- Проверить https://docs.expo.dev/versions/v57.0.0/ перед правкой кода, если меняется что-то за пределами уже проверенных в этом плане API (`mobile/AGENTS.md`).
- `npx tsc --noEmit` чистый после каждого таска.
- `useVoiceChat.ts` НЕ меняется ни в одном таске — весь новый визуал читает существующий `VoiceState`/`transcript`/`reply`/`error`/`forceFinalize`/`pauseConversation`, ничего не пишет обратно.
- Halo — стопка полупрозрачных `View`-кругов за портретом, БЕЗ библиотек градиентов (`expo-linear-gradient`/`react-native-svg` не добавляются — см. spec, раздел «Рассмотренные подходы»). Собственное золотое свечение портрета не перекрашивается.
- Затемнение портрета (paused/sleeping) — через `opacity` поверх чёрного фона, не через CSS-`filter` (в RN `Image` его нет).
- Спек: [`docs/superpowers/specs/2026-07-09-amoled-avatar-redesign-design.md`](../specs/2026-07-09-amoled-avatar-redesign-design.md).

---

## Task 1: Фундамент — safe-area зависимость, AMOLED-тема, sleep-таймер

**Files:**
- Modify: `mobile/package.json`
- Modify: `mobile/App.tsx`
- Create: `mobile/src/theme/amoled.ts`
- Create: `mobile/src/hooks/useSleepTimer.ts`

**Interfaces:**
- Produces: `amoled: { background, glassBackground, glassBackgroundStrong, glassBorder, textPrimary, textSecondary }` (все `string`, hex/rgba).
- Produces: `haloByState: Record<VoiceState, { color: string; intensity: number }>` — `color` без обёртки `rgb(...)`, только `"R,G,B"`, чтобы вызывающий код сам собирал `rgba(${color},${alpha})`.
- Produces: `useSleepTimer(active: boolean, sleepAfterMs: number): boolean`.

- [ ] **Step 1: Добавить `react-native-safe-area-context`**

Текущий `SafeAreaView` из `react-native` в установленной версии (0.86.0) помечен deprecated (проверено напрямую в `node_modules/react-native/index.js`: "SafeAreaView has been deprecated and will be removed in a future release"). Экран без корректных safe-area отступов на чёрном фоне будет выглядеть сломанным на вырезах/чёлках — стандартная замена нужна, не опциональна.

```bash
cd mobile
npx expo install react-native-safe-area-context
```

Ожидаемо: `npx expo install` сам подбирает версию под Expo SDK 57 и добавляет в `package.json`.

- [ ] **Step 2: Обернуть корень приложения в `SafeAreaProvider`**

Modify `mobile/App.tsx` — добавить импорт и обернуть `<AuthProvider>`:

```tsx
import { useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { AuthProvider, useAuth } from './src/context/AuthContext';
import { dismissOnboarding, isOnboardingDismissed } from './src/api/client';
import { LoginScreen } from './src/screens/LoginScreen';
import { HomeScreen } from './src/screens/HomeScreen';
import { ProfileScreen } from './src/screens/ProfileScreen';

// ... Root() unchanged ...

export default function App() {
  return (
    <SafeAreaProvider>
      <AuthProvider>
        <Root />
        <StatusBar style="auto" />
      </AuthProvider>
    </SafeAreaProvider>
  );
}
```

Тело `Root()` и `styles` не меняются — только новый импорт и обёртка вокруг возвращаемого JSX в `App()`.

- [ ] **Step 3: Проверить установку**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок (новый импорт `SafeAreaProvider` резолвится).

- [ ] **Step 4: Создать файл темы**

Create `mobile/src/theme/amoled.ts`:

```ts
import type { VoiceState } from '../hooks/useVoiceChat';

// AMOLED-палитра для редизайна главного экрана — см.
// docs/superpowers/specs/2026-07-09-amoled-avatar-redesign-design.md
export const amoled = {
  background: '#000000',
  glassBackground: 'rgba(255,255,255,0.06)',
  glassBackgroundStrong: 'rgba(255,255,255,0.10)',
  glassBorder: 'rgba(255,255,255,0.12)',
  textPrimary: '#F8FAFC',
  textSecondary: '#94A3B8',
} as const;

export interface HaloStyle {
  // "R,G,B" без обёртки rgb(...) — вызывающий код собирает
  // `rgba(${color},${alpha})` сам, разной alpha для разных колец.
  color: string;
  // 0..1, множитель для alpha самого яркого (ближнего к портрету) кольца.
  intensity: number;
}

// Record, не просто объект — TS требует значение для КАЖДОГО VoiceState,
// так что забытое состояние при будущем изменении VoiceState — ошибка
// компиляции, а не молчаливый пробел в UI (тот же приём, что уже
// использует AvatarFace.tsx для HEAD_COLOR).
export const haloByState: Record<VoiceState, HaloStyle> = {
  idle: { color: '245,158,11', intensity: 0.45 },
  recording: { color: '59,130,246', intensity: 0.55 },
  thinking: { color: '168,85,247', intensity: 0.55 },
  speaking: { color: '245,158,11', intensity: 0.7 },
  paused: { color: '148,163,184', intensity: 0.28 },
};
```

- [ ] **Step 5: Создать хук sleep-таймера**

Create `mobile/src/hooks/useSleepTimer.ts`:

```ts
import { useEffect, useState } from 'react';

// Чисто визуальный, презентационный таймер — НЕ часть VoiceState, не
// трогает useVoiceChat.ts. См. spec: VoiceState глубоко завязан на живую
// логику VAD/микрофона, и добавление туда нового значения потребовало бы
// такого же цикла ревью, как pause/resume (4 раунда, см. PR #59).
//
// `active`, а не сырой VoiceState — сознательно: единственное, что имеет
// значение для сна, это "сейчас idle или нет", а не конкретный ЛИБО
// предыдущий стейт. HomeScreen передаёт `state === 'idle'`.
//
// Любое изменение `active` (не только true→false, но и false→false у
// другого рендера — React всё равно не перезапустит эффект без реальной
// смены значения) сбрасывает sleeping и таймер. Пока active остаётся
// false (recording/thinking/speaking/paused), эффект не перезапускается
// вообще — и не должен: спать можно только из during idle.
export function useSleepTimer(active: boolean, sleepAfterMs: number): boolean {
  const [sleeping, setSleeping] = useState(false);

  useEffect(() => {
    setSleeping(false);
    if (!active) return;
    const timer = setTimeout(() => setSleeping(true), sleepAfterMs);
    return () => clearTimeout(timer);
  }, [active, sleepAfterMs]);

  return sleeping;
}
```

- [ ] **Step 6: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 7: Визуально проверить sleep-таймер живым тестом в превью**

В проекте нет фреймворка для юнит-тестов React-хуков (`mobile/package.json` без test-скрипта — уже подтверждено независимым ревью в этой сессии на PR #59). Логика хука простая (guarded setTimeout, сброс по смене одного boolean) и не safety-critical (в худшем случае — аватар "засыпает" чуть раньше/позже, чисто косметика, не гонка вроде тех, что чинили в pause/resume). Прямая проверка — вместо автотеста:

Временно, ТОЛЬКО для проверки этого шага (откатить перед коммитом), в любом уже смонтированном экране:

```tsx
const sleeping = useSleepTimer(true, 3000); // 3с вместо 90с — для быстрой проверки
console.log('sleeping:', sleeping);
```

Через веб-превью (`preview_start` → `preview_console_logs`) убедиться: `sleeping: false` сразу после монтирования, `sleeping: true` появляется в логе примерно через 3 секунды. Убрать тестовый код перед коммитом — в финальной интеграции (Task 5) хук вызывается с реальным `SLEEP_AFTER_MS = 90_000`.

- [ ] **Step 8: Commit**

```bash
cd mobile
git add package.json package-lock.json App.tsx src/theme/amoled.ts src/hooks/useSleepTimer.ts
git commit -m "Add AMOLED theme constants, safe-area dependency, sleep timer hook"
```

---

## Task 2: `OlgaLayeredAvatar`

**Files:**
- Create: `mobile/src/components/OlgaLayeredAvatar.tsx`

**Interfaces:**
- Consumes: `VoiceState` from `../hooks/useVoiceChat`; `haloByState` from `../theme/amoled` (Task 1).
- Produces: `OlgaLayeredAvatar({ state, sleeping, disabled?, onLoadError? }: OlgaLayeredAvatarProps)` — default export не используется, именованный экспорт (матчит остальные компоненты проекта — `AvatarFace`, `ConversationPeek` и т.д.).

- [ ] **Step 1: Создать компонент**

Create `mobile/src/components/OlgaLayeredAvatar.tsx`:

```tsx
import { useEffect, useRef } from 'react';
import { Animated, Image, StyleSheet, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { haloByState } from '../theme/amoled';

const AVATAR_SIZE = 220;

interface OlgaLayeredAvatarProps {
  state: VoiceState;
  sleeping: boolean;
  disabled?: boolean;
  onLoadError?: () => void;
}

export function OlgaLayeredAvatar({ state, sleeping, disabled, onLoadError }: OlgaLayeredAvatarProps) {
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
      {!sleeping && <HaloGlow color={halo.color} intensity={halo.intensity} size={AVATAR_SIZE} />}
      <Image
        source={require('../../assets/avatar/olga_base.png')}
        style={[
          styles.portrait,
          sleeping && styles.sleepingPortrait,
          state === 'paused' && !sleeping && styles.pausedPortrait,
        ]}
        onError={onLoadError}
      />
      {sleeping ? (
        <Image source={require('../../assets/avatar/olga_eyes_closed_overlay.png')} style={styles.portrait} />
      ) : (
        <Animated.Image
          source={require('../../assets/avatar/olga_eyes_closed_overlay.png')}
          style={[styles.portrait, { opacity: blink }]}
        />
      )}
      {state === 'speaking' && (
        <>
          <Animated.Image
            source={require('../../assets/avatar/olga_mouth_open_small_overlay.png')}
            style={[styles.portrait, { opacity: mouthSmall }]}
          />
          <Animated.Image
            source={require('../../assets/avatar/olga_mouth_open_big_overlay.png')}
            style={[styles.portrait, { opacity: mouthBig }]}
          />
        </>
      )}
      {state === 'thinking' && (
        <Image source={require('../../assets/avatar/olga_brows_thinking_overlay.png')} style={styles.portrait} />
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
    opacity: 0.5,
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

- [ ] **Step 2: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок. Если ошибка о неизвестном модуле `'../../assets/avatar/olga_base.png'` — проверить `mobile/global.d.ts` или `mobile/expo-env.d.ts` на предмет объявления типов для PNG-импортов (Expo TypeScript-шаблон обычно уже включает `declare module '*.png'` глобально; если файла нет — создать `mobile/src/types/images.d.ts` с `declare module '*.png' { const value: number; export default value; }`).

- [ ] **Step 3: Commit**

```bash
cd mobile
git add src/components/OlgaLayeredAvatar.tsx
git commit -m "Add OlgaLayeredAvatar component"
```

(Полная визуальная проверка — в Task 5, когда компонент подключён к реальному `HomeScreen` и виден в превью со всеми состояниями сразу; изолированный рендер одного компонента без экрана вокруг него менее показателен и в проекте нет playground-инструмента для этого.)

---

## Task 3: `ConversationPeek`

**Files:**
- Create: `mobile/src/components/ConversationPeek.tsx`

**Interfaces:**
- Consumes: `VoiceState` from `../hooks/useVoiceChat`; `amoled` from `../theme/amoled`.
- Produces: `ConversationPeek({ transcript, reply, state }: ConversationPeekProps)`.

- [ ] **Step 1: Создать компонент**

Create `mobile/src/components/ConversationPeek.tsx`:

```tsx
import { StyleSheet, Text, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface ConversationPeekProps {
  transcript: string;
  reply: string;
  state: VoiceState;
}

// Одна тёмная стеклянная плашка вместо двух подписанных bubble — показывает
// ТОЛЬКО актуальную для текущего state строку (что говорит пользователь во
// время recording, что отвечает ассистент во время speaking). Пусто — не
// плейсхолдер-фраза, компонент просто ничего не рендерит: минимализм
// важнее заполненности, см. spec.
export function ConversationPeek({ transcript, reply, state }: ConversationPeekProps) {
  const text = state === 'recording' ? transcript : state === 'speaking' ? reply : '';
  if (!text) return null;

  return (
    <View style={styles.peek}>
      <Text style={styles.text} numberOfLines={2}>
        {text}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  peek: {
    alignSelf: 'stretch',
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 24,
    paddingVertical: 14,
    paddingHorizontal: 20,
    marginBottom: 16,
  },
  text: {
    color: amoled.textPrimary,
    fontSize: 16,
    lineHeight: 22,
  },
});
```

- [ ] **Step 2: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 3: Commit**

```bash
cd mobile
git add src/components/ConversationPeek.tsx
git commit -m "Add ConversationPeek component"
```

---

## Task 4: `VoiceControlDock`

**Files:**
- Create: `mobile/src/components/VoiceControlDock.tsx`

**Interfaces:**
- Consumes: `VoiceState` from `../hooks/useVoiceChat`; `amoled` from `../theme/amoled`.
- Produces: `VoiceControlDock({ state, onSettingsPress, onHistoryPress, onMicPress, onMicLongPress, navigationDisabled }: VoiceControlDockProps)`.

Иконки — без новой библиотеки иконок (в проекте её нет и добавлять ради одного экрана не стоит): простые текстовые/эмодзи-глифы в круглых `Pressable`, тем же духом, что уже есть в остальном приложении (текстовые кнопки).

- [ ] **Step 1: Создать компонент**

Create `mobile/src/components/VoiceControlDock.tsx`:

```tsx
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface VoiceControlDockProps {
  state: VoiceState;
  onSettingsPress: () => void;
  onHistoryPress: () => void;
  onMicPress: () => void;
  onMicLongPress: () => void;
  navigationDisabled: boolean;
}

const MIC_GLYPH: Record<VoiceState, string> = {
  idle: '🎙️',
  recording: '🎙️',
  thinking: '…',
  speaking: '⏸',
  paused: '▶',
};

export function VoiceControlDock({
  state,
  onSettingsPress,
  onHistoryPress,
  onMicPress,
  onMicLongPress,
  navigationDisabled,
}: VoiceControlDockProps) {
  return (
    <View style={styles.dock}>
      <Pressable
        style={[styles.sideButton, navigationDisabled && styles.sideButtonDisabled]}
        onPress={onSettingsPress}
        disabled={navigationDisabled}
        accessibilityLabel="Настройки"
      >
        <Text style={styles.sideGlyph}>⚙️</Text>
      </Pressable>

      <Pressable
        style={styles.micButton}
        onPress={onMicPress}
        onLongPress={onMicLongPress}
        accessibilityLabel="Голосовое управление"
      >
        <Text style={styles.micGlyph}>{MIC_GLYPH[state]}</Text>
      </Pressable>

      <Pressable
        style={[styles.sideButton, navigationDisabled && styles.sideButtonDisabled]}
        onPress={onHistoryPress}
        disabled={navigationDisabled}
        accessibilityLabel="История"
      >
        <Text style={styles.sideGlyph}>🕐</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  dock: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    alignSelf: 'stretch',
    paddingHorizontal: 24,
  },
  sideButton: {
    width: 52,
    height: 52,
    borderRadius: 26,
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    alignItems: 'center',
    justifyContent: 'center',
  },
  sideButtonDisabled: {
    opacity: 0.4,
  },
  sideGlyph: {
    fontSize: 20,
  },
  micButton: {
    width: 72,
    height: 72,
    borderRadius: 36,
    backgroundColor: amoled.glassBackgroundStrong,
    borderWidth: 2,
    borderColor: 'rgba(245,158,11,0.6)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  micGlyph: {
    fontSize: 28,
  },
});
```

- [ ] **Step 2: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 3: Commit**

```bash
cd mobile
git add src/components/VoiceControlDock.tsx
git commit -m "Add VoiceControlDock component"
```

---

## Task 5: Перестройка `HomeScreen.tsx`

**Files:**
- Modify: `mobile/src/screens/HomeScreen.tsx` (полная замена тела компонента и стилей)

**Interfaces:**
- Consumes: `OlgaLayeredAvatar` (Task 2), `ConversationPeek` (Task 3), `VoiceControlDock` (Task 4), `useSleepTimer` (Task 1), `amoled` (Task 1). `useVoiceChat`, `AvatarFace`, `useAuth`, `api` — уже существующие, без изменений сигнатур.
- Produces: `HomeScreen()` — сигнатура компонента не меняется (`export function HomeScreen()`), потребители (`App.tsx`) не трогаются.

Этот таск убирает с экрана device-link и logout — они переезжают в `ProfileScreen` в Task 6. **Порядок важен: слить Task 5 до Task 6 нельзя** (пользователь на секунду теряет доступ к logout/device-link) — но оба таска в одном PR, так что на момент мержа функциональность не теряется ни на миг видимого пользователю состояния.

- [ ] **Step 1: Полностью заменить `HomeScreen.tsx`**

Modify `mobile/src/screens/HomeScreen.tsx` — заменить содержимое файла целиком:

```tsx
import { useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useKeepAwake } from 'expo-keep-awake';
import { StatusBar } from 'expo-status-bar';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';
import { useVoiceChat } from '../hooks/useVoiceChat';
import { useSleepTimer } from '../hooks/useSleepTimer';
import { AvatarFace } from '../components/AvatarFace';
import { OlgaLayeredAvatar } from '../components/OlgaLayeredAvatar';
import { ConversationPeek } from '../components/ConversationPeek';
import { VoiceControlDock } from '../components/VoiceControlDock';
import { amoled } from '../theme/amoled';
import { ProfileScreen } from './ProfileScreen';
import { ChatHistoryScreen } from './ChatHistoryScreen';

const SLEEP_AFTER_MS = 90_000;

const HEADLINE: Record<string, string> = {
  idle: 'Слушаю вас…',
  recording: 'Слышу вас…',
  thinking: 'Думаю…',
  speaking: 'Отвечаю…',
  paused: 'На паузе',
};

const SUBTITLE: Record<string, string> = {
  idle: 'Можно говорить естественно',
  recording: 'Собираю мысль',
  thinking: 'Сейчас отвечу',
  speaking: '',
  paused: 'Продолжим, когда будете готовы',
};

const PRESENCE_LABEL: Record<string, string> = {
  idle: 'рядом',
  recording: 'слушает',
  thinking: 'думает',
  speaking: 'говорит',
  paused: 'на паузе',
};

export function HomeScreen() {
  // A slower-paced conversation with pauses between turns is normal here —
  // the screen locking mid-conversation would be more disruptive than a
  // phone that stays awake while this screen is open, so this covers the
  // whole screen, not just the active recording/speaking states.
  useKeepAwake();

  const { assistantName } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  // Ошибка загрузки любого слоя OlgaLayeredAvatar — падаем на AvatarFace
  // на остаток сессии, без retry-петли. См. spec, «Обработка ошибок».
  const [assetsFailed, setAssetsFailed] = useState(false);
  const { state, transcript, reply, error, forceFinalize, pauseConversation } = useVoiceChat(sessionId);

  const sleeping = useSleepTimer(state === 'idle', SLEEP_AFTER_MS);

  useEffect(() => {
    let cancelled = false;
    api
      .getSessions()
      .then((sessions) => {
        if (!cancelled && sessions.length > 0) setSessionId(sessions[0].id);
      })
      .catch((err) => {
        if (!cancelled) setSessionError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  if (showSettings) {
    return <ProfileScreen mode="settings" onDone={() => setShowSettings(false)} />;
  }

  if (showHistory && sessionId) {
    return <ChatHistoryScreen sessionId={sessionId} onDone={() => setShowHistory(false)} />;
  }

  // Та же логика, что была: единственное состояние с реально нечем
  // заняться — отсутствие сессии. 'thinking' больше НЕ блокирует нажатие —
  // long-press во время thinking ставит на паузу (см. pauseConversation),
  // а forceFinalize сам по себе безопасно ничего не делает в 'thinking'.
  const disabled = !sessionId;
  const navigationDisabled = state !== 'idle' || !sessionId;

  return (
    <SafeAreaView style={styles.safeArea} edges={['top', 'bottom']}>
      <StatusBar style="light" />
      <View style={styles.container}>
        {sessionError && <Text style={styles.error}>{sessionError}</Text>}

        <View style={styles.identityRow}>
          <View style={styles.identityLeft}>
            <View style={styles.onlineDot} />
            <View>
              <Text style={styles.identityName}>{assistantName || 'Ольга'}</Text>
              <Text style={styles.identityPresence}>{PRESENCE_LABEL[state]}</Text>
            </View>
          </View>
          <Pressable
            style={styles.profileButton}
            onPress={() => setShowSettings(true)}
            disabled={navigationDisabled}
            accessibilityLabel="Профиль"
          >
            <Text style={styles.profileGlyph}>👤</Text>
          </Pressable>
        </View>

        {!sleeping && (
          <View style={styles.headlineBlock}>
            <Text style={styles.headline}>{HEADLINE[state]}</Text>
            {!!SUBTITLE[state] && <Text style={styles.subtitle}>{SUBTITLE[state]}</Text>}
          </View>
        )}

        <View style={styles.avatarStage}>
          <Pressable onPress={forceFinalize} onLongPress={() => void pauseConversation()} disabled={disabled}>
            {assetsFailed ? (
              <AvatarFace state={state} />
            ) : (
              <OlgaLayeredAvatar
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
            )}
          </Pressable>
        </View>

        <ConversationPeek transcript={transcript} reply={reply} state={state} />
        {error && <Text style={styles.error}>{error}</Text>}

        <VoiceControlDock
          state={state}
          onSettingsPress={() => setShowSettings(true)}
          onHistoryPress={() => setShowHistory(true)}
          onMicPress={forceFinalize}
          onMicLongPress={() => void pauseConversation()}
          navigationDisabled={navigationDisabled}
        />
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: amoled.background,
  },
  container: {
    flex: 1,
    paddingHorizontal: 20,
    paddingTop: 8,
    paddingBottom: 20,
    justifyContent: 'space-between',
  },
  identityRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  identityLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  onlineDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: '#3B82F6',
  },
  identityName: {
    color: amoled.textPrimary,
    fontSize: 17,
    fontWeight: '700',
  },
  identityPresence: {
    color: amoled.textSecondary,
    fontSize: 13,
  },
  profileButton: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    alignItems: 'center',
    justifyContent: 'center',
  },
  profileGlyph: {
    fontSize: 18,
  },
  headlineBlock: {
    marginTop: 24,
  },
  headline: {
    color: amoled.textPrimary,
    fontSize: 34,
    fontWeight: '800',
  },
  subtitle: {
    color: amoled.textSecondary,
    fontSize: 15,
    marginTop: 6,
  },
  avatarStage: {
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
  },
  error: {
    color: '#F87171',
    marginBottom: 12,
    textAlign: 'center',
  },
});
```

- [ ] **Step 2: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок. Если ругается на неиспользуемые `user`, `displayName`, `logout` (были в старом `useAuth()` деструктуринге, в новом убраны) — это ожидаемо, они больше не нужны в этом файле (переехали в `ProfileScreen`, Task 6).

- [ ] **Step 3: Визуальная проверка всех состояний в веб-превью**

```
preview_start
```

Затем через `preview_eval` по очереди форсировать значения (поскольку реальный голосовой цикл недоступен в веб без микрофона/логина, временно хардкодить `state`/`sleeping` в `OlgaLayeredAvatar`/`HEADLINE` вызовах прямо в файле для этой проверки — например, `state="recording"` вместо `state={state}` — и откатить перед коммитом), либо, если тестовый логин через веб проходит (см. риск CORS, встречавшийся в этой сессии ранее) — пройти реальный сценарий.

Проверить визуально по `preview_screenshot`:
- Чёрный фон, без белых артефактов вокруг PNG-слоёв.
- `idle`/`recording`/`thinking`/`speaking`/`paused` визуально различимы (halo-цвет, headline).
- `sleeping` (state идёт `idle`, `sleeping=true`) — headline/subtitle скрыты, портрет затемнён, closed-eyes overlay.
- Dock из трёх кнопок внизу, mic-иконка меняется по `state`.
- `ConversationPeek` не рендерится, когда `transcript`/`reply` пустые.

- [ ] **Step 4: Commit**

```bash
cd mobile
git add src/screens/HomeScreen.tsx
git commit -m "Rebuild HomeScreen with AMOLED layout and OlgaLayeredAvatar"
```

---

## Task 6: Device-link и logout в `ProfileScreen`

**Files:**
- Modify: `mobile/src/screens/ProfileScreen.tsx`

**Interfaces:**
- Consumes: `api.createDeviceLink()` (существующий, без изменений); `logout` из `useAuth()` (существующий).
- Не меняет `ProfileScreenProps` — `mode`/`onDone` остаются как есть.

Секция показывается **только при `mode === 'settings'`** — при `mode === 'onboarding'` (только что зарегистрировавшийся пользователь) device-link/logout неуместны, см. существующий комментарий в файле про то, что `onboarding` уже отдельно управляет своим "готово"-путём.

- [ ] **Step 1: Добавить состояние и обработчик device-link**

Modify `mobile/src/screens/ProfileScreen.tsx` — добавить в деструктуринг `useAuth()`:

```tsx
const { displayName, assistantName, refreshProfile, logout } = useAuth();
```

Добавить новые `useState` рядом с существующими (после `voicesError`):

```tsx
const [deviceCode, setDeviceCode] = useState<string | null>(null);
const [isGeneratingCode, setIsGeneratingCode] = useState(false);
const [linkError, setLinkError] = useState<string | null>(null);
```

Добавить обработчик рядом с `handleSaveNames`/`handleSelectVoice`:

```tsx
async function handleShowDeviceCode() {
  setLinkError(null);
  setIsGeneratingCode(true);
  try {
    const { code } = await api.createDeviceLink();
    setDeviceCode(code);
  } catch (err) {
    setLinkError(err instanceof Error ? err.message : String(err));
  } finally {
    setIsGeneratingCode(false);
  }
}
```

- [ ] **Step 2: Добавить секцию в JSX**

Modify `mobile/src/screens/ProfileScreen.tsx` — вставить новый блок между секцией голосов и `doneLink`, обёрнутый в `mode === 'settings'`:

```tsx
      {mode === 'settings' && (
        <>
          <View style={styles.divider} />

          <Text style={styles.label}>Новое устройство</Text>
          {deviceCode ? (
            <View style={styles.codeBox}>
              <Text style={styles.hint}>Код действителен 10 минут:</Text>
              <Text style={styles.codeValue}>{deviceCode}</Text>
              <Text style={styles.hint}>
                Введите его на новом устройстве в разделе «Есть код с другого устройства?»
              </Text>
            </View>
          ) : (
            <Pressable
              style={[styles.button, isGeneratingCode && styles.buttonDisabled]}
              onPress={handleShowDeviceCode}
              disabled={isGeneratingCode}
            >
              {isGeneratingCode ? (
                <ActivityIndicator color="#fff" />
              ) : (
                <Text style={styles.buttonText}>Показать код для нового устройства</Text>
              )}
            </Pressable>
          )}
          {linkError && <Text style={styles.error}>{linkError}</Text>}

          <View style={styles.divider} />

          <Pressable style={styles.logoutButton} onPress={logout}>
            <Text style={styles.logoutText}>Выйти</Text>
          </Pressable>
        </>
      )}
```

- [ ] **Step 3: Добавить стили**

Modify `mobile/src/screens/ProfileScreen.tsx` — добавить в `StyleSheet.create({...})`:

```ts
  codeBox: {
    alignItems: 'center',
    marginBottom: 16,
    padding: 16,
    borderRadius: 12,
    backgroundColor: '#f0f4fa',
  },
  codeValue: {
    fontSize: 36,
    fontWeight: '700',
    letterSpacing: 8,
    color: '#4a6fa5',
    marginVertical: 6,
  },
  logoutButton: {
    borderWidth: 1,
    borderColor: '#c0392b',
    borderRadius: 10,
    paddingVertical: 12,
    alignItems: 'center',
  },
  logoutText: {
    color: '#c0392b',
    fontSize: 16,
    fontWeight: '600',
  },
```

- [ ] **Step 4: Проверить компиляцию**

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок.

- [ ] **Step 5: Убрать теперь неиспользуемые `user`/`displayName`/`logout` импорты из `HomeScreen.tsx`, если остались**

Перепроверить `mobile/src/screens/HomeScreen.tsx` (Task 5) — `useAuth()` там теперь деструктурирует только `assistantName`. Если IDE/`tsc` показывает unused-import предупреждение для чего-то ещё — убрать.

Run: `cd mobile && npx tsc --noEmit`
Expected: без ошибок и без unused-предупреждений, влияющих на сборку.

- [ ] **Step 6: Визуальная проверка в превью**

```
preview_start
```

Открыть Настройки (`preview_click` по кнопке профиля или через прямой рендер `ProfileScreen mode="settings"`), убедиться:
- Секция «Новое устройство» и кнопка «Выйти» видны в settings-режиме.
- В onboarding-режиме (`mode="onboarding"`, если доступно для проверки — например, временно вызвав компонент с этим пропом в превью) секция НЕ рендерится.
- Кнопка генерации кода работает (или хотя бы не падает — реальный API может быть недоступен в веб-превью, как уже бывало в этой сессии с CORS; если так — зафиксировать это как известное ограничение проверки, не блокер).

- [ ] **Step 7: Commit**

```bash
cd mobile
git add src/screens/ProfileScreen.tsx
git commit -m "Move device-link and logout into ProfileScreen settings section"
```

---

## Финальная проверка перед PR

- [ ] `cd mobile && npx tsc --noEmit` — чистый на финальном состоянии всех 6 тасков вместе.
- [ ] Полный проход по всем состояниям в веб-превью ещё раз, теперь на финальной версии.
- [ ] `git log --oneline` показывает 6 отдельных коммитов (по одному на таск) — при необходимости оформить как один PR со всеми шестью коммитами (matches this session's practice: one feature = one branch/PR, но внутри — частые коммиты по TDD-шагам).
- [ ] Собрать APK через WSL (`docs/react-native/BUILD-WSL.md`), передать на физический тест: чёрный фон без белых артефактов, все состояния различимы, тап/long-press работают, sleeping не мешает голосовому циклу, fallback на `AvatarFace` (искусственно сломать один путь к ассету, проверить, что не крашится).
- [ ] Обновить `docs/react-native/BACKLOG.md` отдельным docs-PR после мержа, как заведено в этой сессии.
