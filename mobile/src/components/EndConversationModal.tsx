import { ActivityIndicator, Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { Power } from 'lucide-react-native';
import { amoled } from '../theme/amoled';

interface EndConversationModalProps {
  visible: boolean;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}

export function EndConversationModal({ visible, busy, onCancel, onConfirm }: EndConversationModalProps) {
  return (
    <Modal transparent visible={visible} animationType="fade" onRequestClose={busy ? () => undefined : onCancel}>
      <View style={styles.backdrop}>
        {!busy && <Pressable style={StyleSheet.absoluteFill} onPress={onCancel} accessibilityLabel="Не завершать разговор" />}
        <View style={styles.dialog} accessibilityViewIsModal>
          <View style={styles.iconWrap}>
            <Power size={28} color="#FCA5A5" strokeWidth={2.25} />
          </View>
          <Text style={styles.title}>Завершить разговор?</Text>
          <Text style={styles.description}>
            Vass остановит запись, ответ, озвучивание и плавающий режим, затем закроет приложение.
          </Text>
          <Text style={styles.note}>История, память и уже созданные напоминания сохранятся.</Text>
          <View style={styles.actions}>
            <Pressable
              style={styles.cancelButton}
              onPress={onCancel}
              disabled={busy}
              accessibilityRole="button"
              accessibilityLabel="Не завершать разговор"
            >
              <Text style={styles.cancelText}>Не сейчас</Text>
            </Pressable>
            <Pressable
              style={[styles.confirmButton, busy && styles.confirmButtonDisabled]}
              onPress={onConfirm}
              disabled={busy}
              accessibilityRole="button"
              accessibilityLabel="Завершить разговор и закрыть приложение"
            >
              {busy ? <ActivityIndicator color="#FFFFFF" /> : <Power size={18} color="#FFFFFF" strokeWidth={2.4} />}
              <Text style={styles.confirmText}>{busy ? 'Завершаем…' : 'Завершить'}</Text>
            </Pressable>
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    justifyContent: 'center',
    paddingHorizontal: 24,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  dialog: {
    backgroundColor: '#111827',
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 8,
    padding: 24,
  },
  iconWrap: {
    width: 54,
    height: 54,
    borderRadius: 27,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(239,68,68,0.14)',
    marginBottom: 18,
  },
  title: {
    color: amoled.textPrimary,
    fontSize: 21,
    fontWeight: '700',
  },
  description: {
    color: amoled.textSecondary,
    fontSize: 16,
    lineHeight: 23,
    marginTop: 13,
  },
  note: {
    color: amoled.textPrimary,
    fontSize: 14,
    lineHeight: 21,
    marginTop: 12,
  },
  actions: {
    flexDirection: 'row',
    gap: 10,
    marginTop: 24,
  },
  cancelButton: {
    flex: 1,
    minHeight: 50,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 7,
  },
  cancelText: {
    color: amoled.textPrimary,
    fontSize: 16,
    fontWeight: '600',
  },
  confirmButton: {
    flex: 1.2,
    minHeight: 50,
    flexDirection: 'row',
    gap: 8,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#DC2626',
    borderRadius: 7,
  },
  confirmButtonDisabled: {
    opacity: 0.65,
  },
  confirmText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
});
