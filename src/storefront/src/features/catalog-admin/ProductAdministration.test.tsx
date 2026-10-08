import React from 'react';
import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { ProductForm } from './ProductForm';
import { ProductAdminPage } from './ProductAdminPage';
import { AuthProvider } from '../../auth/AuthProvider';
afterEach(cleanup);
it('blocks anonymous admin routes', () => {
  render(
    <AuthProvider>
      <ProductAdminPage />
    </AuthProvider>,
  );
  expect(screen.getByText(/products:manage/)).toBeTruthy();
});
it('sends loaded version without stock and preserves edits after stale save', async () => {
  const save = vi.fn().mockRejectedValue(Error('Conflict'));
  render(
    <ProductForm
      product={{
        id: 1,
        name: 'Original',
        category: 'Audio',
        description: 'Text',
        priceCents: 1999,
        currency: 'EUR',
        available: 7,
        version: 4,
      }}
      save={save}
      reload={() => {}}
    />,
  );
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Edited' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save product' }));
  await screen.findByText('Conflict');
  expect(save.mock.calls[0][0]).toMatchObject({
    name: 'Edited',
    expectedVersion: 4,
    priceCents: 1999,
  });
  expect(save.mock.calls[0][0].available).toBeUndefined();
  expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('Edited');
  expect(screen.getByRole('button', { name: 'Reload current product' })).toBeTruthy();
});
it('creates with initial stock and exact decimal price', async () => {
  const save = vi.fn().mockResolvedValue(undefined);
  render(<ProductForm save={save} />);
  for (const [label, value] of [
    ['Name', 'New'],
    ['Category', 'Audio'],
    ['Description', 'Text'],
    ['Price (EUR)', '19.99'],
    ['Initial stock', '3'],
  ])
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
  fireEvent.click(screen.getByRole('button', { name: 'Create product' }));
  await screen.findByText(/Saved/);
  expect(save.mock.calls[0][0]).toMatchObject({ available: 3, priceCents: 1999 });
});

it('accepts an empty description as allowed by the catalog contract', async () => {
  const save = vi.fn().mockResolvedValue(undefined);
  render(<ProductForm save={save} />);
  for (const [label, value] of [
    ['Name', 'New'],
    ['Category', 'Audio'],
    ['Price (EUR)', '1.00'],
    ['Initial stock', '1'],
  ])
    fireEvent.change(screen.getByLabelText(label), { target: { value } });
  const textarea = screen.getByLabelText('Description') as HTMLTextAreaElement;
  expect(textarea.required).toBe(false);
  expect(textarea.form!.checkValidity()).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Create product' }));
  await screen.findByText(/Saved/);
  expect(save.mock.calls[0][0].description).toBe('');
});
