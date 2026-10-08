import { expect, it } from 'vitest';
import { parsePriceInput, formatMoney, totalCents } from './money';
it('parses literal decimal cents exactly', () => {
  expect(parsePriceInput('19.99')).toBe(1999);
  expect(parsePriceInput('0.01')).toBe(1);
  expect(parsePriceInput('0')).toBe(0);
});
it('rejects precision loss and unsupported totals', () => {
  for (const value of ['1.001', '-1', 'Infinity', '90071992547409.92'])
    expect(() => parsePriceInput(value)).toThrow();
  expect(() => formatMoney(Number.MAX_SAFE_INTEGER + 1)).toThrow();
  expect(() => totalCents([{ quantity: 2, unitPriceCents: Number.MAX_SAFE_INTEGER }])).toThrow();
});
