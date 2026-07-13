import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { StatusBar } from 'expo-status-bar';
import { useAuth } from '../context/AuthContext';
import { log } from '../logging/remoteLogger';
import type { VoiceState } from '../hooks/useVoiceChat';
import { useConversationRuntime } from '../context/ConversationRuntimeContext';
import { useSleepTimer } from '../hooks/useSleepTimer';
import { useGreeting } from '../hooks/useGreeting';
import { useConversationKeepAwake } from '../hooks/useConversationKeepAwake';
import { AvatarFace } from '../components/AvatarFace';
import { LayeredAvatar, type AvatarId } from '../components/LayeredAvatar';
import { ConversationPeek } from '../components/ConversationPeek';
import { VoiceControlDock } from '../components/VoiceControlDock';
import { amoled } from '../theme/amoled';
import { ProfileScreen } from './ProfileScreen';
import { ChatHistoryScreen } from './ChatHistoryScreen';
import { VisualInputButton } from '../components/VisualInputButton';
import { VisualSourceSheet } from '../components/VisualSourceSheet';
import { PendingVisualPreview } from '../components/PendingVisualPreview';
import type { VisualSource } from '../visual/types';

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
  const { assistantName, avatarId } = useAuth();
  const displayAvatarId: AvatarId = avatarId === 'male' ? 'male' : 'olga';
  const [showSettings, setShowSettings] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [showVisualSources, setShowVisualSources] = useState(false);
  // Ошибка загрузки любого слоя LayeredAvatar — падаем на AvatarFace
  // на остаток сессии, без retry-петли. См. spec, «Обработка ошибок».
  const [assetsFailed, setAssetsFailed] = useState(false);
  const {
    sessionId,
    sessionError,
    state,
    transcript,
    reply,
    error,
    forceFinalize,
    pauseConversation,
    micArmed,
    pendingVisual,
    visualStatus,
    visualError,
    visualUploadingUri,
    pickVisual,
    removePendingVisual,
  } = useConversationRuntime();
  useConversationKeepAwake(state !== 'paused');

  const sleeping = useSleepTimer(state === 'idle', SLEEP_AFTER_MS);
  // micArmed, not just state === 'idle' -- see useVoiceChat.ts's own
  // comment on why: state is 'idle' from the very first render, before mic
  // permission has even been requested, which used to let the cold-start
  // greeting's "fires once" guard latch true well before the OS permission
  // dialog could even appear (found in review -- that dialog's own
  // background/foreground churn then masqueraded as a real focus-return,
  // greeting twice on a fresh install).
  useGreeting(micArmed && state === 'idle' && !!sessionId);

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

  // Settings/History navigation had zero diagnostic visibility before this —
  // a real-device report of the app freezing/not responding to input had no
  // way to tell whether a navigation attempt reached this handler at all.
  // Purely additive (no behavior change), same reasoning as forceFinalize's
  // new no-op log in useVoiceChat.ts.
  function openSettings() {
    log('debug', 'app', 'settings opened', { state, sleeping });
    setShowSettings(true);
  }
  function openHistory() {
    log('debug', 'app', 'history opened', { state, sleeping, hasSession: !!sessionId });
    setShowHistory(true);
  }

  function selectVisualSource(source: VisualSource) {
    setShowVisualSources(false);
    void pickVisual(source);
  }

  return (
    <SafeAreaView style={styles.safeArea} edges={['top', 'bottom']}>
      <StatusBar style="light" />
      <View style={styles.container}>
        {sessionError && <Text style={styles.error}>{sessionError}</Text>}

        <View style={styles.identityRow}>
          <View style={styles.identityLeft}>
            <View style={styles.onlineDot} />
            <View>
              <Text style={styles.identityName}>
                {assistantName || (displayAvatarId === 'male' ? 'Максим' : 'Ольга')}
              </Text>
              <Text style={styles.identityPresence}>{PRESENCE_LABEL[state]}</Text>
            </View>
          </View>
          <Pressable
            style={styles.profileButton}
            onPress={openSettings}
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
              <LayeredAvatar
                avatarId={displayAvatarId}
                state={state}
                sleeping={sleeping}
                disabled={disabled}
                onLoadError={() => setAssetsFailed(true)}
              />
            )}
          </Pressable>
        </View>

        <ConversationPeek transcript={transcript} reply={reply} state={state} />
        <View style={styles.visualArea}>
          <VisualInputButton
            disabled={disabled || state === 'thinking'}
            status={visualStatus}
            onPress={() => setShowVisualSources(true)}
          />
          <PendingVisualPreview
            pending={pendingVisual}
            uploadingUri={visualUploadingUri}
            status={visualStatus}
            error={visualError}
            onRemove={() => void removePendingVisual()}
          />
        </View>
        {error && <Text style={styles.error}>{error}</Text>}

        <VoiceControlDock
          state={state}
          onSettingsPress={openSettings}
          onHistoryPress={openHistory}
          onMicPress={forceFinalize}
          onMicLongPress={() => void pauseConversation()}
          historyDisabled={historyDisabled}
        />
      </View>
      <VisualSourceSheet
        visible={showVisualSources}
        onClose={() => setShowVisualSources(false)}
        onSelect={selectVisualSource}
      />
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
  visualArea: {
    gap: 4,
  },
});
