import { StyleSheet, Text, View } from 'react-native';
import type { VoiceState } from '../hooks/useVoiceChat';
import { amoled } from '../theme/amoled';

interface ConversationPeekProps {
  transcript: string;
  reply: string;
  state: VoiceState;
}

// Одна тёмная стеклянная плашка вместо двух подписанных bubble. During a
// mediated screen capture `reply` carries explicit local progress while the
// runtime remains in thinking, so that otherwise invisible wait is shown.
export function ConversationPeek({ transcript, reply, state }: ConversationPeekProps) {
  const text = state === 'recording'
    ? transcript
    : state === 'thinking' || state === 'speaking'
      ? reply
      : '';
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
