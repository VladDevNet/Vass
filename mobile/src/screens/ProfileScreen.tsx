import { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Image,
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
import {
  getResolvedVoiceId,
  getVoiceGenderTags,
  isLikelyLocalVoice,
  listRussianVoices,
  previewVoice,
  setVoiceGender,
  setVoicePreference,
  type VoiceGender,
} from '../tts/systemSpeech';
import type { AvatarId } from '../components/LayeredAvatar';
import { OverlaySettings } from '../components/OverlaySettings';
import { BellRing, BookOpen, BrainCircuit, CircleHelp } from 'lucide-react-native';

interface ProfileScreenProps {
  mode: 'onboarding' | 'settings';
  // Called once the user is done here, whether by saving a name or (in
  // onboarding mode) skipping — App.tsx's Root owns what "done" means
  // (settings just closes; onboarding also persists dismissal so a skip
  // doesn't re-prompt on every future launch).
  onDone: () => void;
  onOpenMemory?: () => void;
  onOpenReminders?: () => void;
  onOpenHelp?: () => void;
  onOpenLibrary?: () => void;
}

export function ProfileScreen({ mode, onDone, onOpenMemory, onOpenReminders, onOpenHelp, onOpenLibrary }: ProfileScreenProps) {
  const { displayName, assistantName, avatarId, refreshProfile, logout } = useAuth();
  const [name, setName] = useState(displayName ?? '');
  const [assistantNameInput, setAssistantNameInput] = useState(assistantName ?? '');
  const [savingName, setSavingName] = useState(false);
  const [nameError, setNameError] = useState<string | null>(null);

  // undefined = still loading, null = no Russian voice on this device.
  const [voices, setVoices] = useState<Voice[] | undefined>(undefined);
  const [selectedVoiceId, setSelectedVoiceId] = useState<string | null>(null);
  const [voicesError, setVoicesError] = useState<string | null>(null);

  const [genderTags, setGenderTags] = useState<Record<string, VoiceGender>>({});
  const [avatarSwitchError, setAvatarSwitchError] = useState<string | null>(null);

  const [deviceCode, setDeviceCode] = useState<string | null>(null);
  const [isGeneratingCode, setIsGeneratingCode] = useState(false);
  const [linkError, setLinkError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [list, resolved, tags] = await Promise.all([
          listRussianVoices(),
          getResolvedVoiceId(),
          getVoiceGenderTags(),
        ]);
        if (cancelled) return;
        setVoices(list);
        setSelectedVoiceId(resolved);
        setGenderTags(tags);
      } catch (err) {
        if (!cancelled) setVoicesError(err instanceof Error ? err.message : String(err));
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function handleSaveNames() {
    const trimmed = name.trim();
    if (!trimmed) {
      setNameError('Введите имя');
      return;
    }
    setNameError(null);
    setSavingName(true);
    try {
      await api.updateNames(trimmed, assistantNameInput);
      await refreshProfile();
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

  function handleTagVoiceGender(identifier: string, gender: VoiceGender) {
    setGenderTags((prev) => ({ ...prev, [identifier]: gender }));
    void setVoiceGender(identifier, gender);
  }

  async function handleSelectAvatar(id: AvatarId) {
    setAvatarSwitchError(null);
    try {
      await api.updateAvatarId(id);
      await refreshProfile();
    } catch (err) {
      setAvatarSwitchError(err instanceof Error ? err.message : String(err));
      return;
    }
    // Переключение голоса — best-effort поверх уже успешно сохранённого
    // аватара: если голосов нужного пола ещё нет, аватар всё равно
    // переключился, просто голос не трогаем (см. spec — «группа пустая с
    // подсказкой», не блокирующая ошибка).
    const targetGender: VoiceGender = id === 'male' ? 'male' : 'female';
    const match = (voices ?? []).find((v) => genderTags[v.identifier] === targetGender);
    if (match) {
      setSelectedVoiceId(match.identifier);
      void setVoicePreference(match.identifier);
      previewVoice(match.identifier);
    }
  }

  async function handleShowDeviceCode() {
    setLinkError(null);
    setIsGeneratingCode(true);
    try {
      const { code } = await api.createDeviceLink();
      setDeviceCode(code);
    } catch (err) {
      setLinkError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsGeneratingCode(false);
    }
  }

  function handleLogout() {
    Alert.alert(
      'Выйти из Vass?',
      'Напоминания этого аккаунта перестанут срабатывать на телефоне до следующего входа. Серии «каждые N минут/часов» потребуется создать заново.',
      [
        { text: 'Остаться', style: 'cancel' },
        { text: 'Выйти', style: 'destructive', onPress: () => { void logout(); } },
      ],
    );
  }

  // Android's Google TTS engine doesn't give voices a human name (raw
  // identifiers look like "ru-ru-x-rud-network" — see systemSpeech.ts's
  // isLikelyLocalVoice comment) and exposes no reliable gender info either,
  // so listening is the only way to tell voices apart — hence plain
  // ordinal labels below instead of anything invented. Prefer local voices
  // (the whole point of this feature is avoiding a network round-trip);
  // only fall back to showing network-dependent ones if a device genuinely
  // has no local Russian voice, so the picker is never empty.
  const localVoices = voices?.filter((v) => isLikelyLocalVoice(v.identifier)) ?? [];
  const displayVoices = voices === undefined ? undefined : localVoices.length > 0 ? localVoices : voices;
  const showingNetworkVoices = displayVoices?.some((v) => !isLikelyLocalVoice(v.identifier)) ?? false;
  const hasCustomAssistantName = assistantNameInput.trim().length > 0;

  const maleVoices = displayVoices?.filter((v) => genderTags[v.identifier] === 'male') ?? [];
  const femaleVoices = displayVoices?.filter((v) => genderTags[v.identifier] === 'female') ?? [];
  const untaggedVoices = displayVoices?.filter((v) => !genderTags[v.identifier]) ?? [];

  function renderVoiceGroup(label: string, groupVoices: Voice[]) {
    if (groupVoices.length === 0) return null;
    return (
      <View key={label}>
        <Text style={styles.voiceGroupLabel}>{label}</Text>
        {groupVoices.map((voice, index) => {
          const selected = voice.identifier === selectedVoiceId;
          const needsNetwork = !isLikelyLocalVoice(voice.identifier);
          const tag = genderTags[voice.identifier];
          return (
            <View key={voice.identifier} style={[styles.voiceRow, selected && styles.voiceRowSelected]}>
              <Pressable style={styles.voiceRowMain} onPress={() => handleSelectVoice(voice)}>
                <Text style={[styles.voiceName, selected && styles.voiceNameSelected]}>
                  Голос {index + 1}
                  {needsNetwork ? ' 🌐' : ''}
                </Text>
                {selected && <Text style={styles.voiceCheck}>✓</Text>}
              </Pressable>
              <View style={styles.genderToggle}>
                <Pressable
                  style={[styles.genderButton, tag === 'male' && styles.genderButtonActive]}
                  onPress={() => handleTagVoiceGender(voice.identifier, 'male')}
                >
                  <Text style={[styles.genderButtonText, tag === 'male' && styles.genderButtonTextActive]}>М</Text>
                </Pressable>
                <Pressable
                  style={[styles.genderButton, tag === 'female' && styles.genderButtonActive]}
                  onPress={() => handleTagVoiceGender(voice.identifier, 'female')}
                >
                  <Text style={[styles.genderButtonText, tag === 'female' && styles.genderButtonTextActive]}>Ж</Text>
                </Pressable>
              </View>
            </View>
          );
        })}
      </View>
    );
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

      <Text style={styles.label}>Как назвать ассистента?</Text>
      <Text style={styles.hint}>Необязательно — можно оставить пустым</Text>
      <TextInput
        style={styles.input}
        placeholder="Например, Ольга"
        value={assistantNameInput}
        onChangeText={setAssistantNameInput}
      />

      {nameError && <Text style={styles.error}>{nameError}</Text>}
      <Pressable
        style={[styles.button, savingName && styles.buttonDisabled]}
        onPress={handleSaveNames}
        disabled={savingName}
      >
        {savingName ? (
          <ActivityIndicator color="#fff" />
        ) : (
          <Text style={styles.buttonText}>Сохранить</Text>
        )}
      </Pressable>

      <View style={styles.divider} />

      <Text style={styles.label}>Аватар ассистента</Text>
      <View style={styles.avatarPickerRow}>
        <Pressable
          style={[styles.avatarOption, (avatarId ?? 'olga') !== 'male' && styles.avatarOptionSelected]}
          onPress={() => handleSelectAvatar('olga')}
          accessibilityRole="button"
          accessibilityLabel="Выбрать женский образ ассистента"
          accessibilityState={{ selected: (avatarId ?? 'olga') !== 'male' }}
        >
          <Image source={require('../../assets/avatar/olga_base.png')} style={[styles.avatarThumb, hasCustomAssistantName && styles.avatarThumbWithoutLabel]} />
          {!hasCustomAssistantName && <Text style={styles.hint}>Ольга</Text>}
        </Pressable>
        <Pressable
          style={[styles.avatarOption, avatarId === 'male' && styles.avatarOptionSelected]}
          onPress={() => handleSelectAvatar('male')}
          accessibilityRole="button"
          accessibilityLabel="Выбрать мужской образ ассистента"
          accessibilityState={{ selected: avatarId === 'male' }}
        >
          <Image source={require('../../assets/avatar/male_base.png')} style={[styles.avatarThumb, hasCustomAssistantName && styles.avatarThumbWithoutLabel]} />
          {!hasCustomAssistantName && <Text style={styles.hint}>Максим</Text>}
        </Pressable>
      </View>
      {avatarSwitchError && <Text style={styles.error}>{avatarSwitchError}</Text>}

      {mode === 'settings' && (
        <>
          <View style={styles.divider} />
          <OverlaySettings avatarId={avatarId === 'male' ? 'male' : 'olga'} />
          <View style={styles.divider} />
          <Text style={styles.label}>Память</Text>
          <Text style={styles.hint}>Просматривайте, исправляйте и удаляйте сохраненные записи.</Text>
          <Pressable style={styles.memoryButton} onPress={onOpenMemory}>
            <BrainCircuit size={20} color="#4a6fa5" />
            <Text style={styles.memoryButtonText}>Открыть память</Text>
          </Pressable>
          <Pressable style={[styles.memoryButton, styles.secondaryToolButton]} onPress={onOpenReminders}>
            <BellRing size={20} color="#4a6fa5" />
            <Text style={styles.memoryButtonText}>Напоминания</Text>
          </Pressable>
          <Pressable style={[styles.memoryButton, styles.secondaryToolButton]} onPress={onOpenHelp}>
            <CircleHelp size={20} color="#4a6fa5" />
            <Text style={styles.memoryButtonText}>Возможности Vass</Text>
          </Pressable>
          <Pressable style={[styles.memoryButton, styles.secondaryToolButton]} onPress={onOpenLibrary}>
            <BookOpen size={20} color="#4a6fa5" />
            <Text style={styles.memoryButtonText}>Моя библиотека</Text>
          </Pressable>
        </>
      )}

      <View style={styles.divider} />

      <Text style={styles.label}>Голос ассистента</Text>
      <Text style={styles.hint}>
        У голосов нет названий — нажмите, чтобы послушать и выбрать (так понятно, мужской он или женский)
      </Text>
      {showingNetworkVoices && (
        <Text style={styles.hint}>🌐 — этому голосу нужен интернет, остальные работают без сети</Text>
      )}
      {voicesError && <Text style={styles.error}>{voicesError}</Text>}
      {voices === undefined && !voicesError && <ActivityIndicator style={styles.voicesLoading} />}
      {voices?.length === 0 && (
        <Text style={styles.hint}>На устройстве не найдено русских голосов.</Text>
      )}
      {renderVoiceGroup('Мужские голоса', maleVoices)}
      {renderVoiceGroup('Женские голоса', femaleVoices)}
      {renderVoiceGroup('Не размечено', untaggedVoices)}

      {mode === 'settings' && (
        <>
          <View style={styles.divider} />

          <Text style={styles.label}>Новое устройство</Text>
          {deviceCode ? (
            <View style={styles.codeBox}>
              <Text style={styles.hint}>Код действителен 10 минут:</Text>
              <Text style={styles.codeValue}>{deviceCode}</Text>
              <Text style={styles.hint}>
                Введите его на новом устройстве в разделе «Есть код с другого устройства?»
              </Text>
            </View>
          ) : (
            <Pressable
              style={[styles.button, isGeneratingCode && styles.buttonDisabled]}
              onPress={handleShowDeviceCode}
              disabled={isGeneratingCode}
            >
              {isGeneratingCode ? (
                <ActivityIndicator color="#fff" />
              ) : (
                <Text style={styles.buttonText}>Показать код для нового устройства</Text>
              )}
            </Pressable>
          )}
          {linkError && <Text style={styles.error}>{linkError}</Text>}

          <View style={styles.divider} />

          <Pressable style={styles.logoutButton} onPress={handleLogout}>
            <Text style={styles.logoutText}>Выйти</Text>
          </Pressable>
        </>
      )}

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
  avatarPickerRow: {
    flexDirection: 'row',
    gap: 16,
    marginBottom: 12,
  },
  avatarOption: {
    flex: 1,
    alignItems: 'center',
    padding: 12,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: '#ccc',
  },
  avatarOptionSelected: {
    borderColor: '#4a6fa5',
    backgroundColor: '#f0f4fa',
  },
  avatarThumb: {
    width: 72,
    height: 72,
    borderRadius: 36,
    marginBottom: 8,
  },
  avatarThumbWithoutLabel: {
    marginBottom: 0,
  },
  voiceGroupLabel: {
    fontSize: 14,
    fontWeight: '700',
    color: '#666',
    marginTop: 8,
    marginBottom: 8,
  },
  voiceRowMain: {
    flex: 1,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  genderToggle: {
    flexDirection: 'row',
    gap: 6,
    marginLeft: 12,
  },
  genderButton: {
    width: 32,
    height: 32,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#ccc',
    alignItems: 'center',
    justifyContent: 'center',
  },
  genderButtonActive: {
    borderColor: '#4a6fa5',
    backgroundColor: '#4a6fa5',
  },
  genderButtonText: {
    fontSize: 13,
    fontWeight: '700',
    color: '#666',
  },
  genderButtonTextActive: {
    color: '#fff',
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
  codeBox: {
    alignItems: 'center',
    marginBottom: 16,
    padding: 16,
    borderRadius: 12,
    backgroundColor: '#f0f4fa',
  },
  codeValue: {
    fontSize: 36,
    fontWeight: '700',
    letterSpacing: 8,
    color: '#4a6fa5',
    marginVertical: 6,
  },
  memoryButton: {
    minHeight: 52,
    borderWidth: 1,
    borderColor: '#4a6fa5',
    borderRadius: 10,
    paddingHorizontal: 16,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 10,
  },
  memoryButtonText: {
    color: '#4a6fa5',
    fontSize: 16,
    fontWeight: '600',
  },
  secondaryToolButton: {
    marginTop: 10,
  },
  logoutButton: {
    borderWidth: 1,
    borderColor: '#c0392b',
    borderRadius: 10,
    paddingVertical: 12,
    alignItems: 'center',
  },
  logoutText: {
    color: '#c0392b',
    fontSize: 16,
    fontWeight: '600',
  },
});
