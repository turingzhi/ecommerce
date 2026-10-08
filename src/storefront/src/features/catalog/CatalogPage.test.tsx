import React from 'react';
import { render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import { CatalogPage } from './CatalogPage';
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
it('renders actual catalog data and price sort options', async () => {
  vi.stubGlobal(
    'fetch',
    vi
      .fn()
      .mockResolvedValue(
        new Response(
          '{"products":[{"id":1,"name":"Real SQL product","category":"Audio","description":"Text","priceCents":1999}],"total":1,"page":1,"pageSize":20}',
        ),
      ),
  );
  render(
    <MemoryRouter>
      <CatalogPage />
    </MemoryRouter>,
  );
  expect(await screen.findByText('Real SQL product')).toBeTruthy();
  expect(screen.getByText('Price: low to high')).toBeTruthy();
  expect(screen.getByText('EUR 19.99')).toBeTruthy();
});
