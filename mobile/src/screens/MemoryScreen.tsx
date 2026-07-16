import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  AppState,
  Image,
  Modal,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import * as Sharing from 'expo-sharing';
import {
  ArrowLeft,
  Check,
  ExternalLink,
  FileText,
  Image as ImageIcon,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Tag,
  Trash2,
} from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { api, type MemoryAttachment, type MemoryItem, type MemoryStatus } from '../api/client';
import { amoled } from '../theme/amoled';

interface MemoryScreenProps {
  onDone: () => void;
}

const MEMORY_CATEGORIES = [
  ['profile', 'О себе'], ['family', 'Семья'], ['contacts', 'Контакты'], ['health', 'Здоровье'],
  ['medications', 'Лекарства'], ['allergies', 'Аллергии'], ['habits', 'Привычки'], ['work', 'Работа'],
  ['education', 'Обучение'], ['finance', 'Финансы'], ['home', 'Дом'], ['pets', 'Питомцы'],
  ['shopping', 'Покупки'], ['recipes', 'Рецепты'], ['food', 'Еда'], ['travel', 'Путешествия'],
  ['transport', 'Транспорт'], ['events', 'События'], ['tasks', 'Дела'], ['projects', 'Проекты'],
  ['hobbies', 'Хобби'], ['books', 'Книги'], ['films', 'Фильмы'], ['music', 'Музыка'],
  ['games', 'Игры'], ['technology', 'Технологии'], ['links', 'Ссылки'], ['documents', 'Документы'],
  ['other', 'Прочее'],
] as const;

type MemoryCategory = typeof MEMORY_CATEGORIES[number][0];
type CategoryPickerTarget = 'new' | 'edit' | null;

function operationId(): string | undefined {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : undefined;
}

function categoryLabel(value: string | null | undefined): string {
  return MEMORY_CATEGORIES.find(([id]) => id === value)?.[1] ?? 'Прочее';
}

function isImageAttachment(attachment: MemoryAttachment): boolean {
  return attachment.mimeType.startsWith('image/');
}

export function MemoryScreen({ onDone }: MemoryScreenProps) {
  const [status, setStatus] = useState<MemoryStatus | null>(null);
  const [items, setItems] = useState<MemoryItem[]>([]);
  const [query, setQuery] = useState('');
  const [newText, setNewText] = useState('');
  const [newCategory, setNewCategory] = useState<MemoryCategory>('other');
  const [editing, setEditing] = useState<string | null>(null);
  const [editText, setEditText] = useState('');
  const [editCategory, setEditCategory] = useState<MemoryCategory>('other');
  const [categoryPickerTarget, setCategoryPickerTarget] = useState<CategoryPickerTarget>(null);
  const [previewAttachment, setPreviewAttachment] = useState<MemoryAttachment | null>(null);
  const [openingAttachmentId, setOpeningAttachmentId] = useState<string | null>(null);
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
    if (!needle) return items;
    return items.filter((item) =>
      item.text.toLocaleLowerCase().includes(needle) ||
      categoryLabel(item.category).toLocaleLowerCase().includes(needle),
    );
  }, [items, query]);

  async function remember() {
    const text = newText.trim();
    if (!text) return;
    setSaving(true);
    setError(null);
    try {
      const receipt = await api.remember(text, newCategory, operationId());
      if (receipt.code !== 'remembered' && receipt.code !== 'already_known') {
        setError('Запись не была сохранена');
        return;
      }
      setNewText('');
      setNewCategory('other');
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
      const receipt = await api.correctMemory(id, text, editCategory, operationId());
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

  async function openAttachment(attachment: MemoryAttachment) {
    if (isImageAttachment(attachment)) {
      setPreviewAttachment(attachment);
      return;
    }

    setOpeningAttachmentId(attachment.id);
    setError(null);
    try {
      const file = await api.downloadVisualAsset(attachment.id, attachment.originalFileName);
      if (!await Sharing.isAvailableAsync()) throw new Error('На этом устройстве недоступен просмотр документов');
      await Sharing.shareAsync(file.uri, {
        mimeType: attachment.mimeType,
        dialogTitle: 'Открыть документ',
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось открыть сохраненный документ');
    } finally {
      setOpeningAttachmentId(null);
    }
  }

  function selectCategory(category: MemoryCategory) {
    if (categoryPickerTarget === 'new') setNewCategory(category);
    if (categoryPickerTarget === 'edit') setEditCategory(category);
    setCategoryPickerTarget(null);
  }

  const pickerCategory = categoryPickerTarget === 'edit' ? editCategory : newCategory;

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
        <Text style={styles.hint}>Здесь остаются только подтвержденные записи. Категорию и текст можно изменить в любой момент.</Text>
        <View style={styles.composer}>
          <TextInput style={styles.composerInput} value={newText} onChangeText={setNewText} placeholder="Добавить запись" placeholderTextColor={amoled.textSecondary} multiline />
          <Pressable style={[styles.roundAction, (!newText.trim() || saving) && styles.disabled]} onPress={() => void remember()} disabled={!newText.trim() || saving} accessibilityLabel="Сохранить в память">
            {saving ? <ActivityIndicator size="small" color="#071018" /> : <Plus size={21} color="#071018" />}
          </Pressable>
        </View>
        <Pressable style={styles.categoryControl} onPress={() => setCategoryPickerTarget('new')} accessibilityLabel="Выбрать категорию новой записи">
          <Tag size={16} color="#F6D37A" />
          <Text style={styles.categoryControlLabel}>Категория</Text>
          <Text style={styles.categoryControlValue}>{categoryLabel(newCategory)}</Text>
        </Pressable>
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
                <Pressable style={styles.categoryControl} onPress={() => setCategoryPickerTarget('edit')} accessibilityLabel="Выбрать категорию записи">
                  <Tag size={16} color="#F6D37A" />
                  <Text style={styles.categoryControlLabel}>Категория</Text>
                  <Text style={styles.categoryControlValue}>{categoryLabel(editCategory)}</Text>
                </Pressable>
                <View style={styles.itemActions}>
                  <Pressable style={styles.actionButton} onPress={() => setEditing(null)}><Text style={styles.actionText}>Отмена</Text></Pressable>
                  <Pressable style={styles.confirmButton} onPress={() => void saveCorrection(item.id)} disabled={saving} accessibilityLabel="Сохранить изменение"><Check size={18} color="#071018" /></Pressable>
                </View>
              </>
            ) : (
              <>
                <View style={styles.itemCategory}><Tag size={14} color="#F6D37A" /><Text style={styles.itemCategoryText}>{categoryLabel(item.category)}</Text></View>
                <Text style={styles.itemText}>{item.text}</Text>
                {item.attachment && (
                  <Pressable style={styles.attachmentRow} onPress={() => void openAttachment(item.attachment!)} disabled={openingAttachmentId === item.attachment.id} accessibilityLabel="Открыть сохраненное вложение">
                    {isImageAttachment(item.attachment) ? <ImageIcon size={19} color="#F6D37A" /> : <FileText size={19} color="#F6D37A" />}
                    <View style={styles.attachmentCopy}>
                      <Text style={styles.attachmentName} numberOfLines={1}>{item.attachment.originalFileName || 'Сохраненное вложение'}</Text>
                      <Text style={styles.attachmentType} numberOfLines={1}>{item.attachment.mimeType}</Text>
                    </View>
                    {openingAttachmentId === item.attachment.id ? <ActivityIndicator color="#F6D37A" /> : <ExternalLink size={18} color={amoled.textSecondary} />}
                  </Pressable>
                )}
                <View style={styles.itemFooter}>
                  <Text style={styles.date}>{new Date(item.updatedAt).toLocaleDateString('ru-RU')}</Text>
                  <View style={styles.itemActions}>
                    <Pressable style={styles.actionButton} onPress={() => { setEditing(item.id); setEditText(item.text); setEditCategory((MEMORY_CATEGORIES.some(([id]) => id === item.category) ? item.category : 'other') as MemoryCategory); }} accessibilityLabel="Изменить запись"><Pencil size={17} color={amoled.textSecondary} /></Pressable>
                    <Pressable style={styles.actionButton} onPress={() => forget(item)} accessibilityLabel="Удалить запись"><Trash2 size={17} color="#F87171" /></Pressable>
                  </View>
                </View>
              </>
            )}
          </View>
        ))}
        {!loading && visibleItems.length === 0 && <Text style={styles.empty}>{query ? 'Ничего не найдено' : 'Здесь пока нет сохраненных записей'}</Text>}
      </ScrollView>

      <Modal visible={categoryPickerTarget !== null} transparent animationType="slide" onRequestClose={() => setCategoryPickerTarget(null)}>
        <View style={styles.modalBackdrop}>
          <Pressable style={styles.backdropPress} onPress={() => setCategoryPickerTarget(null)} />
          <View style={styles.categorySheet}>
            <Text style={styles.sheetTitle}>Категория</Text>
            <ScrollView contentContainerStyle={styles.categoryList}>
              {MEMORY_CATEGORIES.map(([id, label]) => (
                <Pressable key={id} style={[styles.categoryOption, pickerCategory === id && styles.categoryOptionSelected]} onPress={() => selectCategory(id)}>
                  <Text style={[styles.categoryOptionText, pickerCategory === id && styles.categoryOptionTextSelected]}>{label}</Text>
                  {pickerCategory === id && <Check size={18} color="#071018" />}
                </Pressable>
              ))}
            </ScrollView>
          </View>
        </View>
      </Modal>

      <Modal visible={previewAttachment !== null} transparent animationType="fade" onRequestClose={() => setPreviewAttachment(null)}>
        <View style={styles.previewBackdrop}>
          <Pressable style={styles.backdropPress} onPress={() => setPreviewAttachment(null)} />
          {previewAttachment && <Image source={api.visualAssetContentSource(previewAttachment.id)} style={styles.previewImage} resizeMode="contain" accessibilityLabel="Сохраненное изображение" />}
          <Pressable style={styles.closePreview} onPress={() => setPreviewAttachment(null)}><Text style={styles.closePreviewText}>Закрыть</Text></Pressable>
        </View>
      </Modal>
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
  categoryControl: { minHeight: 42, flexDirection: 'row', alignItems: 'center', gap: 8, paddingHorizontal: 12, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  categoryControlLabel: { color: amoled.textSecondary, fontSize: 14 },
  categoryControlValue: { color: amoled.textPrimary, fontSize: 14, fontWeight: '600', marginLeft: 'auto' },
  searchRow: { height: 46, flexDirection: 'row', alignItems: 'center', paddingHorizontal: 13, gap: 9, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  searchInput: { flex: 1, color: amoled.textPrimary, fontSize: 15 },
  item: { padding: 15, borderRadius: 8, backgroundColor: amoled.glassBackground, borderWidth: 1, borderColor: amoled.glassBorder },
  itemCategory: { flexDirection: 'row', alignItems: 'center', gap: 6, marginBottom: 8 },
  itemCategoryText: { color: '#F6D37A', fontSize: 12, fontWeight: '700' },
  itemText: { color: amoled.textPrimary, fontSize: 16, lineHeight: 22 },
  attachmentRow: { minHeight: 58, flexDirection: 'row', alignItems: 'center', gap: 10, marginTop: 12, padding: 10, borderRadius: 8, backgroundColor: '#121A22' },
  attachmentCopy: { flex: 1 },
  attachmentName: { color: amoled.textPrimary, fontSize: 13, fontWeight: '700' },
  attachmentType: { color: amoled.textSecondary, fontSize: 11, marginTop: 2 },
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
  modalBackdrop: { flex: 1, justifyContent: 'flex-end', backgroundColor: 'rgba(0,0,0,0.62)' },
  backdropPress: { ...StyleSheet.absoluteFill },
  categorySheet: { maxHeight: '72%', padding: 20, paddingBottom: 28, backgroundColor: '#101820', borderTopLeftRadius: 8, borderTopRightRadius: 8 },
  sheetTitle: { color: amoled.textPrimary, fontSize: 19, fontWeight: '700', marginBottom: 14 },
  categoryList: { gap: 6 },
  categoryOption: { minHeight: 44, paddingHorizontal: 14, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', borderRadius: 8, backgroundColor: amoled.glassBackground },
  categoryOptionSelected: { backgroundColor: '#F6D37A' },
  categoryOptionText: { color: amoled.textPrimary, fontSize: 15 },
  categoryOptionTextSelected: { color: '#071018', fontWeight: '700' },
  previewBackdrop: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 16, backgroundColor: 'rgba(0,0,0,0.9)' },
  previewImage: { width: '100%', height: '78%' },
  closePreview: { minHeight: 44, marginTop: 16, paddingHorizontal: 20, borderRadius: 8, backgroundColor: '#F6D37A', alignItems: 'center', justifyContent: 'center' },
  closePreviewText: { color: '#071018', fontSize: 15, fontWeight: '700' },
});
