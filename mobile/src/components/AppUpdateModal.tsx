import { Modal, Pressable, StyleSheet, Text, View } from 'react-native';
import { Download, RefreshCw } from 'lucide-react-native';
import type { AndroidAppUpdate } from '../api/client';
import { amoled } from '../theme/amoled';

interface AppUpdateModalProps {
  update: AndroidAppUpdate | null;
  isLaunching: boolean;
  error: string | null;
  onInstall: () => void;
  onSkip: () => void;
}

export function AppUpdateModal({ update, isLaunching, error, onInstall, onSkip }: AppUpdateModalProps) {
  const mandatory = update?.mandatory ?? false;

  return (
    <Modal
      transparent
      visible={update !== null}
      animationType="fade"
      statusBarTranslucent
      onRequestClose={mandatory ? () => undefined : onSkip}
    >
      <View style={styles.backdrop}>
        {!mandatory && (
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={onSkip}
            accessibilityLabel="Отложить обновление"
          />
        )}
        <View style={styles.dialog} accessibilityViewIsModal>
          <View style={styles.iconWrap}>
            {mandatory ? (
              <RefreshCw size={29} color="#FBBF24" strokeWidth={2.2} />
            ) : (
              <Download size={29} color="#93C5FD" strokeWidth={2.2} />
            )}
          </View>
          <Text style={styles.title}>{mandatory ? 'Нужно обновить Vass' : 'Доступно обновление Vass'}</Text>
          <Text style={styles.version}>Версия {update?.latestVersion}</Text>
          <Text style={styles.description}>
            {mandatory
              ? 'Эта версия приложения больше не поддерживается. Обновите Vass, чтобы продолжить.'
              : 'Можно обновиться сейчас или продолжить с текущей версией.'}
          </Text>
          {update?.releaseNotes ? <Text style={styles.notes}>{update.releaseNotes}</Text> : null}
          {error ? <Text style={styles.error}>{error}</Text> : null}
          <Pressable
            style={[styles.primaryButton, isLaunching && styles.primaryButtonDisabled]}
            onPress={onInstall}
            disabled={isLaunching}
            accessibilityRole="button"
            accessibilityLabel="Скачать и установить обновление"
          >
            <Download size={19} color="#07111F" strokeWidth={2.5} />
            <Text style={styles.primaryButtonText}>{isLaunching ? 'Открываем обновление...' : 'Скачать и установить'}</Text>
          </Pressable>
          {!mandatory && (
            <Pressable
              style={styles.skipButton}
              onPress={onSkip}
              disabled={isLaunching}
              accessibilityRole="button"
              accessibilityLabel="Отложить обновление"
            >
              <Text style={styles.skipButtonText}>Позже</Text>
            </Pressable>
          )}
          <Text style={styles.systemNote}>Android попросит подтвердить установку.</Text>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: {
    flex: 1,
    justifyContent: 'center',
    paddingHorizontal: 24,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  dialog: {
    backgroundColor: '#111827',
    borderWidth: 1,
    borderColor: amoled.glassBorder,
    borderRadius: 8,
    paddingHorizontal: 24,
    paddingTop: 26,
    paddingBottom: 20,
  },
  iconWrap: {
    width: 54,
    height: 54,
    borderRadius: 27,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(59,130,246,0.16)',
    marginBottom: 18,
  },
  title: {
    color: amoled.textPrimary,
    fontSize: 21,
    fontWeight: '700',
  },
  version: {
    color: '#93C5FD',
    fontSize: 15,
    fontWeight: '600',
    marginTop: 7,
  },
  description: {
    color: amoled.textSecondary,
    fontSize: 16,
    lineHeight: 23,
    marginTop: 14,
  },
  notes: {
    color: amoled.textPrimary,
    fontSize: 15,
    lineHeight: 22,
    marginTop: 14,
  },
  error: {
    color: '#FCA5A5',
    fontSize: 14,
    lineHeight: 20,
    marginTop: 14,
  },
  primaryButton: {
    minHeight: 52,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 10,
    backgroundColor: '#BFDBFE',
    borderRadius: 7,
    marginTop: 24,
    paddingHorizontal: 16,
  },
  primaryButtonDisabled: {
    opacity: 0.65,
  },
  primaryButtonText: {
    color: '#07111F',
    fontSize: 16,
    fontWeight: '700',
  },
  skipButton: {
    minHeight: 46,
    alignItems: 'center',
    justifyContent: 'center',
    marginTop: 4,
  },
  skipButtonText: {
    color: amoled.textSecondary,
    fontSize: 16,
    fontWeight: '600',
  },
  systemNote: {
    color: '#64748B',
    fontSize: 13,
    lineHeight: 18,
    textAlign: 'center',
    marginTop: 3,
  },
});
