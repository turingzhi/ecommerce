import React from 'react';
import { MemoryRouter } from 'react-router-dom';
import { render, screen, cleanup } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { OrderItems } from './OrderDetailsPage';
afterEach(cleanup);
it('displays purchase prices rather than catalog prices', () => {
  render(
    <MemoryRouter>
      <OrderItems
        order={{
          id: 'order',
          status: 'Paid',
          currency: 'EUR',
          createdAt: '',
          orderItems: [{ id: 'item', productId: 1, quantity: 2, unitPriceCents: 1999 }],
        }}
      />
    </MemoryRouter>,
  );
  expect(screen.getByText('EUR 39.98')).toBeTruthy();
  expect(screen.getByText('EUR 19.99')).toBeTruthy();
});
