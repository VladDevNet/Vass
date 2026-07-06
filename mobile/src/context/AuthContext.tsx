import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, getToken, loadStoredToken, setToken, type CurrentUser } from '../api/client';

interface AuthContextValue {
  isLoading: boolean;
  user: CurrentUser | null;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [isLoading, setIsLoading] = useState(true);
  const [user, setUser] = useState<CurrentUser | null>(null);

  useEffect(() => {
    (async () => {
      await loadStoredToken();
      if (getToken()) {
        try {
          setUser(await api.me());
        } catch {
          await setToken(null);
        }
      }
      setIsLoading(false);
    })();
  }, []);

  async function login(email: string, password: string) {
    const { token } = await api.login(email, password);
    await setToken(token);
    setUser(await api.me());
  }

  async function register(email: string, password: string) {
    const { token } = await api.register(email, password);
    await setToken(token);
    setUser(await api.me());
  }

  async function logout() {
    await setToken(null);
    setUser(null);
  }

  return (
    <AuthContext.Provider value={{ isLoading, user, login, register, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
