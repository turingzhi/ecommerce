import React, { createContext, useCallback, useContext, useRef, useState } from 'react';
import { apiRequest, ApiError, type RequestOptions } from '../api/client';
import type { User } from '../api/types';
export interface AuthSession {
  token: string;
  user: User;
}
interface Auth {
  session: AuthSession | null;
  generation: number;
  login(email: string, password: string): Promise<void>;
  register(email: string, password: string): Promise<void>;
  logout(): void;
  request<T>(path: string, options?: RequestOptions): Promise<T>;
}
const Context = createContext<Auth | null>(null);
export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(null);
  const [generation, setGeneration] = useState(0);
  const current = useRef<AuthSession | null>(null);
  const epoch = useRef(0);
  const reset = useCallback(() => {
    epoch.current++;
    current.current = null;
    setSession(null);
    setGeneration(epoch.current);
  }, []);
  const login = useCallback(
    async (email: string, password: string) => {
      reset();
      const captured = epoch.current;
      const result = await apiRequest<{ accessToken: string }>('/auth/login', {
        method: 'POST',
        body: { email, password },
      });
      const user = await apiRequest<User>('/auth/me', { token: result.accessToken });
      if (epoch.current !== captured) throw new DOMException('Session changed', 'AbortError');
      const value = { token: result.accessToken, user };
      current.current = value;
      setSession(value);
    },
    [reset],
  );
  const register = useCallback(
    async (email: string, password: string) => {
      const captured = epoch.current;
      await apiRequest('/auth/register', { method: 'POST', body: { email, password } });
      if (epoch.current !== captured) throw new DOMException('Session changed', 'AbortError');
      await login(email, password);
    },
    [login],
  );
  const request = useCallback(
    async <T,>(path: string, options: RequestOptions = {}): Promise<T> => {
      const captured = epoch.current;
      const owner = current.current;
      if (!owner) throw new ApiError(401, 'Please sign in.');
      try {
        const result = await apiRequest<T>(path, { ...options, token: owner.token });
        if (epoch.current !== captured || current.current !== owner)
          throw new DOMException('Session changed', 'AbortError');
        return result;
      } catch (e) {
        if (epoch.current !== captured || current.current !== owner)
          throw new DOMException('Session changed', 'AbortError');
        if (e instanceof ApiError && e.status === 401) reset();
        throw e;
      }
    },
    [reset],
  );
  return (
    <Context.Provider value={{ session, generation, login, register, logout: reset, request }}>
      {children}
    </Context.Provider>
  );
}
export function useAuth() {
  const auth = useContext(Context);
  if (!auth) throw Error('AuthProvider is required');
  return auth;
}
