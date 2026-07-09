import { useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useKeepAwake } from 'expo-keep-awake';
import { StatusBar } from 'expo-status-bar';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';
import type { VoiceState } from '../hooks/useVoiceChat';
import { useVoiceChat } from '../hooks/useVoiceChat';
import { useSleepTimer } from '../hooks/useSleepTimer';
import { AvatarFace } from '../components/AvatarFace';
import { OlgaLayeredAvatar } from '../components/OlgaLayeredAvatar';
import { ConversationPeek } from '../components/ConversationPeek';
import { VoiceControlDock } from '../components/VoiceControlDock';
import { amoled } from '../theme/amoled';
import { ProfileScreen } from './ProfileScreen';
import { ChatHistoryScreen } from './ChatHistoryScreen';

const SLEEP_AFTER_MS = 90_000;

const HEADLINE: Record<VoiceState, string> = {
  idle: 'Слушаю вас…',
  recording: 'Слышу вас…',
  thinking: 'Думаю…',
  speaking: 'Отвечаю…',
  paused: 'На паузе',
};

const SUBTITLE: Record<VoiceState, string> = {
  idle: 'Можно говорить естественно',
  recording: 'Собираю мысль',
  thinking: 'Сейчас отвечу',
  speaking: '',
  paused: 'Продолжим, когда будете готовы',
};

const PRESENCE_LABEL: Record<VoiceState, string> = {
  idle: 'рядом',
  recording: 'слушает',
  thinking: 'думает',
  speaking: 'говорит',
  paused: 'на паузе',
};

export function HomeScreen() {
  // A slower-paced conversation with pauses between turns is normal here —
  // the screen locking mid-conversation would be more disruptive than a
  // phone that stays awake while this screen is open, so this covers the
  // whole screen, not just the active recording/speaking states.
  useKeepAwake();

  const { assistantName } = useAuth();
  const [sessionId, setSessionId] = useState<number | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [showSettings, setShowSettings] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  // Ошибка загрузки любого слоя OlgaLayeredAvatar — падаем на AvatarFace
  // на остаток сессии, без retry-петли. См. spec, «Обработка ошибок».
  const [assetsFailed, setAssetsFailed] = useState(false);
  const { state, transcript, reply, error, forceFinalize, pauseConversation } = useVoiceChat(sessionId);

  const sleeping = useSleepTimer(state === 'idle', SLEEP_AFTER_MS);

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

  if (showSettings) {
    return <ProfileScreen mode="settings" onDone={() => setShowSettings(false)} />;
  }

  if (showHistory && sessionId) {
    return <ChatHistoryScreen sessionId={sessionId} onDone={() => setShowHistory(false)} />;
  }

  // Та же логика, что была: единственное состояние с реально нечем
  // заняться — отсутствие сессии. 'thinking' больше НЕ блокирует нажатие —
  // long-press во время thinking ставит на паузу (см. pauseConversation),
  // а forceFinalize сам по себе безопасно ничего не делает в 'thinking'.
  const disabled = !sessionId;
  // Настройки НЕ блокируются состоянием разговора — это единственный путь
  // к logout, и useVoiceChat продолжает работать в фоне независимо от того,
  // что отрендерено (хуки безусловны, условен только JSX). Раньше здесь была
  // завязка на state !== 'idle' — с непрерывно слушающим VAD это могло
  // держать кнопку недоступной большую часть времени, что согласуется с
  // репортом с реального устройства ("не работает"); точный механизм не
  // трассирован логами, только код-инспекция. История по-прежнему требует
  // сессию — без неё ей физически нечего показывать.
  const historyDisabled = !sessionId;

  return (
    <SafeAreaView style={styles.safeArea} edges={['top', 'bottom']}>
      <StatusBar style="light" />
      <View style={styles.container}>
        {sessionError && <Text style={styles.error}>{sessionError}</Text>}

        <View style={styles.identityRow}>
          <View style={styles.identityLeft}>
            <View style={styles.onlineDot} />
            <View>
              <Text style={styles.identityName}>{assistantName || 'Ольга'}</Text>
              <Text style={styles.identityPresence}>{PRESENCE_LABEL[state]}</Text>
            </View>
          </View>
          <Pressable
            style={styles.profileButton}
            onPress={() => setShowSettings(true)}
            accessibilityLabel="Профиль"
          >
            <Text style={styles.profileGlyph}>👤</Text>
          </Pressable>
        </View>

        {!sleeping && (
          <View style={styles.headlineBlock}>
            <Text style={styles.headline}>{HEADLINE[state]}</Text>
            {!!SUBTITLE[state] && <Text style={styles.subtitle}>{SUBTITLE[state]}</Text>}
          </View>
        )}

        <View style={styles.avatarStage}>
          <Pressable onPress={forceFinalize} onLongPress={() => void pauseConversation()} disabled={disabled}>
            {assetsFailed ? (
              <AvatarFace state={state} />
            ) : (
              <OlgaLayeredAvatar
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
            )}
          </Pressable>
        </View>

        <ConversationPeek transcript={transcript} reply={reply} state={state} />
        {error && <Text style={styles.error}>{error}</Text>}

        <VoiceControlDock
          state={state}
          onSettingsPress={() => setShowSettings(true)}
          onHistoryPress={() => setShowHistory(true)}
          onMicPress={forceFinalize}
          onMicLongPress={() => void pauseConversation()}
          historyDisabled={historyDisabled}
        />
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: amoled.background,
  },
  container: {
    flex: 1,
    paddingHorizontal: 20,
    paddingTop: 8,
    paddingBottom: 20,
    justifyContent: 'space-between',
  },
  identityRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  identityLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  onlineDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: '#3B82F6',
  },
  identityName: {
    color: amoled.textPrimary,
    fontSize: 17,
    fontWeight: '700',
  },
  identityPresence: {
    color: amoled.textSecondary,
    fontSize: 13,
  },
  profileButton: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: amoled.glassBackground,
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    alignItems: 'center',
    justifyContent: 'center',
  },
  profileGlyph: {
    fontSize: 18,
  },
  headlineBlock: {
    marginTop: 24,
  },
  headline: {
    color: amoled.textPrimary,
    fontSize: 34,
    fontWeight: '800',
  },
  subtitle: {
    color: amoled.textSecondary,
    fontSize: 15,
    marginTop: 6,
  },
  avatarStage: {
    alignItems: 'center',
    justifyContent: 'center',
    flex: 1,
  },
  error: {
    color: '#F87171',
    marginBottom: 12,
    textAlign: 'center',
  },
});
