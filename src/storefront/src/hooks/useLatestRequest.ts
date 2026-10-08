import { useEffect, useRef, useState } from 'react';
export function useLatestRequest<T>(
  load: (signal: AbortSignal) => Promise<T>,
  dependencies: unknown[],
) {
  const [state, setState] = useState<{ data: T | null; loading: boolean; error: Error | null }>({
    data: null,
    loading: true,
    error: null,
  });
  const revision = useRef(0);
  useEffect(() => {
    const id = ++revision.current;
    const controller = new AbortController();
    setState({ data: null, loading: true, error: null });
    (() => {
      try {
        return load(controller.signal);
      } catch (error) {
        return Promise.reject(error);
      }
    })()
      .then((data) => {
        if (id === revision.current && !controller.signal.aborted)
          setState({ data, loading: false, error: null });
      })
      .catch((error) => {
        if (id === revision.current && !controller.signal.aborted)
          setState({
            data: null,
            loading: false,
            error: error instanceof Error ? error : Error('Request failed'),
          });
      });
    return () => {
      revision.current++;
      controller.abort();
    };
  }, dependencies);
  return state;
}
