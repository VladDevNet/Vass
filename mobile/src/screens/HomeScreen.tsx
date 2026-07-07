import { useEffect, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useKeepAwake } from 'expo-keep-awake';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';
import { useVoiceChat } from '../hooks/useVoiceChat';
import { AvatarFace } from '../components/AvatarFace';
import { ProfileScreen } from './ProfileScreen';

const STATE_LABEL: Record<string, string> = {
  idle: 'Нажмите и говорите',
  recording: 'Слушаю… нажмите ещё раз, когда закончите',
  thinking: 'Думаю…',
  speaking: 'Отвечаю…',
};

export function HomeScreen() {
  // A slower-paced conversation with pauses between turns is normal here —
  // the screen locking mid-conversation would be more disruptive than a
  // phone that stays awake while this screen is open, so this covers the
  // whole screen, not just the active recording/speaking states.
  useKeepAwake();

  const { user, displayName, logout } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [deviceCode, setDeviceCode] = useState<string | null>(null);
  const [isGenerating, setIsGenerating] = useState(false);
  const [linkError, setLinkError] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const { state, transcript, reply, error, startRecording, stopAndRespond } = useVoiceChat(sessionId);

  useEffect(() => {
    let cancelled = false;
    api
      .getSessions()
      .then((sessions) => {
        if (!cancelled && sessions.length > 0) setSessionId(sessions[0].id);
      })
      .catch((err) => {
        if (!cancelled) setSessionError(err instanceof Error ? err.message : String(err));
      });
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleShowDeviceCode() {
    setLinkError(null);
    setIsGenerating(true);
    try {
      const { code } = await api.createDeviceLink();
      setDeviceCode(code);
    } catch (err) {
      setLinkError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsGenerating(false);
    }
  }

  function handlePress() {
    if (state === 'idle') startRecording();
    else if (state === 'recording') stopAndRespond();
  }

  if (showSettings) {
    return <ProfileScreen mode="settings" onDone={() => setShowSettings(false)} />;
  }

  const busy = state === 'thinking' || state === 'speaking';

  return (
    <ScrollView contentContainerStyle={styles.container}>
      <Text style={styles.greeting}>Привет, {displayName ?? user?.email}</Text>

      {sessionError && <Text style={styles.error}>{sessionError}</Text>}

      <Pressable onPress={handlePress} disabled={busy || !sessionId}>
        <AvatarFace state={state} />
      </Pressable>
      <Text style={styles.stateLabel}>{STATE_LABEL[state]}</Text>

      {!!transcript && (
        <View style={styles.bubble}>
          <Text style={styles.bubbleLabel}>Вы:</Text>
          <Text style={styles.bubbleText}>{transcript}</Text>
        </View>
      )}
      {!!reply && (
        <View style={[styles.bubble, styles.bubbleReply]}>
          <Text style={styles.bubbleLabel}>Ассистент:</Text>
          <Text style={styles.bubbleText}>{reply}</Text>
        </View>
      )}
      {error && <Text style={styles.error}>{error}</Text>}

      <View style={styles.divider} />

      {deviceCode ? (
        <View style={styles.codeBox}>
          <Text style={styles.codeLabel}>Код действителен 10 минут:</Text>
          <Text style={styles.codeValue}>{deviceCode}</Text>
          <Text style={styles.codeHint}>
            Введите его на новом устройстве в разделе «Есть код с другого устройства?»
          </Text>
        </View>
      ) : (
        <Pressable style={styles.linkButton} onPress={handleShowDeviceCode} disabled={isGenerating}>
          <Text style={styles.linkButtonText}>
            {isGenerating ? 'Создаю код…' : 'Показать код для нового устройства'}
          </Text>
        </Pressable>
      )}
      {linkError && <Text style={styles.error}>{linkError}</Text>}

      <Pressable
        style={[styles.linkButton, state !== 'idle' && styles.buttonDisabled]}
        onPress={() => setShowSettings(true)}
        disabled={state !== 'idle'}
      >
        <Text style={styles.linkButtonText}>Настройки</Text>
      </Pressable>

      <Pressable style={styles.logoutButton} onPress={logout}>
        <Text style={styles.logoutText}>Выйти</Text>
      </Pressable>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: {
    flexGrow: 1,
    alignItems: 'center',
    padding: 24,
    paddingTop: 48,
    backgroundColor: '#fff',
  },
  greeting: {
    fontSize: 18,
    fontWeight: '600',
    marginBottom: 24,
    textAlign: 'center',
  },
  stateLabel: {
    fontSize: 15,
    color: '#666',
    marginBottom: 24,
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
  divider: {
    alignSelf: 'stretch',
    height: 1,
    backgroundColor: '#eee',
    marginVertical: 24,
  },
  linkButton: {
    borderWidth: 1,
    borderColor: '#4a6fa5',
    borderRadius: 10,
    paddingVertical: 12,
    paddingHorizontal: 20,
    marginBottom: 24,
  },
  linkButtonText: {
    color: '#4a6fa5',
    fontSize: 15,
    fontWeight: '600',
  },
  buttonDisabled: {
    opacity: 0.5,
  },
  codeBox: {
    alignItems: 'center',
    marginBottom: 24,
    padding: 16,
    borderRadius: 12,
    backgroundColor: '#f0f4fa',
  },
  codeLabel: {
    fontSize: 14,
    color: '#666',
    marginBottom: 8,
  },
  codeValue: {
    fontSize: 40,
    fontWeight: '700',
    letterSpacing: 8,
    color: '#4a6fa5',
    marginBottom: 8,
  },
  codeHint: {
    fontSize: 13,
    color: '#666',
    textAlign: 'center',
    maxWidth: 260,
  },
  error: {
    color: '#c0392b',
    marginBottom: 16,
    textAlign: 'center',
  },
  logoutButton: {
    borderWidth: 1,
    borderColor: '#c0392b',
    borderRadius: 10,
    paddingVertical: 12,
    paddingHorizontal: 24,
  },
  logoutText: {
    color: '#c0392b',
    fontSize: 16,
    fontWeight: '600',
  },
});
