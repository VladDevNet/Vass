import { Pressable, StyleSheet, Text, View } from 'react-native';
import { Clock3, ShieldCheck } from 'lucide-react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

interface RegistrationPendingScreenProps {
  email: string | null;
  onBack: () => void;
}

export function RegistrationPendingScreen({ email, onBack }: RegistrationPendingScreenProps) {
  return (
    <SafeAreaView style={styles.screen} edges={['top', 'bottom']}>
      <View style={styles.content}>
        <View style={styles.iconWrap}>
          <Clock3 size={36} color="#4a6fa5" />
        </View>
        <Text style={styles.title}>Заявка отправлена</Text>
        <Text style={styles.body}>
          Доступ к Vass откроется после подтверждения администратором.
        </Text>
        {!!email && <Text style={styles.email}>{email}</Text>}
        <View style={styles.note}>
          <ShieldCheck size={20} color="#4a6fa5" />
          <Text style={styles.noteText}>Не нужно регистрироваться повторно. После подтверждения войдите с тем же email и паролем.</Text>
        </View>
        <Pressable style={styles.button} onPress={onBack} accessibilityLabel="Вернуться ко входу">
          <Text style={styles.buttonText}>Вернуться ко входу</Text>
        </Pressable>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: '#fff' },
  content: { flex: 1, justifyContent: 'center', padding: 28 },
  iconWrap: { width: 72, height: 72, borderRadius: 36, backgroundColor: '#f0f4fa', alignItems: 'center', justifyContent: 'center', alignSelf: 'center', marginBottom: 24 },
  title: { color: '#111827', fontSize: 26, fontWeight: '700', textAlign: 'center', marginBottom: 12 },
  body: { color: '#4b5563', fontSize: 17, lineHeight: 24, textAlign: 'center' },
  email: { color: '#111827', fontSize: 16, fontWeight: '600', textAlign: 'center', marginTop: 16 },
  note: { flexDirection: 'row', gap: 10, padding: 16, marginTop: 28, borderRadius: 8, backgroundColor: '#f7f9fc' },
  noteText: { flex: 1, color: '#4b5563', fontSize: 14, lineHeight: 20 },
  button: { marginTop: 28, minHeight: 52, borderRadius: 8, backgroundColor: '#4a6fa5', alignItems: 'center', justifyContent: 'center' },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '700' },
});
