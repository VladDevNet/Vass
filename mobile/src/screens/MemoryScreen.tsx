import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  AppState,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { ArrowLeft, Check, Pencil, Plus, RefreshCw, Search, Trash2 } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { api, type MemoryItem, type MemoryStatus } from '../api/client';
import { amoled } from '../theme/amoled';

interface MemoryScreenProps {
  onDone: () => void;
}

function operationId(): string | undefined {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : undefined;
}

export function MemoryScreen({ onDone }: MemoryScreenProps) {
  const [status, setStatus] = useState<MemoryStatus | null>(null);
  const [items, setItems] = useState<MemoryItem[]>([]);
  const [query, setQuery] = useState('');
  const [newText, setNewText] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const [editText, setEditText] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [nextStatus, nextItems] = await Promise.all([api.getMemoryStatus(), api.getMemoryItems()]);
      setStatus(nextStatus);
      setItems(nextItems);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить память');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (nextState) => {
      if (nextState === 'active') void load();
    });
    return () => subscription.remove();
  }, [load]);

  const visibleItems = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return needle ? items.filter((item) => item.text.toLocaleLowerCase().includes(needle)) : items;
  }, [items, query]);

  async function remember() {
    const text = newText.trim();
    if (!text) return;
    setSaving(true);
    setError(null);
    try {
      const receipt = await api.remember(text, operationId());
      if (receipt.code !== 'remembered' && receipt.code !== 'already_known') {
        setError('Запись не была сохранена');
        return;
      }
      setNewText('');
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить запись');
    } finally {
      setSaving(false);
    }
  }

  async function saveCorrection(id: string) {
    const text = editText.trim();
    if (!text) return;
    setSaving(true);
    setError(null);
    try {
      const receipt = await api.correctMemory(id, text, operationId());
      if (receipt.code !== 'corrected') {
        setError('Изменение не было сохранено');
        return;
      }
      setEditing(null);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось изменить запись');
    } finally {
      setSaving(false);
    }
  }

  function forget(item: MemoryItem) {
    Alert.alert('Удалить запись?', item.text, [
      { text: 'Отмена', style: 'cancel' },
      {
        text: 'Удалить', style: 'destructive', onPress: () => {
          void (async () => {
            setSaving(true);
            setError(null);
            try {
              const receipt = await api.forgetMemory(item.id, operationId());
              if (receipt.code !== 'forgotten') throw new Error('Запись не была удалена');
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось удалить запись');
            } finally {
              setSaving(false);
            }
          })();
        },
      },
    ]);
  }

  function clearAll() {
    Alert.alert('Очистить всю память?', 'Это удалит все сохраненные записи. Действие нельзя отменить.', [
      { text: 'Отмена', style: 'cancel' },
      {
        text: 'Очистить', style: 'destructive', onPress: () => {
          void (async () => {
            setSaving(true);
            setError(null);
            try {
              const prepared = await api.prepareMemoryClear();
              if (!prepared.confirmationToken) throw new Error('Подтверждение очистки не получено');
              const receipt = await api.clearMemory(prepared.operationId, prepared.confirmationToken);
              if (receipt.code !== 'cleared') throw new Error('Память не была очищена');
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось очистить память');
            } finally {
              setSaving(false);
            }
          })();
        },
      },
    ]);
  }

  return (
    <SafeAreaView style={styles.screen} edges={['top']}>
      <View style={styles.header}>
        <Pressable style={styles.iconButton} onPress={onDone} accessibilityLabel="Назад">
          <ArrowLeft size={22} color={amoled.textPrimary} />
        </Pressable>
        <View style={styles.titleBlock}>
          <Text style={styles.title}>Память</Text>
          <Text style={styles.subtitle}>
            {status?.availability === 'disabled' ? 'Недоступна' : `${status?.activeCount ?? 0} сохранено`}
          </Text>
        </View>
        <View style={styles.toolbarActions}>
          <Pressable style={[styles.iconButton, loading && styles.disabled]} onPress={() => void load()} disabled={loading} accessibilityLabel="Обновить список памяти">
            <RefreshCw size={19} color={amoled.textSecondary} />
          </Pressable>
          <Pressable style={[styles.iconButton, items.length === 0 && styles.disabled]} onPress={clearAll} disabled={items.length === 0 || saving} accessibilityLabel="Очистить память">
            <Trash2 size={20} color={items.length === 0 ? amoled.textSecondary : '#F87171'} />
          </Pressable>
        </View>
      </View>

      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={loading} onRefresh={() => void load()} tintColor="#F6D37A" />}
      >
        <Text style={styles.hint}>Сюда попадают только подтвержденные записи. Их можно исправить или удалить в любой момент.</Text>
        <View style={styles.composer}>
          <TextInput style={styles.composerInput} value={newText} onChangeText={setNewText} placeholder="Добавить факт" placeholderTextColor={amoled.textSecondary} multiline />
          <Pressable style={[styles.roundAction, (!newText.trim() || saving) && styles.disabled]} onPress={() => void remember()} disabled={!newText.trim() || saving} accessibilityLabel="Сохранить в память">
            {saving ? <ActivityIndicator size="small" color="#071018" /> : <Plus size={21} color="#071018" />}
          </Pressable>
        </View>
        <View style={styles.searchRow}>
          <Search size={18} color={amoled.textSecondary} />
          <TextInput style={styles.searchInput} value={query} onChangeText={setQuery} placeholder="Найти среди записей" placeholderTextColor={amoled.textSecondary} />
        </View>
        {error && <Text style={styles.error}>{error}</Text>}
        {loading ? <ActivityIndicator color="#F6D37A" style={styles.loading} /> : visibleItems.map((item) => (
          <View key={item.id} style={styles.item}>
            {editing === item.id ? (
              <>
                <TextInput style={styles.editInput} value={editText} onChangeText={setEditText} multiline autoFocus />
                <View style={styles.itemActions}>
                  <Pressable style={styles.actionButton} onPress={() => setEditing(null)}><Text style={styles.actionText}>Отмена</Text></Pressable>
                  <Pressable style={styles.confirmButton} onPress={() => void saveCorrection(item.id)} disabled={saving} accessibilityLabel="Сохранить изменение"><Check size={18} color="#071018" /></Pressable>
                </View>
              </>
            ) : (
              <>
                <Text style={styles.itemText}>{item.text}</Text>
                <View style={styles.itemFooter}>
                  <Text style={styles.date}>{new Date(item.updatedAt).toLocaleDateString('ru-RU')}</Text>
                  <View style={styles.itemActions}>
                    <Pressable style={styles.actionButton} onPress={() => { setEditing(item.id); setEditText(item.text); }} accessibilityLabel="Изменить запись"><Pencil size={17} color={amoled.textSecondary} /></Pressable>
                    <Pressable style={styles.actionButton} onPress={() => forget(item)} accessibilityLabel="Удалить запись"><Trash2 size={17} color="#F87171" /></Pressable>
                  </View>
                </View>
              </>
            )}
          </View>
        ))}
        {!loading && visibleItems.length === 0 && <Text style={styles.empty}>{query ? 'Ничего не найдено' : 'Здесь пока нет сохраненных записей'}</Text>}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: amoled.background },
  header: { minHeight: 64, paddingHorizontal: 16, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  titleBlock: { flex: 1, marginLeft: 12 },
  toolbarActions: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  title: { color: amoled.textPrimary, fontSize: 20, fontWeight: '700' },
  subtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 2 },
  iconButton: { width: 40, height: 40, alignItems: 'center', justifyContent: 'center', borderRadius: 20, backgroundColor: amoled.glassBackground },
  content: { padding: 20, gap: 12 },
  hint: { color: amoled.textSecondary, fontSize: 14, lineHeight: 20, marginBottom: 4 },
  composer: { minHeight: 54, flexDirection: 'row', alignItems: 'center', padding: 8, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  composerInput: { flex: 1, color: amoled.textPrimary, fontSize: 15, paddingHorizontal: 8, paddingVertical: 6, maxHeight: 92 },
  roundAction: { width: 38, height: 38, borderRadius: 19, backgroundColor: '#F6D37A', alignItems: 'center', justifyContent: 'center' },
  disabled: { opacity: 0.42 },
  searchRow: { height: 46, flexDirection: 'row', alignItems: 'center', paddingHorizontal: 13, gap: 9, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  searchInput: { flex: 1, color: amoled.textPrimary, fontSize: 15 },
  item: { padding: 15, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  itemText: { color: amoled.textPrimary, fontSize: 16, lineHeight: 22 },
  itemFooter: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginTop: 12 },
  date: { color: amoled.textSecondary, fontSize: 12 },
  itemActions: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  actionButton: { minWidth: 34, minHeight: 34, alignItems: 'center', justifyContent: 'center' },
  actionText: { color: amoled.textSecondary, fontSize: 14 },
  confirmButton: { width: 34, height: 34, borderRadius: 17, backgroundColor: '#F6D37A', alignItems: 'center', justifyContent: 'center' },
  editInput: { color: amoled.textPrimary, fontSize: 16, lineHeight: 22, minHeight: 48, padding: 0 },
  error: { color: '#F87171', fontSize: 14 },
  loading: { marginTop: 32 },
  empty: { color: amoled.textSecondary, textAlign: 'center', marginTop: 38, fontSize: 15 },
});
