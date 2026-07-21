import { useCallback, useEffect, useRef, useState } from 'react';
import { AppState, Platform, Pressable, StyleSheet, Switch, Text, View } from 'react-native';
import {
  VassOverlay,
  type OverlayAvatarId,
  type OverlayStatus,
} from '../../modules/vass-overlay';
import { api } from '../api/client';
import { useConversationRuntime } from '../context/ConversationRuntimeContext';

const EMPTY_STATUS: OverlayStatus = {
  available: false,
  permissionGranted: false,
  enabled: false,
  running: false,
};

export function OverlaySettings({ avatarId }: { avatarId: OverlayAvatarId }) {
  const { prepareOverlayMode, disableOverlayMode } = useConversationRuntime();
  const [status, setStatus] = useState<OverlayStatus>(EMPTY_STATUS);
  const [showDisclosure, setShowDisclosure] = useState(false);
  const [showRestrictedSettingsHelp, setShowRestrictedSettingsHelp] = useState(false);
  const [waitingForPermission, setWaitingForPermission] = useState(false);
  const waitingForPermissionRef = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refreshStatus = useCallback(async () => {
    const next = await VassOverlay.getStatus();
    setStatus(next);
    return next;
  }, []);

  const setPermissionWait = useCallback((waiting: boolean) => {
    waitingForPermissionRef.current = waiting;
    setWaitingForPermission(waiting);
  }, []);

  const startOverlay = useCallback(async () => {
    setBusy(true);
    setError(null);
    let backgroundPrepared = false;
    try {
      await prepareOverlayMode();
      backgroundPrepared = true;
      await VassOverlay.start({ state: 'idle', avatarId, enabled: true }, AppState.currentState === 'active');
      setShowDisclosure(false);
      setShowRestrictedSettingsHelp(false);
      setPermissionWait(false);
      await new Promise((resolve) => setTimeout(resolve, 250));
      const next = await refreshStatus();
      if (next.enabled && next.running) {
        void api.recordCapabilityUsage('overlay').catch(() => undefined);
      }
    } catch (err) {
      if (backgroundPrepared) await disableOverlayMode(true);
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }, [avatarId, disableOverlayMode, prepareOverlayMode, refreshStatus, setPermissionWait]);

  useEffect(() => {
    void refreshStatus();
  }, [refreshStatus]);

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (nextState) => {
      if (nextState !== 'active') return;
      void (async () => {
        const next = await refreshStatus();
        if (waitingForPermissionRef.current && next.permissionGranted) await startOverlay();
        if (waitingForPermissionRef.current && !next.permissionGranted) {
          setPermissionWait(false);
          setShowRestrictedSettingsHelp(true);
        }
      })();
    });
    return () => subscription.remove();
  }, [refreshStatus, setPermissionWait, startOverlay]);

  async function handleToggle(enabled: boolean) {
    if (!enabled) {
      setBusy(true);
      setError(null);
      try {
        await disableOverlayMode(true);
        await VassOverlay.stop();
        setShowDisclosure(false);
        setShowRestrictedSettingsHelp(false);
        setPermissionWait(false);
        await refreshStatus();
      } finally {
        setBusy(false);
      }
      return;
    }

    if (status.permissionGranted) {
      await startOverlay();
    } else {
      setShowRestrictedSettingsHelp(false);
      setShowDisclosure(true);
    }
  }

  async function openPermissionSettings() {
    setError(null);
    setShowRestrictedSettingsHelp(false);
    setPermissionWait(true);
    try {
      await VassOverlay.requestOverlayPermission();
    } catch (err) {
      setPermissionWait(false);
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function openAppDetails() {
    setError(null);
    try {
      await VassOverlay.openAppDetails();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  if (Platform.OS !== 'android') return null;

  return (
    <View>
      <View style={styles.headingRow}>
        <View style={styles.headingCopy}>
          <Text style={styles.label}>Поверх других приложений</Text>
          <Text style={styles.hint}>Небольшой аватар остаётся рядом, когда Vass свёрнут.</Text>
        </View>
        <Switch
          value={status.enabled && status.running}
          onValueChange={(value) => void handleToggle(value)}
          disabled={busy || !status.available}
          trackColor={{ false: '#CBD5E1', true: '#2563EB' }}
          thumbColor="#FFFFFF"
          accessibilityLabel="Плавающий аватар поверх приложений"
        />
      </View>

      {!status.available && (
        <Text style={styles.hint}>Функция доступна в Android-сборке Vass на Android 8 и новее.</Text>
      )}

      {status.enabled && !status.running && status.permissionGranted && (
        <Pressable style={styles.secondaryButton} onPress={() => void startOverlay()} disabled={busy}>
          <Text style={styles.secondaryButtonText}>Запустить снова</Text>
        </Pressable>
      )}

      {showDisclosure && (
        <View style={styles.disclosure}>
          <Text style={styles.disclosureTitle}>Что изменится</Text>
          <Text style={styles.disclosureText}>
            Android покажет круглый аватар Vass поверх браузера, YouTube и других приложений. Его можно
            передвигать, двойным нажатием возвращаться в Vass, а остановить режим можно здесь или из
            постоянного уведомления.
          </Text>
          <Text style={styles.disclosureText}>
            Пока режим включён, тот же голосовой разговор продолжается в фоне, а Android показывает
            постоянное уведомление о работе микрофона. Окно не читает экран: его разбор появится
            отдельно и всегда будет требовать явного системного подтверждения.
          </Text>
          <Pressable
            style={[styles.primaryButton, busy && styles.buttonDisabled]}
            onPress={() => void openPermissionSettings()}
            disabled={busy || waitingForPermission}
          >
            <Text style={styles.primaryButtonText}>
              {waitingForPermission ? 'Ожидаю разрешение…' : 'Открыть настройки Android'}
            </Text>
          </Pressable>
          <Pressable style={styles.cancelButton} onPress={() => setShowDisclosure(false)}>
            <Text style={styles.cancelText}>Не сейчас</Text>
          </Pressable>
        </View>
      )}

      {showRestrictedSettingsHelp && (
        <View style={styles.restrictedHelp}>
          <Text style={styles.disclosureTitle}>Android заблокировал разрешение</Text>
          <Text style={styles.disclosureText}>
            Для APK, установленного вручную, Android требует ещё один шаг. Откройте карточку Vass,
            нажмите меню ⋮ справа сверху и выберите «Разрешить ограниченные настройки». После этого
            вернитесь сюда и включите режим ещё раз.
          </Text>
          <Pressable style={styles.primaryButton} onPress={() => void openAppDetails()}>
            <Text style={styles.primaryButtonText}>Открыть карточку Vass</Text>
          </Pressable>
          <Pressable style={styles.cancelButton} onPress={() => setShowRestrictedSettingsHelp(false)}>
            <Text style={styles.cancelText}>Закрыть</Text>
          </Pressable>
        </View>
      )}

      {error && <Text style={styles.error}>{error}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  headingRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 16,
  },
  headingCopy: {
    flex: 1,
  },
  label: {
    fontSize: 16,
    fontWeight: '600',
    marginBottom: 6,
    color: '#111827',
  },
  hint: {
    fontSize: 13,
    color: '#64748B',
    lineHeight: 19,
  },
  disclosure: {
    marginTop: 18,
    paddingTop: 18,
    borderTopWidth: 1,
    borderTopColor: '#E2E8F0',
  },
  restrictedHelp: {
    marginTop: 18,
    paddingTop: 18,
    borderTopWidth: 1,
    borderTopColor: '#E2E8F0',
  },
  disclosureTitle: {
    fontSize: 17,
    fontWeight: '700',
    color: '#111827',
    marginBottom: 10,
  },
  disclosureText: {
    fontSize: 14,
    color: '#475569',
    lineHeight: 21,
    marginBottom: 12,
  },
  primaryButton: {
    backgroundColor: '#2563EB',
    borderRadius: 8,
    paddingVertical: 14,
    paddingHorizontal: 16,
    alignItems: 'center',
    marginTop: 4,
  },
  primaryButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontWeight: '700',
  },
  secondaryButton: {
    borderWidth: 1,
    borderColor: '#2563EB',
    borderRadius: 8,
    paddingVertical: 11,
    alignItems: 'center',
    marginTop: 14,
  },
  secondaryButtonText: {
    color: '#2563EB',
    fontSize: 14,
    fontWeight: '700',
  },
  cancelButton: {
    paddingVertical: 13,
    alignItems: 'center',
  },
  cancelText: {
    color: '#64748B',
    fontSize: 14,
  },
  buttonDisabled: {
    opacity: 0.55,
  },
  error: {
    color: '#B91C1C',
    marginTop: 12,
  },
});
