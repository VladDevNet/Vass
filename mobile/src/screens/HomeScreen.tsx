import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useAuth } from '../context/AuthContext';

// Placeholder for Phase 1 skeleton — the real voice screen (VAD, turn-taking,
// on-device TTS) lands in later BACKLOG.md phases. This just proves auth +
// API wiring works end to end on-device.
export function HomeScreen() {
  const { user, logout } = useAuth();

  return (
    <View style={styles.container}>
      <Text style={styles.greeting}>Привет, {user?.email}</Text>
      <Text style={styles.hint}>
        Голосовой экран появится в следующих этапах — см. docs/react-native/BACKLOG.md
      </Text>
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
