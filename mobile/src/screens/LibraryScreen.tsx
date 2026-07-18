import { useCallback, useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, Alert, Modal, Pressable, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { ArrowLeft, BookOpen, ChevronRight, Folder, FolderInput, FolderPlus, MoreVertical, Pencil, RefreshCw, Trash2, X } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import {
  createLibrarySection,
  deleteLibraryArtifact,
  deleteLibrarySection,
  getLibraryOverview,
  moveLibraryArtifact,
  renameLibrarySection,
} from '../library/libraryStore';
import { LIBRARY_KIND_LABELS, type LibraryArtifact, type LibrarySection } from '../library/types';
import { amoled } from '../theme/amoled';
import { LibraryReaderScreen } from './LibraryReaderScreen';

interface LibraryScreenProps {
  onDone: () => void;
  initialArtifactId?: string | null;
  navigationRequestId?: number | null;
}

type SectionEditorState = {
  mode: 'create' | 'rename';
  section?: LibrarySection;
  moveArtifactId?: string | null;
};

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

function formatSectionCount(count: number): string {
  const lastTwo = count % 100;
  const last = count % 10;
  const noun = lastTwo >= 11 && lastTwo <= 14
    ? 'разделов'
    : last === 1
      ? 'раздел'
      : last >= 2 && last <= 4
        ? 'раздела'
        : 'разделов';
  return `${count} ${noun}`;
}

export function LibraryScreen({ onDone, initialArtifactId, navigationRequestId }: LibraryScreenProps) {
  const [sections, setSections] = useState<LibrarySection[]>([]);
  const [items, setItems] = useState<LibraryArtifact[]>([]);
  const [selectedSectionId, setSelectedSectionId] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sectionEditor, setSectionEditor] = useState<SectionEditorState | null>(null);
  const [sectionTitle, setSectionTitle] = useState('');
  const [bookActionsFor, setBookActionsFor] = useState<LibraryArtifact | null>(null);
  const [moveArtifact, setMoveArtifact] = useState<LibraryArtifact | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const overview = await getLibraryOverview();
      setSections(overview.sections);
      setItems(overview.artifacts);
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
      setSelectedSectionId(null);
    }
  }, [initialArtifactId, items, navigationRequestId]);

  const selected = useMemo(() => items.find((item) => item.id === selectedId) ?? null, [items, selectedId]);
  const selectedSection = useMemo(
    () => sections.find((section) => section.id === selectedSectionId) ?? null,
    [sections, selectedSectionId],
  );
  const sectionItems = useMemo(
    () => selectedSection ? items.filter((item) => item.sectionId === selectedSection.id) : [],
    [items, selectedSection],
  );
  const counts = useMemo(() => {
    const next = new Map<string, number>();
    for (const item of items) next.set(item.sectionId, (next.get(item.sectionId) ?? 0) + 1);
    return next;
  }, [items]);

  function openCreateSection(moveArtifactId?: string | null) {
    setSectionTitle('');
    setSectionEditor({ mode: 'create', moveArtifactId });
  }

  function openRenameSection(section: LibrarySection) {
    setSectionTitle(section.title);
    setSectionEditor({ mode: 'rename', section });
  }

  async function saveSection() {
    if (!sectionEditor || working) return;
    setWorking(true);
    setError(null);
    try {
      const result = sectionEditor.mode === 'create'
        ? await createLibrarySection(sectionTitle)
        : { section: await renameLibrarySection(sectionEditor.section!.id, sectionTitle), created: false };
      if (sectionEditor.moveArtifactId) await moveLibraryArtifact(sectionEditor.moveArtifactId, result.section.id);
      setSectionEditor(null);
      setSelectedSectionId(result.section.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить раздел');
    } finally {
      setWorking(false);
    }
  }

  function removeBook(artifact: LibraryArtifact) {
    Alert.alert('Удалить книгу?', `«${artifact.title}» и все её версии будут удалены только с этого устройства.`, [
      { text: 'Отмена', style: 'cancel' },
      {
        text: 'Удалить',
        style: 'destructive',
        onPress: () => {
          void (async () => {
            setWorking(true);
            setError(null);
            try {
              await deleteLibraryArtifact(artifact.id);
              setBookActionsFor(null);
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось удалить книгу');
            } finally {
              setWorking(false);
            }
          })();
        },
      },
    ]);
  }

  function removeSection(section: LibrarySection) {
    const count = counts.get(section.id) ?? 0;
    const detail = count === 0
      ? `Удалить пустой раздел «${section.title}»?`
      : `Книги из раздела «${section.title}» останутся в библиотеке и будут перенесены в «Без раздела».`;
    Alert.alert('Удалить раздел?', detail, [
      { text: 'Отмена', style: 'cancel' },
      {
        text: 'Удалить',
        style: 'destructive',
        onPress: () => {
          void (async () => {
            setWorking(true);
            setError(null);
            try {
              await deleteLibrarySection(section.id);
              setSelectedSectionId(null);
              await load();
            } catch (err) {
              setError(err instanceof Error ? err.message : 'Не удалось удалить раздел');
            } finally {
              setWorking(false);
            }
          })();
        },
      },
    ]);
  }

  async function moveBook(artifact: LibraryArtifact, section: LibrarySection) {
    if (working) return;
    setWorking(true);
    setError(null);
    try {
      await moveLibraryArtifact(artifact.id, section.id);
      setMoveArtifact(null);
      setSelectedSectionId(section.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось переместить книгу');
    } finally {
      setWorking(false);
    }
  }

  if (selected) {
    return <LibraryReaderScreen artifact={selected} onDone={() => { setSelectedId(null); setSelectedSectionId(selected.sectionId); }} />;
  }

  const sectionTitleText = items.length === 0
    ? (sections.length === 0 ? 'Пока пусто' : formatSectionCount(sections.length))
    : `${formatSectionCount(sections.length)} · ${formatBookCount(items.length)}`;

  return (
    <SafeAreaView style={styles.screen} edges={['top', 'bottom']}>
      <View style={styles.header}>
        <Pressable
          style={styles.iconButton}
          onPress={selectedSection ? () => setSelectedSectionId(null) : onDone}
          accessibilityLabel={selectedSection ? 'К разделам библиотеки' : 'Назад'}
        >
          <ArrowLeft size={22} color={amoled.textPrimary} />
        </Pressable>
        <View style={styles.titleBlock}>
          <Text style={styles.title} numberOfLines={1}>{selectedSection?.title ?? 'Моя библиотека'}</Text>
          <Text style={styles.subtitle} numberOfLines={1}>
            {selectedSection ? formatBookCount(sectionItems.length) : sectionTitleText}
          </Text>
        </View>
        {selectedSection ? (
          <>
            <Pressable style={[styles.iconButton, working && styles.disabled]} onPress={() => openRenameSection(selectedSection)} disabled={working} accessibilityLabel="Переименовать раздел">
              <Pencil size={19} color={amoled.textSecondary} />
            </Pressable>
            <Pressable style={[styles.iconButton, working && styles.disabled]} onPress={() => removeSection(selectedSection)} disabled={working} accessibilityLabel="Удалить раздел">
              <Trash2 size={19} color="#FCA5A5" />
            </Pressable>
          </>
        ) : (
          <>
            <Pressable style={[styles.iconButton, working && styles.disabled]} onPress={() => openCreateSection()} disabled={working} accessibilityLabel="Создать раздел">
              <FolderPlus size={20} color="#F6D37A" />
            </Pressable>
            <Pressable style={[styles.iconButton, loading && styles.disabled]} onPress={() => void load()} disabled={loading} accessibilityLabel="Обновить библиотеку">
              <RefreshCw size={19} color={amoled.textSecondary} />
            </Pressable>
          </>
        )}
      </View>
      <ScrollView contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={loading} onRefresh={() => void load()} tintColor="#F6D37A" />}>
        {error && <Text style={styles.error}>{error}</Text>}
        {loading ? <ActivityIndicator color="#F6D37A" style={styles.loading} /> : null}
        {!loading && !selectedSection && sections.length === 0 ? (
          <View style={styles.empty}>
            <Folder size={34} color={amoled.textSecondary} />
            <Text style={styles.emptyTitle}>Здесь появятся ваши разделы</Text>
            <Text style={styles.emptyHint}>Ассистент сам разложит новые книги, а свой раздел можно создать кнопкой сверху.</Text>
          </View>
        ) : null}
        {!loading && !selectedSection ? sections.map((section) => {
          const count = counts.get(section.id) ?? 0;
          return (
            <Pressable key={section.id} style={styles.sectionRow} onPress={() => setSelectedSectionId(section.id)} accessibilityLabel={`Открыть раздел ${section.title}`}>
              <View style={styles.sectionIcon}><Folder size={21} color="#F6D37A" /></View>
              <View style={styles.sectionText}>
                <Text style={styles.sectionName} numberOfLines={1}>{section.title}</Text>
                <Text style={styles.sectionMeta}>{formatBookCount(count)}</Text>
              </View>
              <ChevronRight size={20} color={amoled.textSecondary} />
            </Pressable>
          );
        }) : null}
        {!loading && selectedSection && sectionItems.length === 0 ? (
          <View style={styles.empty}>
            <BookOpen size={34} color={amoled.textSecondary} />
            <Text style={styles.emptyTitle}>В этом разделе пока нет книг</Text>
          </View>
        ) : null}
        {!loading && selectedSection ? sectionItems.map((item) => (
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
            <Pressable style={[styles.bookMenuButton, working && styles.disabled]} onPress={() => setBookActionsFor(item)} disabled={working} accessibilityLabel={`Действия с ${item.title}`}>
              <MoreVertical size={20} color={amoled.textSecondary} />
            </Pressable>
          </View>
        )) : null}
      </ScrollView>

      <Modal visible={sectionEditor !== null} transparent animationType="fade" onRequestClose={() => !working && setSectionEditor(null)}>
        <View style={styles.modalOverlay}>
          <Pressable style={StyleSheet.absoluteFill} onPress={() => !working && setSectionEditor(null)} accessibilityLabel="Закрыть" />
          <View style={styles.dialog}>
            <Text style={styles.dialogTitle}>{sectionEditor?.mode === 'rename' ? 'Переименовать раздел' : 'Новый раздел'}</Text>
            <Text style={styles.inputLabel}>Название раздела</Text>
            <TextInput
              value={sectionTitle}
              onChangeText={setSectionTitle}
              style={styles.input}
              placeholder="Например, Путешествия"
              placeholderTextColor="#718096"
              maxLength={60}
              autoFocus
              editable={!working}
              returnKeyType="done"
              onSubmitEditing={() => void saveSection()}
              accessibilityLabel="Название раздела"
            />
            <View style={styles.dialogActions}>
              <Pressable style={styles.textButton} onPress={() => setSectionEditor(null)} disabled={working} accessibilityLabel="Отмена">
                <Text style={styles.textButtonLabel}>Отмена</Text>
              </Pressable>
              <Pressable style={[styles.primaryTextButton, working && styles.disabled]} onPress={() => void saveSection()} disabled={working} accessibilityLabel="Сохранить раздел">
                {working ? <ActivityIndicator size="small" color="#101820" /> : <Text style={styles.primaryTextButtonLabel}>Сохранить</Text>}
              </Pressable>
            </View>
          </View>
        </View>
      </Modal>

      <Modal visible={bookActionsFor !== null} transparent animationType="fade" onRequestClose={() => setBookActionsFor(null)}>
        <View style={styles.modalOverlay}>
          <Pressable style={StyleSheet.absoluteFill} onPress={() => setBookActionsFor(null)} accessibilityLabel="Закрыть действия с книгой" />
          <View style={styles.sheet}>
            <View style={styles.sheetHeader}>
              <Text style={styles.sheetTitle} numberOfLines={1}>{bookActionsFor?.title}</Text>
              <Pressable style={styles.sheetCloseButton} onPress={() => setBookActionsFor(null)} accessibilityLabel="Закрыть">
                <X size={20} color={amoled.textPrimary} />
              </Pressable>
            </View>
            <Pressable style={styles.actionRow} onPress={() => { setMoveArtifact(bookActionsFor); setBookActionsFor(null); }} accessibilityLabel="Переместить в раздел">
              <FolderInput size={20} color="#F6D37A" />
              <Text style={styles.actionLabel}>Переместить в раздел</Text>
            </Pressable>
            <Pressable style={styles.actionRow} onPress={() => bookActionsFor && removeBook(bookActionsFor)} accessibilityLabel="Удалить книгу">
              <Trash2 size={20} color="#FCA5A5" />
              <Text style={[styles.actionLabel, styles.dangerLabel]}>Удалить книгу</Text>
            </Pressable>
          </View>
        </View>
      </Modal>

      <Modal visible={moveArtifact !== null} transparent animationType="fade" onRequestClose={() => !working && setMoveArtifact(null)}>
        <View style={styles.modalOverlay}>
          <Pressable style={StyleSheet.absoluteFill} onPress={() => !working && setMoveArtifact(null)} accessibilityLabel="Закрыть выбор раздела" />
          <View style={styles.sheet}>
            <View style={styles.sheetHeader}>
              <View style={styles.sheetTitleBlock}>
                <Text style={styles.sheetTitle}>Переместить книгу</Text>
                <Text style={styles.sheetSubtitle} numberOfLines={1}>{moveArtifact?.title}</Text>
              </View>
              <Pressable style={styles.sheetCloseButton} onPress={() => setMoveArtifact(null)} disabled={working} accessibilityLabel="Закрыть">
                <X size={20} color={amoled.textPrimary} />
              </Pressable>
            </View>
            <ScrollView contentContainerStyle={styles.moveList} keyboardShouldPersistTaps="handled">
              {sections.filter((section) => section.id !== moveArtifact?.sectionId).map((section) => (
                <Pressable key={section.id} style={styles.actionRow} onPress={() => moveArtifact && void moveBook(moveArtifact, section)} disabled={working} accessibilityLabel={`Переместить в ${section.title}`}>
                  <Folder size={20} color="#F6D37A" />
                  <Text style={styles.actionLabel}>{section.title}</Text>
                  <ChevronRight size={19} color={amoled.textSecondary} />
                </Pressable>
              ))}
              <Pressable
                style={styles.actionRow}
                onPress={() => {
                  const artifactId = moveArtifact?.id ?? null;
                  setMoveArtifact(null);
                  openCreateSection(artifactId);
                }}
                disabled={working}
                accessibilityLabel="Создать раздел и переместить книгу"
              >
                <FolderPlus size={20} color="#F6D37A" />
                <Text style={styles.actionLabel}>Новый раздел</Text>
              </Pressable>
            </ScrollView>
          </View>
        </View>
      </Modal>
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
    gap: 8,
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
  titleBlock: { flex: 1, minWidth: 0, marginLeft: 4 },
  title: { color: amoled.textPrimary, fontSize: 20, fontWeight: '700' },
  subtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 2 },
  content: { padding: 16, gap: 10, flexGrow: 1 },
  loading: { marginTop: 38 },
  error: { color: '#F87171', fontSize: 14, lineHeight: 20 },
  sectionRow: {
    minHeight: 72,
    paddingHorizontal: 14,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    backgroundColor: amoled.glassBackground,
  },
  sectionIcon: { width: 38, height: 38, alignItems: 'center', justifyContent: 'center', borderRadius: 8, backgroundColor: 'rgba(246,211,122,0.11)' },
  sectionText: { flex: 1, minWidth: 0, gap: 3 },
  sectionName: { color: amoled.textPrimary, fontSize: 16, fontWeight: '700' },
  sectionMeta: { color: amoled.textSecondary, fontSize: 13 },
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
  cardText: { flex: 1, minWidth: 0, gap: 3 },
  cardTitle: { color: amoled.textPrimary, fontSize: 16, fontWeight: '700', lineHeight: 21 },
  summary: { color: amoled.textSecondary, fontSize: 13, lineHeight: 18 },
  meta: { color: '#718096', fontSize: 12, marginTop: 2 },
  bookMenuButton: { position: 'absolute', top: 8, right: 7, width: 34, height: 34, alignItems: 'center', justifyContent: 'center' },
  empty: { flex: 1, minHeight: 260, alignItems: 'center', justifyContent: 'center', gap: 12, paddingHorizontal: 28 },
  emptyTitle: { color: amoled.textSecondary, fontSize: 15, textAlign: 'center' },
  emptyHint: { color: '#718096', fontSize: 13, lineHeight: 19, textAlign: 'center' },
  disabled: { opacity: 0.42 },
  modalOverlay: { flex: 1, justifyContent: 'flex-end', backgroundColor: 'rgba(0, 0, 0, 0.64)' },
  dialog: {
    margin: 18,
    padding: 18,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    backgroundColor: amoled.background,
  },
  dialogTitle: { color: amoled.textPrimary, fontSize: 19, fontWeight: '700' },
  inputLabel: { color: amoled.textSecondary, fontSize: 13, marginTop: 18, marginBottom: 7 },
  input: {
    minHeight: 48,
    paddingHorizontal: 13,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    color: amoled.textPrimary,
    fontSize: 16,
    backgroundColor: amoled.glassBackground,
  },
  dialogActions: { flexDirection: 'row', justifyContent: 'flex-end', gap: 8, marginTop: 18 },
  textButton: { minHeight: 42, paddingHorizontal: 14, justifyContent: 'center' },
  textButtonLabel: { color: amoled.textSecondary, fontSize: 15, fontWeight: '700' },
  primaryTextButton: { minHeight: 42, paddingHorizontal: 16, borderRadius: 8, justifyContent: 'center', alignItems: 'center', backgroundColor: '#F6D37A' },
  primaryTextButtonLabel: { color: '#101820', fontSize: 15, fontWeight: '700' },
  sheet: {
    maxHeight: '72%',
    paddingBottom: 18,
    borderTopWidth: 1,
    borderTopColor: amoled.glassBorder,
    borderTopLeftRadius: 8,
    borderTopRightRadius: 8,
    backgroundColor: amoled.background,
  },
  sheetHeader: { minHeight: 72, paddingHorizontal: 18, flexDirection: 'row', alignItems: 'center', gap: 12, borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  sheetTitleBlock: { flex: 1, minWidth: 0 },
  sheetTitle: { flex: 1, color: amoled.textPrimary, fontSize: 18, fontWeight: '700' },
  sheetSubtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 3 },
  sheetCloseButton: { width: 40, height: 40, borderRadius: 20, alignItems: 'center', justifyContent: 'center', backgroundColor: amoled.glassBackground },
  actionRow: { minHeight: 58, paddingHorizontal: 18, flexDirection: 'row', alignItems: 'center', gap: 14, borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  actionLabel: { flex: 1, color: amoled.textPrimary, fontSize: 16, fontWeight: '600' },
  dangerLabel: { color: '#FCA5A5' },
  moveList: { paddingBottom: 2 },
});
