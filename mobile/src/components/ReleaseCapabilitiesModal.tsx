import { Modal, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Sparkles } from 'lucide-react-native';
import type { CapabilityDiscoveryItem } from '../api/client';
import { amoled } from '../theme/amoled';

interface ReleaseCapabilitiesModalProps {
  items: CapabilityDiscoveryItem[];
  onDone: () => void;
}

export function ReleaseCapabilitiesModal({ items, onDone }: ReleaseCapabilitiesModalProps) {
  return (
    <Modal transparent visible={items.length > 0} animationType="fade" statusBarTranslucent onRequestClose={onDone}>
      <View style={styles.backdrop}>
        <View style={styles.dialog} accessibilityViewIsModal>
          <View style={styles.iconWrap}>
            <Sparkles size={27} color="#93C5FD" strokeWidth={2.1} />
          </View>
          <Text style={styles.title}>Можно попробовать новое</Text>
          <Text style={styles.description}>
            В Vass есть возможности, которыми вы ещё не пользовались. Они останутся доступными, а это знакомство больше не будет повторяться.
          </Text>
          <ScrollView style={styles.list} contentContainerStyle={styles.listContent} showsVerticalScrollIndicator={false}>
            {items.map((item) => (
              <View key={item.id} style={styles.item}>
                <Text style={styles.itemTitle}>{item.title}</Text>
                <Text style={styles.itemDescription}>{item.description}</Text>
                {item.examples[0] ? <Text style={styles.example}>Например: «{item.examples[0]}»</Text> : null}
              </View>
            ))}
          </ScrollView>
          <Pressable style={styles.primaryButton} onPress={onDone} accessibilityRole="button" accessibilityLabel="Понятно">
            <Text style={styles.primaryButtonText}>Понятно</Text>
          </Pressable>
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
    maxHeight: '82%',
    backgroundColor: '#111827',
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 8,
    paddingHorizontal: 24,
    paddingTop: 24,
    paddingBottom: 20,
  },
  iconWrap: {
    width: 52,
    height: 52,
    borderRadius: 26,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(59,130,246,0.16)',
    marginBottom: 16,
  },
  title: {
    color: amoled.textPrimary,
    fontSize: 21,
    fontWeight: '700',
  },
  description: {
    color: amoled.textSecondary,
    fontSize: 15,
    lineHeight: 22,
    marginTop: 12,
  },
  list: {
    marginTop: 16,
  },
  listContent: {
    gap: 10,
  },
  item: {
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    backgroundColor: '#0B1220',
    borderRadius: 7,
    padding: 14,
  },
  itemTitle: {
    color: amoled.textPrimary,
    fontSize: 16,
    fontWeight: '700',
  },
  itemDescription: {
    color: amoled.textSecondary,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 5,
  },
  example: {
    color: '#93C5FD',
    fontSize: 13,
    lineHeight: 19,
    marginTop: 9,
  },
  primaryButton: {
    minHeight: 52,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#BFDBFE',
    borderRadius: 7,
    marginTop: 18,
    paddingHorizontal: 16,
  },
  primaryButtonText: {
    color: '#07111F',
    fontSize: 16,
    fontWeight: '700',
  },
});
