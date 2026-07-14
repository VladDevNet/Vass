import { Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { Camera, FileText, Images, SwitchCamera, X } from 'lucide-react-native';
import type { ComponentType } from 'react';
import type { LucideProps } from 'lucide-react-native';
import type { VisualSource } from '../visual/types';
import { amoled } from '../theme/amoled';

interface VisualSourceSheetProps {
  visible: boolean;
  onClose: () => void;
  onSelect: (source: VisualSource) => void;
}

const actions: Array<{ source: VisualSource; label: string; Icon: ComponentType<LucideProps> }> = [
  { source: 'camera', label: 'Сфотографировать', Icon: Camera },
  { source: 'selfie', label: 'Селфи', Icon: SwitchCamera },
  { source: 'gallery', label: 'Из галереи', Icon: Images },
  { source: 'file', label: 'Файл, документ или скриншот', Icon: FileText },
];

export function VisualSourceSheet({ visible, onClose, onSelect }: VisualSourceSheetProps) {
  return (
    <Modal transparent visible={visible} animationType="fade" onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <Pressable style={StyleSheet.absoluteFill} onPress={onClose} accessibilityLabel="Закрыть выбор вложения" />
        <View style={styles.sheet}>
          <View style={styles.header}>
            <Text style={styles.title}>Добавить вложение</Text>
            <Pressable style={styles.closeButton} onPress={onClose} accessibilityLabel="Закрыть">
              <X size={20} color={amoled.textSecondary} />
            </Pressable>
          </View>
          {actions.map(({ source, label, Icon }) => (
            <Pressable
              key={source}
              style={styles.action}
              onPress={() => onSelect(source)}
              accessibilityRole="button"
              accessibilityLabel={label}
            >
              <Icon size={22} color={amoled.textPrimary} />
              <Text style={styles.actionText}>{label}</Text>
            </Pressable>
          ))}
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    justifyContent: 'flex-end',
    backgroundColor: 'rgba(0,0,0,0.72)',
  },
  sheet: {
    backgroundColor: '#111827',
    borderTopWidth: 1,
    borderColor: amoled.glassBorder,
    paddingHorizontal: 20,
    paddingTop: 14,
    paddingBottom: 28,
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: 8,
  },
  title: {
    color: amoled.textPrimary,
    fontSize: 17,
    fontWeight: '700',
  },
  closeButton: {
    width: 36,
    height: 36,
    alignItems: 'center',
    justifyContent: 'center',
  },
  action: {
    minHeight: 54,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 14,
  },
  actionText: {
    color: amoled.textPrimary,
    fontSize: 16,
  },
});
