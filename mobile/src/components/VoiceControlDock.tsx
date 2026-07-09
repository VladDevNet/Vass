import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface VoiceControlDockProps {
  state: VoiceState;
  onSettingsPress: () => void;
  onHistoryPress: () => void;
  onMicPress: () => void;
  onMicLongPress: () => void;
  historyDisabled: boolean;
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
  historyDisabled,
}: VoiceControlDockProps) {
  return (
    <View style={styles.dock}>
      {/* Настройки — единственный путь к logout, поэтому НИКОГДА не
          блокируются состоянием разговора (см. HomeScreen.tsx): при
          непрерывно слушающем VAD state может редко задерживаться в
          'idle', так что завязка на него могла делать кнопку недоступной
          большую часть времени — согласуется с репортом с реального
          устройства ("не работает"), хотя точный механизм не трассирован
          логами, только код-инспекция. */}
      <Pressable style={styles.sideButton} onPress={onSettingsPress} accessibilityLabel="Настройки">
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
        style={[styles.sideButton, historyDisabled && styles.sideButtonDisabled]}
        onPress={onHistoryPress}
        disabled={historyDisabled}
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
    width: 64,
    height: 64,
    borderRadius: 32,
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
    fontSize: 30,
  },
  micButton: {
    width: 88,
    height: 88,
    borderRadius: 44,
    backgroundColor: amoled.glassBackgroundStrong,
    borderWidth: 2,
    borderColor: 'rgba(245,158,11,0.6)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  micGlyph: {
    fontSize: 34,
  },
});
