import { useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useAuth } from '../context/AuthContext';
import { api } from '../api/client';

// Placeholder for Phase 1 skeleton — the real voice screen (VAD, turn-taking,
// on-device TTS) lands in later BACKLOG.md phases. This just proves auth +
// API wiring works end to end on-device.
export function HomeScreen() {
  const { user, logout } = useAuth();
  const [deviceCode, setDeviceCode] = useState<string | null>(null);
  const [isGenerating, setIsGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleShowDeviceCode() {
    setError(null);
    setIsGenerating(true);
    try {
      const { code } = await api.createDeviceLink();
      setDeviceCode(code);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsGenerating(false);
    }
  }

  return (
    <View style={styles.container}>
      <Text style={styles.greeting}>Привет, {user?.email}</Text>
      <Text style={styles.hint}>
        Голосовой экран появится в следующих этапах — см. docs/react-native/BACKLOG.md
      </Text>

      {deviceCode ? (
        <View style={styles.codeBox}>
          <Text style={styles.codeLabel}>Код действителен 10 минут:</Text>
          <Text style={styles.codeValue}>{deviceCode}</Text>
          <Text style={styles.codeHint}>
            Введите его на новом устройстве в разделе «Есть код с другого устройства?»
          </Text>
        </View>
      ) : (
        <Pressable
          style={styles.linkButton}
          onPress={handleShowDeviceCode}
          disabled={isGenerating}
        >
          <Text style={styles.linkButtonText}>
            {isGenerating ? 'Создаю код…' : 'Показать код для нового устройства'}
          </Text>
        </Pressable>
      )}
      {error && <Text style={styles.error}>{error}</Text>}

      <Pressable style={styles.logoutButton} onPress={logout}>
        <Text style={styles.logoutText}>Выйти</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    padding: 24,
    backgroundColor: '#fff',
  },
  greeting: {
    fontSize: 22,
    fontWeight: '600',
    marginBottom: 12,
    textAlign: 'center',
  },
  hint: {
    fontSize: 14,
    color: '#666',
    textAlign: 'center',
    marginBottom: 32,
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
