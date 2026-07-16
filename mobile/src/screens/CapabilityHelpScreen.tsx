import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { ArrowLeft, Lightbulb, RefreshCw } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { api, type CapabilityHelpItem } from '../api/client';
import { VassOverlay } from '../../modules/vass-overlay';
import { amoled } from '../theme/amoled';

interface CapabilityHelpScreenProps {
  onDone: () => void;
}

export function CapabilityHelpScreen({ onDone }: CapabilityHelpScreenProps) {
  const [items, setItems] = useState<CapabilityHelpItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const isPhone = Platform.OS === 'android' || Platform.OS === 'ios';
      setItems(await api.getCapabilityHelp({
        supportsReminders: isPhone,
        supportsPeriodicReminders: isPhone,
        supportsExternalActions: isPhone,
        supportsScreenAnalysis: Platform.OS === 'android' && VassOverlay.isAvailable(),
      }));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить возможности');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  return (
    <SafeAreaView style={styles.screen} edges={['top']}>
      <View style={styles.header}>
        <Pressable style={styles.iconButton} onPress={onDone} accessibilityLabel="Назад"><ArrowLeft size={22} color={amoled.textPrimary} /></Pressable>
        <View style={styles.titleBlock}><Text style={styles.title}>Возможности Vass</Text><Text style={styles.subtitle}>Подсказки и примеры фраз</Text></View>
        <Pressable style={[styles.iconButton, loading && styles.disabled]} onPress={() => void load()} disabled={loading} accessibilityLabel="Обновить возможности"><RefreshCw size={19} color={amoled.textSecondary} /></Pressable>
      </View>
      <ScrollView contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={loading} onRefresh={() => void load()} tintColor="#F6D37A" />}>
        {error && <Text style={styles.error}>{error}</Text>}
        {loading ? <ActivityIndicator color="#F6D37A" style={styles.loading} /> : items.map((item) => (
          <View key={item.id} style={styles.item}>
            <View style={styles.itemTitle}><Lightbulb size={19} color="#F6D37A" /><Text style={styles.itemTitleText}>{item.title}</Text></View>
            <Text style={styles.description}>{item.description}</Text>
            <View style={styles.examples}>{item.examples.map((example) => <Text key={example} style={styles.example}>{example}</Text>)}</View>
            <Text style={styles.interfaceHint}>{item.interfaceHint}</Text>
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: amoled.background },
  header: { minHeight: 64, paddingHorizontal: 16, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  iconButton: { width: 40, height: 40, borderRadius: 20, alignItems: 'center', justifyContent: 'center', backgroundColor: amoled.glassBackground },
  titleBlock: { flex: 1, marginLeft: 12 },
  title: { color: amoled.textPrimary, fontSize: 20, fontWeight: '700' },
  subtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 2 },
  content: { padding: 20, gap: 12 },
  loading: { marginTop: 34 },
  error: { color: '#F87171', fontSize: 14 },
  item: { padding: 15, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  itemTitle: { flexDirection: 'row', alignItems: 'center', gap: 9 },
  itemTitleText: { color: amoled.textPrimary, fontSize: 17, fontWeight: '700' },
  description: { marginTop: 10, color: amoled.textSecondary, fontSize: 14, lineHeight: 20 },
  examples: { marginTop: 12, gap: 6 },
  example: { color: '#F6D37A', fontSize: 14, lineHeight: 20 },
  interfaceHint: { marginTop: 12, color: amoled.textPrimary, fontSize: 13, lineHeight: 19 },
  disabled: { opacity: 0.42 },
});
