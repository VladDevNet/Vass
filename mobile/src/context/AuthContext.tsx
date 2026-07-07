import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import {
  api,
  getToken,
  loadStoredToken,
  setToken,
  setUnauthorizedHandler,
  type CurrentUser,
} from '../api/client';

interface AuthContextValue {
  isLoading: boolean;
  user: CurrentUser | null;
  // Lives on UserSettings, not CurrentUser (see client.ts's Settings
  // interface) — the name the assistant calls the user by, set during
  // onboarding or later from the settings screen. null means "genuinely
  // not set yet" (drives the onboarding prompt), not "not loaded" —
  // isLoading covers that distinction.
  displayName: string | null;
  refreshDisplayName: () => Promise<void>;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<void>;
  loginWithDeviceCode: (code: string) => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isLoading, setIsLoading] = useState(true);
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [displayName, setDisplayName] = useState<string | null>(null);

  async function refreshDisplayName() {
    try {
      const settings = await api.getSettings();
      setDisplayName(settings.displayName);
    } catch {
      // Best-effort — worst case the onboarding prompt just asks again
      // next time, same as if the name were genuinely never set.
    }
  }

  async function loadUser() {
    setUser(await api.me());
    await refreshDisplayName();
  }

  useEffect(() => {
    // Any 401 anywhere (an expired/revoked token mid-session, not just on
    // the calls made directly from this effect) should drop the app back to
    // LoginScreen instead of leaving the user stuck on HomeScreen retrying
    // a dead token — see client.ts's setUnauthorizedHandler.
    setUnauthorizedHandler(() => {
      setUser(null);
      setDisplayName(null);
    });

    (async () => {
      await loadStoredToken();
      if (getToken()) {
        try {
          await loadUser();
        } catch {
          await setToken(null);
        }
      }
      setIsLoading(false);
    })();

    return () => setUnauthorizedHandler(null);
  }, []);

  async function login(email: string, password: string) {
    const { token } = await api.login(email, password);
    await setToken(token);
    await loadUser();
  }

  async function register(email: string, password: string) {
    const { token } = await api.register(email, password);
    await setToken(token);
    await loadUser();
  }

  async function loginWithDeviceCode(code: string) {
    const { token } = await api.redeemDeviceLink(code);
    await setToken(token);
    await loadUser();
  }

  async function logout() {
    await setToken(null);
    setUser(null);
    setDisplayName(null);
  }

  return (
    <AuthContext.Provider
      value={{ isLoading, user, displayName, refreshDisplayName, login, register, loginWithDeviceCode, logout }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
