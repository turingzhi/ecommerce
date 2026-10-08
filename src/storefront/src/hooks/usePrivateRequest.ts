import { useAuth } from '../auth/AuthProvider';
import { useLatestRequest } from './useLatestRequest';
import type { RequestOptions } from '../api/client';
export function usePrivateRequest<T>(
  load: (
    request: <R>(path: string, options?: RequestOptions) => Promise<R>,
    signal: AbortSignal,
  ) => Promise<T>,
  dependencies: unknown[],
) {
  const auth = useAuth();
  return useLatestRequest(
    (signal) => load(auth.request, signal),
    [auth.generation, auth.session?.user.userId, ...dependencies],
  );
}
