import { Pressable, StyleSheet, Text, View } from 'react-native';
import { Link2, X } from 'lucide-react-native';
import type { PendingSharedText } from '../visual/types';
import { amoled } from '../theme/amoled';

interface PendingSharedTextPreviewProps {
  pending: PendingSharedText | null;
  onRemove: () => void;
}

export function PendingSharedTextPreview({ pending, onRemove }: PendingSharedTextPreviewProps) {
  if (!pending) return null;

  return (
    <View style={styles.root}>
      <View style={styles.icon}>
        <Link2 size={22} color={amoled.textPrimary} />
      </View>
      <View style={styles.copy}>
        <Text style={styles.title}>Ссылка или текст прикреплены</Text>
        <Text style={styles.subtitle} numberOfLines={2}>{pending.content}</Text>
      </View>
      <Pressable style={styles.remove} onPress={onRemove} accessibilityLabel="Удалить прикрепленную ссылку или текст">
        <X size={18} color={amoled.textSecondary} />
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    minHeight: 66,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 8,
  },
  icon: {
    width: 58,
    height: 58,
    borderRadius: 6,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: amoled.glassBackgroundStrong,
  },
  copy: {
    flex: 1,
  },
  title: {
    color: amoled.textPrimary,
    fontSize: 14,
    fontWeight: '700',
  },
  subtitle: {
    color: amoled.textSecondary,
    fontSize: 13,
    marginTop: 3,
  },
  remove: {
    width: 36,
    height: 36,
    alignItems: 'center',
    justifyContent: 'center',
  },
});
