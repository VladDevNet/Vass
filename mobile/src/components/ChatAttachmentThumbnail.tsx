import { useState } from 'react';
import { Image, StyleSheet, Text, View } from 'react-native';
import { FileText, ImageOff } from 'lucide-react-native';
import { api, type ChatAttachment } from '../api/client';

interface ChatAttachmentThumbnailProps {
  attachment: ChatAttachment;
}

export function ChatAttachmentThumbnail({ attachment }: ChatAttachmentThumbnailProps) {
  const [failed, setFailed] = useState(false);
  const isImage = attachment.kind === 'image' && attachment.mimeType.startsWith('image/');
  if (!isImage) {
    return (
      <View style={styles.document} accessibilityLabel={`Прикрепленный файл ${attachment.originalName ?? ''}`}>
        <FileText size={20} color="#475569" />
        <View style={styles.documentCopy}>
          <Text style={styles.documentName} numberOfLines={2}>{attachment.originalName ?? 'Вложение'}</Text>
          <Text style={styles.documentType} numberOfLines={1}>{attachment.mimeType}</Text>
        </View>
      </View>
    );
  }
  if (failed) {
    return (
      <View style={styles.placeholder} accessibilityLabel="Прикрепленное изображение недоступно">
        <ImageOff size={20} color="#64748B" />
      </View>
    );
  }

  return (
    <Image
      source={api.visualAssetContentSource(attachment.id)}
      style={styles.image}
      resizeMode="cover"
      onError={() => setFailed(true)}
      accessibilityLabel="Прикрепленное изображение"
    />
  );
}

const styles = StyleSheet.create({
  image: {
    width: 156,
    height: 112,
    borderRadius: 6,
    marginBottom: 8,
    backgroundColor: '#e2e8f0',
  },
  placeholder: {
    width: 156,
    height: 112,
    borderRadius: 6,
    marginBottom: 8,
    backgroundColor: '#e2e8f0',
    alignItems: 'center',
    justifyContent: 'center',
  },
  document: {
    width: 220,
    minHeight: 58,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    padding: 10,
    borderRadius: 6,
    marginBottom: 8,
    backgroundColor: '#e2e8f0',
  },
  documentCopy: {
    flex: 1,
  },
  documentName: {
    color: '#0f172a',
    fontSize: 13,
    fontWeight: '700',
  },
  documentType: {
    color: '#64748b',
    fontSize: 11,
    marginTop: 2,
  },
});
