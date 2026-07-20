import { useEffect, useState } from 'react';
import { ActivityIndicator, Alert, Modal, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { ArrowLeft, Check, FileWarning, History, Share2, X } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { WebView } from 'react-native-webview';
import { shareLibraryRevisionAsPdf } from '../library/libraryPdf';
import { readLibraryArtifactRevisionHtml } from '../library/libraryStore';
import type { LibraryArtifact } from '../library/types';
import { amoled } from '../theme/amoled';

interface LibraryReaderScreenProps {
  artifact: LibraryArtifact;
  onDone: () => void;
}

export function LibraryReaderScreen({ artifact, onDone }: LibraryReaderScreenProps) {
  const [html, setHtml] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedRevisionId, setSelectedRevisionId] = useState(artifact.currentRevisionId);
  const [loadedRevisionId, setLoadedRevisionId] = useState<string | null>(null);
  const [showRevisions, setShowRevisions] = useState(false);
  const [sharingPdf, setSharingPdf] = useState(false);

  const selectedRevisionIndex = artifact.revisions.findIndex((revision) => revision.id === selectedRevisionId);
  const selectedRevisionNumber = selectedRevisionIndex >= 0 ? selectedRevisionIndex + 1 : artifact.revisions.length;

  useEffect(() => {
    setSelectedRevisionId(artifact.currentRevisionId);
  }, [artifact.currentRevisionId, artifact.id]);

  useEffect(() => {
    let cancelled = false;
    setHtml(null);
    setError(null);
    setLoadedRevisionId(null);
    void readLibraryArtifactRevisionHtml(artifact.id, selectedRevisionId, artifact)
      .then((content) => {
        if (cancelled) return;
        if (!content) {
          setError('Не удалось открыть эту версию книги.');
          return;
        }
        setHtml(content);
        setLoadedRevisionId(selectedRevisionId);
      })
      .catch(() => {
        if (!cancelled) setError('Не удалось прочитать книгу.');
      });
    return () => {
      cancelled = true;
    };
  }, [artifact, selectedRevisionId]);

  const canShareCurrentRevision = html !== null && loadedRevisionId === selectedRevisionId;

  async function shareCurrentRevisionPdf() {
    if (!html || !canShareCurrentRevision || sharingPdf) return;
    setSharingPdf(true);
    try {
      await shareLibraryRevisionAsPdf(artifact.title, html);
    } catch (shareError) {
      Alert.alert(
        'Не удалось отправить PDF',
        shareError instanceof Error ? shareError.message : 'Попробуйте ещё раз.',
      );
    } finally {
      setSharingPdf(false);
    }
  }

  return (
    <SafeAreaView style={styles.screen} edges={['top', 'bottom']}>
      <View style={styles.header}>
        <Pressable style={styles.iconButton} onPress={onDone} accessibilityLabel="К оглавлению библиотеки">
          <ArrowLeft size={22} color={amoled.textPrimary} />
        </Pressable>
        <View style={styles.titleBlock}>
          <Text style={styles.title} numberOfLines={1}>{artifact.title}</Text>
          <Text style={styles.subtitle}>Версия {selectedRevisionNumber} из {artifact.revisions.length}</Text>
        </View>
        <View style={styles.toolbarActions}>
          <Pressable
            style={[styles.iconButton, (!canShareCurrentRevision || sharingPdf) && styles.iconButtonDisabled]}
            onPress={() => void shareCurrentRevisionPdf()}
            disabled={!canShareCurrentRevision || sharingPdf}
            accessibilityLabel={`Отправить PDF: ${artifact.title}, версия ${selectedRevisionNumber}`}
          >
            {sharingPdf ? <ActivityIndicator size="small" color="#F6D37A" /> : <Share2 size={20} color={amoled.textPrimary} />}
          </Pressable>
          <Pressable
            style={styles.iconButton}
            onPress={() => setShowRevisions(true)}
            accessibilityLabel="Выбрать версию книги"
          >
            <History size={21} color={amoled.textPrimary} />
          </Pressable>
        </View>
      </View>
      {html ? (
        <WebView
          style={styles.reader}
          source={{ html }}
          originWhitelist={['*']}
          javaScriptEnabled={false}
          domStorageEnabled={false}
          javaScriptCanOpenWindowsAutomatically={false}
          setSupportMultipleWindows={false}
          allowFileAccess={false}
          allowUniversalAccessFromFileURLs={false}
          mixedContentMode="never"
          thirdPartyCookiesEnabled={false}
          cacheEnabled={false}
          incognito
          onShouldStartLoadWithRequest={(request) =>
            request.url === 'about:blank' || request.url.startsWith('data:text/html')
          }
          onError={() => setError('Не удалось показать эту книгу.')}
        />
      ) : (
        <View style={styles.placeholder}>
          {error ? <FileWarning size={32} color="#FCA5A5" /> : <ActivityIndicator color="#F6D37A" />}
          <Text style={styles.placeholderText}>{error ?? 'Открываю книгу…'}</Text>
        </View>
      )}
      <Modal
        visible={showRevisions}
        transparent
        animationType="fade"
        onRequestClose={() => setShowRevisions(false)}
      >
        <View style={styles.modalOverlay}>
          <Pressable style={StyleSheet.absoluteFill} onPress={() => setShowRevisions(false)} accessibilityLabel="Закрыть выбор версии" />
          <View style={styles.revisionSheet}>
            <View style={styles.sheetHeader}>
              <View>
                <Text style={styles.sheetTitle}>Версии книги</Text>
                <Text style={styles.sheetSubtitle}>Хранятся только на этом устройстве</Text>
              </View>
              <Pressable style={styles.sheetCloseButton} onPress={() => setShowRevisions(false)} accessibilityLabel="Закрыть">
                <X size={21} color={amoled.textPrimary} />
              </Pressable>
            </View>
            <ScrollView contentContainerStyle={styles.revisionList}>
              {[...artifact.revisions].reverse().map((revision, reverseIndex) => {
                const revisionNumber = artifact.revisions.length - reverseIndex;
                const selected = revision.id === selectedRevisionId;
                const date = new Date(revision.createdAt);
                const dateText = Number.isFinite(date.getTime())
                  ? new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }).format(date)
                  : '';
                return (
                  <Pressable
                    key={revision.id}
                    style={[styles.revisionRow, selected && styles.revisionRowSelected]}
                    onPress={() => { setSelectedRevisionId(revision.id); setShowRevisions(false); }}
                    accessibilityLabel={`Открыть версию ${revisionNumber}`}
                  >
                    <View style={styles.revisionText}>
                      <Text style={styles.revisionTitle}>Версия {revisionNumber}</Text>
                      <Text style={styles.revisionMeta}>{dateText}</Text>
                      {!!revision.note && <Text style={styles.revisionNote}>{revision.note}</Text>}
                    </View>
                    {selected && <Check size={20} color="#F6D37A" />}
                  </Pressable>
                );
              })}
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
  iconButtonDisabled: { opacity: 0.42 },
  titleBlock: { flex: 1, marginLeft: 12 },
  toolbarActions: { flexDirection: 'row', gap: 8, marginLeft: 12 },
  title: { color: amoled.textPrimary, fontSize: 19, fontWeight: '700' },
  subtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 2 },
  reader: { flex: 1, backgroundColor: '#f7f8fb' },
  placeholder: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 14, paddingHorizontal: 32 },
  placeholderText: { color: amoled.textSecondary, fontSize: 15, textAlign: 'center' },
  modalOverlay: { flex: 1, justifyContent: 'flex-end', backgroundColor: 'rgba(0, 0, 0, 0.64)' },
  revisionSheet: {
    maxHeight: '72%',
    paddingBottom: 18,
    borderTopWidth: 1,
    borderTopColor: amoled.glassBorder,
    borderTopLeftRadius: 8,
    borderTopRightRadius: 8,
    backgroundColor: amoled.background,
  },
  sheetHeader: { minHeight: 72, paddingHorizontal: 18, flexDirection: 'row', alignItems: 'center', borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  sheetTitle: { color: amoled.textPrimary, fontSize: 19, fontWeight: '700' },
  sheetSubtitle: { color: amoled.textSecondary, fontSize: 13, marginTop: 3 },
  sheetCloseButton: { marginLeft: 'auto', width: 40, height: 40, borderRadius: 20, alignItems: 'center', justifyContent: 'center', backgroundColor: amoled.glassBackground },
  revisionList: { paddingVertical: 4 },
  revisionRow: { minHeight: 76, paddingHorizontal: 18, paddingVertical: 12, flexDirection: 'row', alignItems: 'center', gap: 12, borderBottomWidth: 1, borderBottomColor: amoled.glassBorder },
  revisionRowSelected: { backgroundColor: 'rgba(246,211,122,0.09)' },
  revisionText: { flex: 1, gap: 2 },
  revisionTitle: { color: amoled.textPrimary, fontSize: 16, fontWeight: '700' },
  revisionMeta: { color: amoled.textSecondary, fontSize: 13 },
  revisionNote: { color: '#A7B6CB', fontSize: 13, lineHeight: 18, marginTop: 2 },
});
