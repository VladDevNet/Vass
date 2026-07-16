import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Alert, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { ArrowLeft, BellOff, CalendarClock, RefreshCw } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { api, type ManagedReminder } from '../api/client';
import { useAuth } from '../context/AuthContext';
import { cancelLocalReminder } from '../reminders/localReminders';
import { amoled } from '../theme/amoled';

interface RemindersScreenProps {
  onDone: () => void;
}

function formatSchedule(reminder: ManagedReminder): string {
  const date = new Date(reminder.dueAtUtc);
  const when = Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat('ru-RU', {
        timeZone: reminder.timeZoneId,
        day: 'numeric',
        month: 'long',
        hour: '2-digit',
        minute: '2-digit',
      }).format(date)
    : reminder.dueAtUtc;
  return reminder.recurrenceRule ? `${when}, повторяется` : when;
}

export function RemindersScreen({ onDone }: RemindersScreenProps) {
  const { user } = useAuth();
  const [items, setItems] = useState<ManagedReminder[]>([]);
  const [loading, setLoading] = useState(true);
  const [cancellingId, setCancellingId] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setItems(await api.getManagedReminders());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить напоминания');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  function cancel(reminder: ManagedReminder) {
    Alert.alert('Отменить напоминание?', reminder.text, [
      { text: 'Нет', style: 'cancel' },
      {
        text: 'Отменить', style: 'destructive', onPress: () => {
          void (async () => {
            setCancellingId(reminder.id);
            setError(null);
            try {
              await api.cancelReminder(reminder.id);
              if (user) await cancelLocalReminder(reminder.id, user.id);
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось отменить напоминание');
            } finally {
              setCancellingId(null);
            }
          })();
        },
      },
    ]);
  }

  return (
    <SafeAreaView style={styles.screen} edges={['top']}>
      <View style={styles.header}>
        <Pressable style={styles.iconButton} onPress={onDone} accessibilityLabel="Назад"><ArrowLeft size={22} color={amoled.textPrimary} /></Pressable>
        <View style={styles.titleBlock}><Text style={styles.title}>Напоминания</Text><Text style={styles.subtitle}>{items.length} активных</Text></View>
        <Pressable style={[styles.iconButton, loading && styles.disabled]} onPress={() => void load()} disabled={loading} accessibilityLabel="Обновить напоминания"><RefreshCw size={19} color={amoled.textSecondary} /></Pressable>
      </View>
      <ScrollView contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={loading} onRefresh={() => void load()} tintColor="#F6D37A" />}>
        <Text style={styles.hint}>Напоминания запускаются локально на телефоне. Здесь можно отменить любое активное расписание.</Text>
        {error && <Text style={styles.error}>{error}</Text>}
        {loading ? <ActivityIndicator color="#F6D37A" style={styles.loading} /> : items.map((item) => (
          <View key={item.id} style={styles.item}>
            <View style={styles.itemHeader}><CalendarClock size={20} color="#F6D37A" /><Text style={styles.itemText}>{item.text}</Text></View>
            <Text style={styles.when}>{formatSchedule(item)}</Text>
            <Pressable style={[styles.cancelButton, cancellingId === item.id && styles.disabled]} onPress={() => cancel(item)} disabled={cancellingId === item.id} accessibilityLabel={`Отменить напоминание ${item.text}`}>
              {cancellingId === item.id ? <ActivityIndicator color="#F87171" /> : <BellOff size={18} color="#F87171" />}
              <Text style={styles.cancelText}>Отменить</Text>
            </Pressable>
          </View>
        ))}
        {!loading && items.length === 0 && <Text style={styles.empty}>Активных напоминаний пока нет</Text>}
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
  hint: { color: amoled.textSecondary, fontSize: 14, lineHeight: 20 },
  error: { color: '#F87171', fontSize: 14 },
  loading: { marginTop: 34 },
  item: { padding: 15, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  itemHeader: { flexDirection: 'row', alignItems: 'flex-start', gap: 10 },
  itemText: { flex: 1, color: amoled.textPrimary, fontSize: 16, fontWeight: '600', lineHeight: 22 },
  when: { marginTop: 10, color: amoled.textSecondary, fontSize: 14 },
  cancelButton: { minHeight: 40, marginTop: 14, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 8, borderRadius: 8, borderWidth: 1, borderColor: '#F87171' },
  cancelText: { color: '#F87171', fontSize: 14, fontWeight: '700' },
  disabled: { opacity: 0.42 },
  empty: { color: amoled.textSecondary, textAlign: 'center', marginTop: 42, fontSize: 15 },
});
