import { Pressable, StyleSheet } from 'react-native';
import { Paperclip } from 'lucide-react-native';
import type { VisualInputStatus } from '../visual/types';
import { amoled } from '../theme/amoled';

interface VisualInputButtonProps {
  disabled: boolean;
  status: VisualInputStatus;
  onPress: () => void;
}

export function VisualInputButton({ disabled, status, onPress }: VisualInputButtonProps) {
  const busy = status === 'picking' || status === 'uploading';
  return (
    <Pressable
      style={[styles.button, (disabled || busy) && styles.disabled]}
      onPress={onPress}
      disabled={disabled || busy}
      accessibilityRole="button"
      accessibilityLabel="Добавить вложение"
      accessibilityHint="Сделать фотографию или выбрать файл"
    >
      <Paperclip size={22} color={amoled.textPrimary} strokeWidth={2} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  button: {
    width: 46,
    height: 46,
    borderRadius: 23,
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    alignItems: 'center',
    justifyContent: 'center',
  },
  disabled: {
    opacity: 0.45,
  },
});
