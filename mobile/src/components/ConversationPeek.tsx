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
