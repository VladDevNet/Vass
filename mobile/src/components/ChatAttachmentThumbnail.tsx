import { useState } from 'react';
import { Image, StyleSheet, View } from 'react-native';
import { ImageOff } from 'lucide-react-native';
import { api, type ChatAttachment } from '../api/client';

interface ChatAttachmentThumbnailProps {
  attachment: ChatAttachment;
}

export function ChatAttachmentThumbnail({ attachment }: ChatAttachmentThumbnailProps) {
  const [failed, setFailed] = useState(false);
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
});
