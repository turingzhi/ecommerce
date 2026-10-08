export interface RequestOptions {
  method?: string;
  body?: unknown;
  token?: string;
  key?: string;
  signal?: AbortSignal;
}
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public retryAfterSeconds: number | null = null,
    public details: Record<string, string[]> | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}
export async function apiRequest<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers = new Headers({ Accept: 'application/json' });
  if (options.body !== undefined) headers.set('Content-Type', 'application/json');
  if (options.token) headers.set('Authorization', `Bearer ${options.token}`);
  if (options.key) headers.set('Idempotency-Key', options.key);
  const response = await fetch(path, {
    method: options.method ?? 'GET',
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    signal: options.signal,
    cache: 'no-store',
  });
  const text = await response.text();
  let payload: unknown;
  try {
    payload = text ? JSON.parse(text) : undefined;
  } catch {
    payload = text;
  }
  if (!response.ok) {
    const data = payload && typeof payload === 'object' ? (payload as Record<string, unknown>) : {};
    const details =
      data.errors && typeof data.errors === 'object'
        ? (data.errors as Record<string, string[]>)
        : null;
    const message =
      typeof payload === 'string'
        ? payload
        : typeof data.error === 'string'
          ? data.error
          : typeof data.detail === 'string'
            ? data.detail
            : details
              ? Object.values(details).flat().join(' ')
              : typeof data.title === 'string'
                ? data.title
                : `Request failed (${response.status})`;
    const retry = response.headers.get('Retry-After');
    let seconds: number | null = null;
    if (retry) {
      const n = Number(retry);
      seconds = Number.isFinite(n)
        ? Math.max(0, n)
        : Math.max(0, Math.ceil((Date.parse(retry) - Date.now()) / 1000));
      if (!Number.isFinite(seconds)) seconds = null;
    }
    throw new ApiError(response.status, message, seconds, details);
  }
  return payload as T;
}
