import { useEffect, useState } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useAuth } from '../context/AuthContext';
import { api, type ChatMessage } from '../api/client';

interface ChatHistoryScreenProps {
  sessionId: number;
  onDone: () => void;
}

export function ChatHistoryScreen({ sessionId, onDone }: ChatHistoryScreenProps) {
  const { assistantName } = useAuth();
  const [messages, setMessages] = useState<ChatMessage[] | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setMessages(undefined);
    setError(null);
    api
      .getSession(sessionId)
      .then((session) => {
        if (!cancelled) setMessages(session.messages);
      })
      .catch((err) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  return (
    <View style={styles.screen}>
      <View style={styles.header}>
        <Pressable onPress={onDone} style={styles.backLink}>
          <Text style={styles.backLinkText}>Назад</Text>
        </Pressable>
        <Text style={styles.title}>История</Text>
        <View style={styles.backLink} />
      </View>

      <ScrollView contentContainerStyle={styles.container}>
        {error && <Text style={styles.error}>{error}</Text>}
        {messages === undefined && !error && <ActivityIndicator style={styles.loading} />}
        {messages?.length === 0 && <Text style={styles.hint}>Сообщений пока нет.</Text>}
        {messages?.map((message, index) => {
          const isUser = message.role === 'user';
          return (
            <View
              key={index}
              style={[styles.bubble, !isUser && styles.bubbleReply]}
            >
              <Text style={styles.bubbleLabel}>{isUser ? 'Вы' : assistantName ?? 'Ассистент'}</Text>
              <Text style={styles.bubbleText}>{message.content}</Text>
            </View>
          );
        })}
      </ScrollView>
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
