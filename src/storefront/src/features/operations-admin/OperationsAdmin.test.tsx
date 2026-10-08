import { fireEvent, render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, expect, it, vi } from 'vitest';
import App from '../../App';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it.each(['orders', 'payments'])('blocks anonymous access to the %s admin page', (resource) => {
  render(
    <MemoryRouter initialEntries={[`/admin/${resource}`]}>
      <App />
    </MemoryRouter>,
  );
  expect(screen.getByText(new RegExp(`${resource}:read`))).toBeTruthy();
});

async function login(permissions: string[]) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const data =
        url === '/auth/login'
          ? { accessToken: 'test-token' }
          : url === '/auth/me'
            ? { userId: 'operator', email: 'operator@example.test', permissions }
            : url.startsWith('/admin/orders')
              ? {
                  page: 1,
                  pageSize: 20,
                  total: 1,
                  orders: [
                    {
                      id: 'order-1',
                      customerId: 'customer-a',
                      status: 'Paid',
                      currency: 'EUR',
                      createdAt: '2026-10-08T12:00:00Z',
                    },
                  ],
                }
              : url.startsWith('/admin/payments')
                ? {
                    page: 1,
                    pageSize: 20,
                    total: 1,
                    payments: [
                      {
                        id: 'payment-1',
                        orderId: 'order-1',
                        customerId: 'customer-a',
                        amountCents: 2468,
                        currency: 'EUR',
                        status: 'Succeeded',
                        createdAt: '2026-10-08T12:00:00Z',
                      },
                    ],
                  }
                : { products: [], page: 1, pageSize: 20, total: 0 };
      return new Response(JSON.stringify(data), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  render(
    <MemoryRouter initialEntries={['/login']}>
      <App />
    </MemoryRouter>,
  );
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'operator@example.test' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Password!123' } });
  fireEvent.click(screen.getByRole('button', { name: /^Sign in$/ }));
  await screen.findByRole('button', { name: 'Sign out' });
}

it('shows cross-customer orders only with the order read permission', async () => {
  await login(['orders:read']);
  const link = screen.getByRole('link', { name: 'Orders admin' });
  expect(screen.queryByRole('link', { name: 'Payments admin' })).toBeNull();
  fireEvent.click(link);
  await screen.findByText('customer-a');
  expect(screen.getByText('Paid', { selector: '.badge' })).toBeTruthy();
  expect(screen.getByRole('button', { name: 'Next' }).hasAttribute('disabled')).toBe(true);
  fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
  expect(screen.queryByText('customer-a')).toBeNull();
});

it('shows payment amounts and allows a payment reader without order permission', async () => {
  await login(['payments:read']);
  expect(screen.queryByRole('link', { name: 'Orders admin' })).toBeNull();
  fireEvent.click(screen.getByRole('link', { name: 'Payments admin' }));
  await screen.findByText('EUR 24.68');
  expect(screen.getByText('customer-a')).toBeTruthy();
  expect(screen.getByText('Succeeded', { selector: '.badge' })).toBeTruthy();
});
