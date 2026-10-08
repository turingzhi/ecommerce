import { ApiError } from '../api/client';
export function ErrorNotice({ error }: { error: unknown }) {
  if (!error || (error instanceof Error && error.name === 'AbortError')) return null;
  return (
    <p className="notice error" role="alert">
      {error instanceof Error ? error.message : 'Request failed.'}
      {error instanceof ApiError && error.retryAfterSeconds !== null
        ? ` Retry after ${error.retryAfterSeconds} seconds.`
        : ''}
    </p>
  );
}
