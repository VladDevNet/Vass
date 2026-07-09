# Task 6 Report: Device-link и logout в `ProfileScreen`

**Status:** DONE
**Commit:** b8b8e21 — Move device-link and logout into ProfileScreen settings section

## Process Note

The implementer subagent originally dispatched for this task made the code
edits (Steps 1-3) and had begun Step 6 (visual verification, via a temporary
`App.tsx` auth-bypass rendering `<ProfileScreen mode="settings" .../>`
directly) when the session was interrupted by a hang/restart. No report file
had been written yet, and no agent ID was recoverable to resume it. The
controller picked up directly from the uncommitted working-tree state rather
than re-dispatching from scratch, since the existing edits were byte-for-byte
consistent with the brief (verified below) and re-doing them would have been
pure waste.

## Implementation Summary

### Step 1: State and handler ✓
`mobile/src/screens/ProfileScreen.tsx`:
- `useAuth()` destructure extended with `logout`.
- Added `deviceCode`, `isGeneratingCode`, `linkError` state, placed after
  `voicesError` as specified.
- Added `handleShowDeviceCode()` — calls `api.createDeviceLink()`, sets
  `deviceCode` on success, `linkError` on failure, `isGeneratingCode` toggled
  in `finally`.

### Step 2: JSX section ✓
New block inserted between the voices list and `doneLink`, gated on
`mode === 'settings'`: "Новое устройство" label, conditional code-box vs.
generate button, error text, divider, red-outlined "Выйти" logout button.

### Step 3: Styles ✓
`codeBox`, `codeValue`, `logoutButton`, `logoutText` added to the
`StyleSheet.create({...})` block, matching the brief's values exactly.

### Step 4/5: Compile check ✓
`cd mobile && npx tsc --noEmit` — exit code 0, no errors, no unused-import
warnings. `HomeScreen.tsx` (Task 5) already destructures only `assistantName`
from `useAuth()`, so there was nothing left to clean up there.

### Step 6: Live preview verification — done directly by controller

Since the interrupted implementer had already added the `App.tsx` bypass
line needed for this (Task 5's HomeScreen sits in front of ProfileScreen in
the normal auth-gated flow, so reaching `mode="settings"` requires bypassing
`Root()`'s gate the same way Task 5's manual check did), the controller
reused it rather than re-inventing an equivalent hack:

1. `preview_start` (name `mobile-web`).
2. With the bypass rendering `<ProfileScreen mode="settings" onDone={() => {}} />`:
   read `document.body.innerText` — confirmed "Новое устройство",
   "Показать код для нового устройства", and "Выйти" all present alongside
   the pre-existing name/voice sections. Screenshot confirmed layout: blue
   generate-code button, divider, red-outlined logout button with red text,
   "Назад" link at the bottom — matches the brief's styles.
3. Clicked "Показать код для нового устройства" (via `preview_eval`
   dispatching a real `.click()` on the matched element, since the button
   has no stable CSS selector). Result: red "Unauthorized" text appeared
   below the button (the `linkError` path), the button returned to its
   normal (non-spinner) state — confirms the try/catch/finally in
   `handleShowDeviceCode` works and does not crash. This is the exact,
   pre-acknowledged limitation from the brief ("реальный API может быть
   недоступен в веб-превью... зафиксировать как известное ограничение
   проверки, не блокер") — the mobile web preview has no valid session
   against the production backend, so a 401 is the expected outcome here,
   not a defect.
4. Edited the `App.tsx` bypass to `mode="onboarding"`, reloaded
   (`window.location.reload()` via `preview_eval`), read
   `document.body.innerText` again: confirmed the onboarding title
   ("Давайте познакомимся") renders and the entire device-link/logout
   section is absent — no "Новое устройство", no "Выйти" anywhere in the
   text dump. The bottom link correctly reads "Пропустить" (the existing,
   unmodified onboarding-vs-settings ternary for that link).
5. Reverted the `App.tsx` bypass entirely (removed the extra `return`
   line), confirmed via `git diff --stat mobile/App.tsx` — empty output,
   byte-identical to the committed version.
6. Re-ran `npx tsc --noEmit` after the revert — exit code 0.
7. `preview_stop`.

Both claims in the spec ("visible only in settings mode", "does not crash
when the API call fails") were observed directly, not inferred from reading
the conditional logic.

### Step 7: Commit ✓
`git add mobile/src/screens/ProfileScreen.tsx && git commit` — commit
`b8b8e21`. `App.tsx` was NOT staged (its diff was empty by this point — the
bypass had already been fully reverted).

## Self-Review

### Completeness
- All three steps' code present and matching the brief verbatim.
- Section correctly scoped to `mode === 'settings'` only.
- No new props added to `ProfileScreenProps` (per Interfaces constraint).

### Quality
- Reuses existing `styles.divider`, `styles.hint`, `styles.button`,
  `styles.buttonDisabled`, `styles.buttonText`, `styles.error` — no
  duplicated style definitions.
- `handleShowDeviceCode` follows the exact same
  try/set-error/finally-reset-loading shape as the pre-existing
  `handleSaveNames` in the same file — consistent with established
  conventions, not a new pattern.

### Discipline
- No changes outside `ProfileScreen.tsx` survived to the commit.
- No retry loop or extra polish added beyond what the brief specified
  (YAGNI).

### Testing
- `npx tsc --noEmit`: exit code 0 (checked twice — once with the temporary
  bypass present, once after full revert).
- Live preview: both `settings` and `onboarding` modes observed directly,
  including the button's real click behavior and graceful error handling.

## Files Modified
- `mobile/src/screens/ProfileScreen.tsx` (+80/-1)

## Fix: navigationDisabled split + Record<VoiceState> typing (final-review finding)

**Status:** DONE

### Changes Made

#### Finding 1: Split navigationDisabled gate to prevent Settings lockout on session-load error

**File:** `mobile/src/screens/HomeScreen.tsx`

- **Line 8 (new import):** Added `import type { VoiceState } from '../hooks/useVoiceChat';` to support Finding 2's type annotation.
- **Lines 20, 28, 36 (type annotations):** Changed `HEADLINE`, `SUBTITLE`, and `PRESENCE_LABEL` from `Record<string, string>` to `Record<VoiceState, string>` for compile-time exhaustiveness checking.
- **Lines 90-91 (disabled gates):** Replaced single `navigationDisabled` with two separate gates:
  - `settingsDisabled = state !== 'idle'` (only blocks during voice activity, not on session error)
  - `historyDisabled = state !== 'idle' || !sessionId` (blocks during voice activity OR when session missing)
- **Line 110 (profile button):** Changed `disabled={navigationDisabled}` to `disabled={settingsDisabled}` so Settings remains accessible even if session fetch fails.
- **Lines 148-149 (VoiceControlDock props):** Changed `navigationDisabled={navigationDisabled}` to two separate props: `settingsDisabled={settingsDisabled}` and `historyDisabled={historyDisabled}`.

**File:** `mobile/src/components/VoiceControlDock.tsx`

- **Lines 11-12 (interface):** Replaced `navigationDisabled: boolean;` with `settingsDisabled: boolean;` and `historyDisabled: boolean;`.
- **Lines 27-28 (destructure):** Updated function parameter destructuring to match the new interface.
- **Lines 33, 35 (settings button):** Changed `navigationDisabled` references to `settingsDisabled` in both the style array and `disabled` prop.
- **Lines 51, 53 (history button):** Changed `navigationDisabled` references to `historyDisabled` in both the style array and `disabled` prop.

### Verification

#### TypeScript compilation
Command: `cd /d/Repos/Vass/mobile && npx tsc --noEmit`

Exit code: 0, no output

#### Live preview verification
1. Temporary App.tsx bypass added: `return <HomeScreen />;` as the first line of `Root()` function body.
2. Preview started with `preview_start` (name `mobile-web`).
3. Page reloaded via `preview_eval`.
4. Observed `document.body.innerText`: "Unauthorized...Слушаю вас…..." (confirming state is 'idle', session fetch failed with error).
5. Queried button opacity and pointer-events state via `preview_eval`:
   - Profile button (👤): opacity 1, pointerEvents "auto" → NOT dimmed ✓
   - Settings button (⚙️): opacity 1, pointerEvents "auto" → NOT dimmed ✓ (this is the fix)
   - History button (🕐): opacity 0.4, pointerEvents "none" → DIMMED ✓
6. Reverted App.tsx bypass, confirmed via `git diff --stat mobile/App.tsx` → empty output.
7. Stopped preview server.

### Result

**Finding 1 fixed:** Settings button is now reachable even when session fetch fails (network error, timeout, etc.), unblocking the user's only path to logout. History button correctly remains disabled when session is missing.

**Finding 2 fixed:** State-keyed label maps now have `Record<VoiceState, string>` type annotation, ensuring future VoiceState additions fail to compile here instead of silently rendering blank text.
