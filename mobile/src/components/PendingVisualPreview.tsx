import { ActivityIndicator, Image, Pressable, StyleSheet, Text, View } from 'react-native';
import { FileText, X } from 'lucide-react-native';
import type { PendingVisualInput, VisualInputStatus } from '../visual/types';
import { amoled } from '../theme/amoled';

interface PendingVisualPreviewProps {
  pending: PendingVisualInput | null;
  uploadingUri: string | null;
  status: VisualInputStatus;
  error: string | null;
  onRemove: () => void;
}

export function PendingVisualPreview({ pending, uploadingUri, status, error, onRemove }: PendingVisualPreviewProps) {
  const uri = pending?.localUri ?? uploadingUri;
  if (!uri && !error) return null;
  const uploading = status === 'uploading';
  const isImage = pending?.mimeType?.startsWith('image/') ?? false;
  const title = uploading
    ? 'Загружаю вложение'
    : pending
      ? 'Вложение прикреплено'
      : 'Не удалось добавить вложение';
  const subtitle = error ?? (pending?.originalName || (pending ? 'Скажите, что сделать' : 'Попробуйте выбрать другой файл'));

  return (
    <View style={styles.root}>
      {uri && isImage ? (
        <Image source={{ uri }} style={styles.thumbnail} resizeMode="cover" />
      ) : (
        <View style={[styles.thumbnail, styles.fileThumbnail]}>
          <FileText size={24} color={amoled.textSecondary} />
        </View>
      )}
      <View style={styles.copy}>
        <Text style={styles.title}>{title}</Text>
        <Text style={styles.subtitle} numberOfLines={2}>{subtitle}</Text>
      </View>
      {uploading ? (
        <ActivityIndicator color={amoled.textPrimary} />
      ) : pending ? (
        <Pressable style={styles.remove} onPress={onRemove} accessibilityLabel="Удалить прикрепленное вложение">
          <X size={18} color={amoled.textSecondary} />
        </Pressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    minHeight: 74,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 8,
  },
  thumbnail: {
    width: 58,
    height: 58,
    borderRadius: 6,
    backgroundColor: amoled.glassBackgroundStrong,
  },
  fileThumbnail: {
    alignItems: 'center',
    justifyContent: 'center',
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
