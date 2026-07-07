import { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import type { Voice } from 'expo-speech';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';
import { getResolvedVoiceId, listRussianVoices, previewVoice, setVoicePreference } from '../tts/systemSpeech';

interface ProfileScreenProps {
  mode: 'onboarding' | 'settings';
  // Called once the user is done here, whether by saving a name or (in
  // onboarding mode) skipping — App.tsx's Root owns what "done" means
  // (settings just closes; onboarding also persists dismissal so a skip
  // doesn't re-prompt on every future launch).
  onDone: () => void;
}

export function ProfileScreen({ mode, onDone }: ProfileScreenProps) {
  const { displayName, refreshDisplayName } = useAuth();
  const [name, setName] = useState(displayName ?? '');
  const [savingName, setSavingName] = useState(false);
  const [nameError, setNameError] = useState<string | null>(null);

  // undefined = still loading, null = no Russian voice on this device.
  const [voices, setVoices] = useState<Voice[] | undefined>(undefined);
  const [selectedVoiceId, setSelectedVoiceId] = useState<string | null>(null);
  const [voicesError, setVoicesError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [list, resolved] = await Promise.all([listRussianVoices(), getResolvedVoiceId()]);
        if (cancelled) return;
        setVoices(list);
        setSelectedVoiceId(resolved);
      } catch (err) {
        if (!cancelled) setVoicesError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleSaveName() {
    const trimmed = name.trim();
    if (!trimmed) {
      setNameError('Введите имя');
      return;
    }
    setNameError(null);
    setSavingName(true);
    try {
      await api.updateDisplayName(trimmed);
      await refreshDisplayName();
      onDone();
    } catch (err) {
      setNameError(err instanceof Error ? err.message : String(err));
    } finally {
      setSavingName(false);
    }
  }

  function handleSelectVoice(voice: Voice) {
    setSelectedVoiceId(voice.identifier);
    setVoicePreference(voice.identifier);
    previewVoice(voice.identifier);
  }

  return (
    <ScrollView contentContainerStyle={styles.container}>
      <Text style={styles.title}>{mode === 'onboarding' ? 'Давайте познакомимся' : 'Настройки'}</Text>
      {mode === 'onboarding' && (
        <Text style={styles.subtitle}>Это можно изменить позже в настройках</Text>
      )}

      <Text style={styles.label}>Как вас называть?</Text>
      <TextInput
        style={styles.input}
        placeholder="Ваше имя"
        value={name}
        onChangeText={setName}
      />
      {nameError && <Text style={styles.error}>{nameError}</Text>}
      <Pressable
        style={[styles.button, savingName && styles.buttonDisabled]}
        onPress={handleSaveName}
        disabled={savingName}
      >
        {savingName ? (
          <ActivityIndicator color="#fff" />
        ) : (
          <Text style={styles.buttonText}>Сохранить</Text>
        )}
      </Pressable>

      <View style={styles.divider} />

      <Text style={styles.label}>Голос ассистента</Text>
      <Text style={styles.hint}>Нажмите на голос, чтобы его послушать и выбрать</Text>
      {voicesError && <Text style={styles.error}>{voicesError}</Text>}
      {voices === undefined && !voicesError && <ActivityIndicator style={styles.voicesLoading} />}
      {voices?.length === 0 && (
        <Text style={styles.hint}>На устройстве не найдено русских голосов.</Text>
      )}
      {voices?.map((voice) => {
        const selected = voice.identifier === selectedVoiceId;
        return (
          <Pressable
            key={voice.identifier}
            style={[styles.voiceRow, selected && styles.voiceRowSelected]}
            onPress={() => handleSelectVoice(voice)}
          >
            <Text style={[styles.voiceName, selected && styles.voiceNameSelected]}>{voice.name}</Text>
            {selected && <Text style={styles.voiceCheck}>✓</Text>}
          </Pressable>
        );
      })}

      <Pressable onPress={onDone} style={styles.doneLink}>
        <Text style={styles.doneLinkText}>{mode === 'onboarding' ? 'Пропустить' : 'Назад'}</Text>
      </Pressable>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: {
    flexGrow: 1,
    padding: 24,
    paddingTop: 48,
    backgroundColor: '#fff',
  },
  title: {
    fontSize: 24,
    fontWeight: '700',
    textAlign: 'center',
    marginBottom: 4,
  },
  subtitle: {
    fontSize: 15,
    color: '#666',
    textAlign: 'center',
    marginBottom: 24,
  },
  label: {
    fontSize: 16,
    fontWeight: '600',
    marginBottom: 8,
  },
  hint: {
    fontSize: 13,
    color: '#666',
    marginBottom: 12,
  },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 10,
    padding: 14,
    fontSize: 16,
    marginBottom: 12,
  },
  error: {
    color: '#c0392b',
    marginBottom: 12,
  },
  button: {
    backgroundColor: '#4a6fa5',
    borderRadius: 10,
    padding: 16,
    alignItems: 'center',
  },
  buttonDisabled: {
    opacity: 0.6,
  },
  buttonText: {
    color: '#fff',
    fontSize: 17,
    fontWeight: '600',
  },
  divider: {
    height: 1,
    backgroundColor: '#eee',
    marginVertical: 28,
  },
  voicesLoading: {
    marginBottom: 12,
  },
  voiceRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 10,
    padding: 14,
    marginBottom: 10,
  },
  voiceRowSelected: {
    borderColor: '#4a6fa5',
    backgroundColor: '#f0f4fa',
  },
  voiceName: {
    fontSize: 16,
  },
  voiceNameSelected: {
    color: '#4a6fa5',
    fontWeight: '600',
  },
  voiceCheck: {
    color: '#4a6fa5',
    fontSize: 18,
    fontWeight: '700',
  },
  doneLink: {
    marginTop: 24,
    marginBottom: 12,
  },
  doneLinkText: {
    textAlign: 'center',
    color: '#4a6fa5',
    fontSize: 15,
  },
});
