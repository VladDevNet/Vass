import { useCallback, useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, Alert, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { ArrowLeft, BookOpen, ChevronRight, RefreshCw, Trash2 } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { deleteLibraryArtifact, listLibraryArtifacts } from '../library/libraryStore';
import { LIBRARY_KIND_LABELS, type LibraryArtifact } from '../library/types';
import { amoled } from '../theme/amoled';
import { LibraryReaderScreen } from './LibraryReaderScreen';

interface LibraryScreenProps {
  onDone: () => void;
  initialArtifactId?: string | null;
  navigationRequestId?: number | null;
}

function formatDate(value: string): string {
  const date = new Date(value);
  return Number.isFinite(date.getTime())
    ? new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' }).format(date)
    : '';
}

function formatBookCount(count: number): string {
  const lastTwo = count % 100;
  const last = count % 10;
  const noun = lastTwo >= 11 && lastTwo <= 14
    ? 'книг'
    : last === 1
      ? 'книга'
      : last >= 2 && last <= 4
        ? 'книги'
        : 'книг';
  return `${count} ${noun}`;
}

export function LibraryScreen({ onDone, initialArtifactId, navigationRequestId }: LibraryScreenProps) {
  const [items, setItems] = useState<LibraryArtifact[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setItems(await listLibraryArtifacts());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось открыть библиотеку');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  useEffect(() => {
    if (navigationRequestId == null) return;
    if (initialArtifactId && items.some((item) => item.id === initialArtifactId)) {
      setSelectedId(initialArtifactId);
    } else if (!initialArtifactId) {
      setSelectedId(null);
    }
  }, [initialArtifactId, items, navigationRequestId]);

  const selected = useMemo(() => items.find((item) => item.id === selectedId) ?? null, [items, selectedId]);

  function remove(artifact: LibraryArtifact) {
    Alert.alert('Удалить книгу?', `«${artifact.title}» и все её версии будут удалены только с этого устройства.`, [
      { text: 'Отмена', style: 'cancel' },
      {
        text: 'Удалить',
        style: 'destructive',
        onPress: () => {
          void (async () => {
            setDeletingId(artifact.id);
            try {
              await deleteLibraryArtifact(artifact.id);
              if (selectedId === artifact.id) setSelectedId(null);
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось удалить книгу');
            } finally {
              setDeletingId(null);
            }
          })();
        },
      },
    ]);
  }

  if (selected) {
    return <LibraryReaderScreen artifact={selected} onDone={() => setSelectedId(null)} />;
  }

  return (
    <SafeAreaView style={styles.screen} edges={['top', 'bottom']}>
      <View style={styles.header}>
        <Pressable style={styles.iconButton} onPress={onDone} accessibilityLabel="Назад">
          <ArrowLeft size={22} color={amoled.textPrimary} />
        </Pressable>
        <View style={styles.titleBlock}>
          <Text style={styles.title}>Моя библиотека</Text>
          <Text style={styles.subtitle}>{items.length === 0 ? 'Пока пусто' : formatBookCount(items.length)}</Text>
        </View>
        <Pressable style={[styles.iconButton, loading && styles.disabled]} onPress={() => void load()} disabled={loading} accessibilityLabel="Обновить библиотеку">
          <RefreshCw size={19} color={amoled.textSecondary} />
        </Pressable>
      </View>
      <ScrollView contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={loading} onRefresh={() => void load()} tintColor="#F6D37A" />}>
        {error && <Text style={styles.error}>{error}</Text>}
        {loading ? <ActivityIndicator color="#F6D37A" style={styles.loading} /> : null}
        {!loading && items.length === 0 ? (
          <View style={styles.empty}>
            <BookOpen size={34} color={amoled.textSecondary} />
            <Text style={styles.emptyTitle}>Здесь появятся ваши книги</Text>
          </View>
        ) : null}
        {items.map((item) => (
          <View key={item.id} style={styles.card}>
            <Pressable style={styles.cardMain} onPress={() => setSelectedId(item.id)} accessibilityLabel={`Открыть ${item.title}`}>
              <View style={styles.bookIcon}><BookOpen size={20} color="#F6D37A" /></View>
              <View style={styles.cardText}>
                <Text style={styles.cardTitle} numberOfLines={2}>{item.title}</Text>
                {!!item.summary && <Text style={styles.summary} numberOfLines={2}>{item.summary}</Text>}
                <Text style={styles.meta}>{LIBRARY_KIND_LABELS[item.kind]} · {formatDate(item.updatedAt)} · {item.revisions.length} верс.</Text>
              </View>
              <ChevronRight size={20} color={amoled.textSecondary} />
            </Pressable>
            <Pressable
              style={[styles.deleteButton, deletingId === item.id && styles.disabled]}
              onPress={() => remove(item)}
              disabled={deletingId === item.id}
              accessibilityLabel={`Удалить ${item.title}`}
            >
              {deletingId === item.id ? <ActivityIndicator size="small" color="#FCA5A5" /> : <Trash2 size={17} color="#FCA5A5" />}
            </Pressable>
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: amoled.background },
  header: {
    minHeight: 64,
    paddingHorizontal: 16,
    flexDirection: 'row',
    alignItems: 'center',
    borderBottomWidth: 1,
    borderBottomColor: amoled.glassBorder,
  },
  iconButton: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: amoled.glassBackground,
  },
  titleBlock: { flex: 1, marginLeft: 12 },
  title: { color: amoled.textPrimary, fontSize: 20, fontWeight: '700' },
  subtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 2 },
  content: { padding: 16, gap: 10, flexGrow: 1 },
  loading: { marginTop: 38 },
  error: { color: '#F87171', fontSize: 14, lineHeight: 20 },
  card: {
    minHeight: 94,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    backgroundColor: amoled.glassBackground,
    overflow: 'hidden',
  },
  cardMain: { flexDirection: 'row', alignItems: 'center', padding: 14, paddingRight: 48, gap: 12 },
  bookIcon: { width: 38, height: 38, alignItems: 'center', justifyContent: 'center', borderRadius: 8, backgroundColor: 'rgba(246,211,122,0.11)' },
  cardText: { flex: 1, gap: 3 },
  cardTitle: { color: amoled.textPrimary, fontSize: 16, fontWeight: '700', lineHeight: 21 },
  summary: { color: amoled.textSecondary, fontSize: 13, lineHeight: 18 },
  meta: { color: '#718096', fontSize: 12, marginTop: 2 },
  deleteButton: { position: 'absolute', top: 9, right: 8, width: 32, height: 32, alignItems: 'center', justifyContent: 'center' },
  empty: { flex: 1, minHeight: 260, alignItems: 'center', justifyContent: 'center', gap: 12 },
  emptyTitle: { color: amoled.textSecondary, fontSize: 15 },
  disabled: { opacity: 0.42 },
});
