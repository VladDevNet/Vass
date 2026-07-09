import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface VoiceControlDockProps {
  state: VoiceState;
  onSettingsPress: () => void;
  onHistoryPress: () => void;
  onMicPress: () => void;
  onMicLongPress: () => void;
  settingsDisabled: boolean;
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
  settingsDisabled,
  historyDisabled,
}: VoiceControlDockProps) {
  return (
    <View style={styles.dock}>
      <Pressable
        style={[styles.sideButton, settingsDisabled && styles.sideButtonDisabled]}
        onPress={onSettingsPress}
        disabled={settingsDisabled}
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
