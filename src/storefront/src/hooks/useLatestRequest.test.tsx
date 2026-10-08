import React from 'react';
import { act, render, screen, cleanup } from '@testing-library/react';
import { expect, it, afterEach } from 'vitest';
import { useLatestRequest } from './useLatestRequest';
afterEach(cleanup);
it('discards slower old query even when fetch ignores abort', async () => {
  let old!: (x: string) => void, newer!: (x: string) => void;
  function Probe({ sort }: { sort: string }) {
    const state = useLatestRequest(
      () =>
        new Promise<string>((r) => {
          if (sort === 'old') old = r;
          else newer = r;
        }),
      [sort],
    );
    return <span>{state.data ?? 'loading'}</span>;
  }
  const view = render(<Probe sort="old" />);
  view.rerender(<Probe sort="new" />);
  await act(async () => newer('new result'));
  await act(async () => old('old result'));
  expect(screen.getByText('new result')).toBeTruthy();
});

it('renders synchronous validation failure without crashing', async () => {
  function Probe() {
    const state = useLatestRequest<string>(() => {
      throw Error('Invalid price');
    }, []);
    return <span>{state.error?.message ?? 'loading'}</span>;
  }
  render(<Probe />);
  expect(await screen.findByText('Invalid price')).toBeTruthy();
});
