import { useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { useAuth } from '../context/AuthContext';
import { RegistrationPendingScreen } from './RegistrationPendingScreen';

type Mode = 'login' | 'register' | 'code';

export function LoginScreen() {
  const { login, register, loginWithDeviceCode, approvalPendingEmail, dismissApprovalPending } = useAuth();
  const [mode, setMode] = useState<Mode>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  if (approvalPendingEmail !== null) {
    return <RegistrationPendingScreen email={approvalPendingEmail} onBack={dismissApprovalPending} />;
  }

  async function handleSubmit() {
    setError(null);
    setIsSubmitting(true);
    try {
      if (mode === 'register') {
        await register(email.trim(), password);
      } else if (mode === 'code') {
        await loginWithDeviceCode(code.trim());
      } else {
        await login(email.trim(), password);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <KeyboardAvoidingView
      style={styles.container}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
    >
      <Text style={styles.title}>AI Voice Assistant</Text>
      <Text style={styles.subtitle}>
        {mode === 'register' && 'Создать аккаунт'}
        {mode === 'login' && 'Вход'}
        {mode === 'code' && 'Вход по коду с другого устройства'}
      </Text>

      {mode === 'code' ? (
        <>
          <Text style={styles.codeHint}>
            Попросите родственника открыть на своём телефоне «Показать код для
            нового устройства» и назвать код
          </Text>
          <TextInput
            style={[styles.input, styles.codeInput]}
            placeholder="000000"
            keyboardType="number-pad"
            maxLength={6}
            value={code}
            onChangeText={setCode}
          />
        </>
      ) : (
        <>
          <TextInput
            style={styles.input}
            placeholder="Email"
            autoCapitalize="none"
            keyboardType="email-address"
            value={email}
            onChangeText={setEmail}
          />
          <TextInput
            style={styles.input}
            placeholder="Пароль"
            secureTextEntry
            autoCapitalize="none"
            value={password}
            onChangeText={setPassword}
          />
        </>
      )}

      {error && <Text style={styles.error}>{error}</Text>}

      <Pressable
        style={[styles.button, isSubmitting && styles.buttonDisabled]}
        onPress={handleSubmit}
        disabled={isSubmitting}
      >
        {isSubmitting ? (
          <ActivityIndicator color="#fff" />
        ) : (
          <Text style={styles.buttonText}>
            {mode === 'register' && 'Зарегистрироваться'}
            {mode === 'login' && 'Войти'}
            {mode === 'code' && 'Войти по коду'}
          </Text>
        )}
      </Pressable>

      {mode !== 'login' && (
        <Pressable onPress={() => setMode('login')}>
          <Text style={styles.switchModeText}>Уже есть аккаунт? Войти</Text>
        </Pressable>
      )}
      {mode !== 'register' && (
        <Pressable onPress={() => setMode('register')}>
          <Text style={styles.switchModeText}>Нет аккаунта? Зарегистрироваться</Text>
        </Pressable>
      )}
      {mode !== 'code' && (
        <Pressable onPress={() => setMode('code')}>
          <Text style={styles.switchModeText}>Есть код с другого устройства?</Text>
        </Pressable>
      )}
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    justifyContent: 'center',
    padding: 24,
    backgroundColor: '#fff',
  },
  title: {
    fontSize: 26,
    fontWeight: '700',
    textAlign: 'center',
    marginBottom: 4,
  },
  subtitle: {
    fontSize: 16,
    textAlign: 'center',
    color: '#666',
    marginBottom: 32,
  },
  codeHint: {
    fontSize: 14,
    color: '#666',
    textAlign: 'center',
    marginBottom: 16,
  },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 10,
    padding: 14,
    fontSize: 16,
    color: '#111827',
    marginBottom: 12,
  },
  codeInput: {
    fontSize: 28,
    textAlign: 'center',
    letterSpacing: 8,
  },
  error: {
    color: '#c0392b',
    marginBottom: 12,
    textAlign: 'center',
  },
  button: {
    backgroundColor: '#4a6fa5',
    borderRadius: 10,
    padding: 16,
    alignItems: 'center',
    marginTop: 8,
  },
  buttonDisabled: {
    opacity: 0.6,
  },
  buttonText: {
    color: '#fff',
    fontSize: 17,
    fontWeight: '600',
  },
  switchModeText: {
    textAlign: 'center',
    color: '#4a6fa5',
    marginTop: 16,
    fontSize: 15,
  },
});
