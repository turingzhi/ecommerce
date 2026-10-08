export function safeInteger(value: number): number {
  if (!Number.isSafeInteger(value) || value < 0)
    throw Error('This amount exceeds the supported range.');
  return value;
}
export function parsePriceInput(value: string): number {
  if (!/^\d+(?:\.\d{1,2})?$/.test(value))
    throw Error('Enter a nonnegative price with at most two decimal places.');
  const [whole, fraction = ''] = value.split('.');
  const cents = BigInt(whole) * 100n + BigInt(fraction.padEnd(2, '0'));
  if (cents > BigInt(Number.MAX_SAFE_INTEGER))
    throw Error('This price exceeds the supported range.');
  return Number(cents);
}
export function formatMoney(value: number, currency = 'EUR'): string {
  safeInteger(value);
  const cents = BigInt(value);
  return `${currency} ${cents / 100n}.${(cents % 100n).toString().padStart(2, '0')}`;
}
export function totalCents(items: { quantity: number; unitPriceCents: number }[]): number {
  const total = items.reduce(
    (n, item) => n + BigInt(safeInteger(item.quantity)) * BigInt(safeInteger(item.unitPriceCents)),
    0n,
  );
  if (total > BigInt(Number.MAX_SAFE_INTEGER))
    throw Error('This total exceeds the supported range.');
  return Number(total);
}
