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
