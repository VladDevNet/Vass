import { useEffect, useState } from 'react';
import { ActivityIndicator, StyleSheet, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { AuthProvider, useAuth } from './src/context/AuthContext';
import { dismissOnboarding, isOnboardingDismissed } from './src/api/client';
import { LoginScreen } from './src/screens/LoginScreen';
import { HomeScreen } from './src/screens/HomeScreen';
import { ProfileScreen } from './src/screens/ProfileScreen';
import { amoled } from './src/theme/amoled';

function Root() {
  const { isLoading, user, displayName } = useAuth();
  // null = not checked yet this session (only matters once a user exists —
  // see the loading-gate below, which waits on this too so we don't flash
  // onboarding for a split second before the dismissed flag loads).
  const [onboardingDismissed, setOnboardingDismissed] = useState<boolean | null>(null);

  useEffect(() => {
    // Reset synchronously on logout — SecureStore's dismissed flag is one
    // device-wide key, not scoped per-account (this app explicitly supports
    // a shared device switching between family members via device-link
    // login). Without this, a stale `true` from the PREVIOUS user survives
    // until this effect's async check resolves for the NEW one, so the
    // loading gate below (which only waits while dismissed === null) skips
    // straight past it — a brand-new user who's never seen onboarding could
    // land on HomeScreen instead, for at least one render.
    if (!user) {
      setOnboardingDismissed(null);
      return;
    }
    isOnboardingDismissed().then(setOnboardingDismissed);
  }, [user]);

  async function handleOnboardingDone() {
    // Persisted unconditionally, whether they saved a name or skipped —
    // if they saved, displayName is now set anyway so this check never
    // matters again; if they skipped, this is what stops it from
    // re-prompting on every future launch.
    await dismissOnboarding();
    setOnboardingDismissed(true);
  }

  if (isLoading || (user && onboardingDismissed === null)) {
    return (
      <View style={styles.loading}>
        <StatusBar style="light" />
        <ActivityIndicator size="large" color={amoled.textPrimary} />
      </View>
    );
  }

  if (!user) return <LoginScreen />;
  if (!displayName && !onboardingDismissed) {
    return <ProfileScreen mode="onboarding" onDone={handleOnboardingDone} />;
  }
  return <HomeScreen />;
}

export default function App() {
  return (
    <SafeAreaProvider>
      <AuthProvider>
        <Root />
        <StatusBar style="auto" />
      </AuthProvider>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  loading: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    backgroundColor: amoled.background,
  },
});
