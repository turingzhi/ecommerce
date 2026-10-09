export interface User {
  userId: string;
  email?: string;
  permissions: string[];
}
export interface Product {
  id: number;
  name: string;
  description: string;
  category: string;
  priceCents: number;
}
export interface Details extends Product {
  available: number;
  currency: string;
}
export interface AdminProduct extends Details {
  version: number;
}
export interface Page<T> {
  products: T[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Cart {
  items: { productId: number; quantity: number }[];
}
export interface OrderSummary {
  id: string;
  status: string;
  createdAt: string;
  currency: string;
}
export interface Order extends OrderSummary {
  orderItems: { id: string; productId: number; quantity: number; unitPriceCents: number }[];
}
export interface Payment {
  id: string;
  orderId: string;
  amountCents: number;
  currency: string;
  status: string;
  createdAt: string;
}
export interface Refund {
  id: string;
  paymentId: string;
  amountCents: number;
  currency: string;
  status: string;
  createdAt: string;
}
export interface AdminOrderSummary extends OrderSummary {
  customerId: string;
}
export interface AdminOrder extends Order {
  customerId: string;
}
export interface AdminPayment extends Payment {
  customerId: string;
}
export interface AdminOrderPage {
  page: number;
  pageSize: number;
  total: number;
  orders: AdminOrderSummary[];
}
export interface AdminPaymentPage {
  page: number;
  pageSize: number;
  total: number;
  payments: AdminPayment[];
}
