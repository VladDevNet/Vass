import { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from 'react-native';
import { useAuth } from '../context/AuthContext';
import { api, type ChatMessage } from '../api/client';
import { ChatAttachmentThumbnail } from '../components/ChatAttachmentThumbnail';

interface ChatHistoryScreenProps {
  sessionId: number;
  onDone: () => void;
}

const PAGE_SIZE = 30;

export function ChatHistoryScreen({ sessionId, onDone }: ChatHistoryScreenProps) {
  const { assistantName } = useAuth();
  // Newest-first — FlatList's `inverted` flips rendering so index 0 (newest)
  // lands at the bottom on open, and scrolling toward the END of this array
  // (oldest) is what visually reads as scrolling up, same as any chat app.
  const [messages, setMessages] = useState<ChatMessage[] | undefined>(undefined);
  const [hasMore, setHasMore] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setMessages(undefined);
    setError(null);
    setHasMore(true);
    api
      .getSession(sessionId, undefined, PAGE_SIZE)
      .then((session) => {
        if (cancelled) return;
        setMessages([...session.messages].reverse());
        setHasMore(session.hasMore);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  const loadMore = useCallback(() => {
    if (loadingMore || !hasMore || !messages || messages.length === 0) return;
    setLoadingMore(true);
    const oldestId = messages[messages.length - 1].id;
    api
      .getSession(sessionId, oldestId, PAGE_SIZE)
      .then((session) => {
        // Empty page: backend's hasMore heuristic (page.Count === limit) can
        // occasionally over-predict at the exact tail — stop regardless of
        // what hasMore says once a page comes back with nothing new.
        setHasMore(session.hasMore && session.messages.length > 0);
        setMessages((prev) => [...(prev ?? []), ...[...session.messages].reverse()]);
      })
      .catch((err) => setError(err instanceof Error ? err.message : String(err)))
      .finally(() => setLoadingMore(false));
  }, [sessionId, messages, hasMore, loadingMore]);

  return (
    <View style={styles.screen}>
      <View style={styles.header}>
        <Pressable onPress={onDone} style={styles.backLink}>
          <Text style={styles.backLinkText}>Назад</Text>
        </Pressable>
        <Text style={styles.title}>История</Text>
        <View style={styles.backLink} />
      </View>

      {error && <Text style={styles.error}>{error}</Text>}
      {messages === undefined && !error && <ActivityIndicator style={styles.loading} />}
      {messages?.length === 0 && <Text style={styles.hint}>Сообщений пока нет.</Text>}
      {messages !== undefined && messages.length > 0 && (
        <FlatList
          inverted
          data={messages}
          keyExtractor={(message) => String(message.id)}
          contentContainerStyle={styles.container}
          onEndReached={loadMore}
          onEndReachedThreshold={0.5}
          ListFooterComponent={loadingMore ? <ActivityIndicator style={styles.loadingMore} /> : null}
          renderItem={({ item: message }) => {
            const isUser = message.role === 'user';
            return (
              <View style={[styles.bubble, !isUser && styles.bubbleReply]}>
                <Text style={styles.bubbleLabel}>{isUser ? 'Вы' : assistantName ?? 'Ассистент'}</Text>
                {message.attachments?.map((attachment) => (
                  <ChatAttachmentThumbnail key={attachment.id} attachment={attachment} />
                ))}
                <Text style={styles.bubbleText}>{message.content}</Text>
              </View>
            );
          }}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    backgroundColor: '#fff',
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingTop: 48,
    paddingHorizontal: 16,
    paddingBottom: 12,
    borderBottomWidth: 1,
    borderBottomColor: '#eee',
  },
  backLink: {
    minWidth: 60,
  },
  backLinkText: {
    color: '#4a6fa5',
    fontSize: 15,
  },
  title: {
    fontSize: 17,
    fontWeight: '700',
  },
  container: {
    flexGrow: 1,
    padding: 16,
  },
  loading: {
    marginTop: 24,
  },
  loadingMore: {
    marginVertical: 16,
  },
  hint: {
    fontSize: 15,
    color: '#666',
    textAlign: 'center',
    marginTop: 24,
  },
  error: {
    color: '#c0392b',
    marginBottom: 16,
    textAlign: 'center',
  },
  bubble: {
    alignSelf: 'stretch',
    backgroundColor: '#f0f4fa',
    borderRadius: 12,
    padding: 14,
    marginBottom: 10,
  },
  bubbleReply: {
    backgroundColor: '#eef7ee',
  },
  bubbleLabel: {
    fontSize: 12,
    fontWeight: '700',
    color: '#666',
    marginBottom: 4,
  },
  bubbleText: {
    fontSize: 16,
    lineHeight: 22,
  },
});
