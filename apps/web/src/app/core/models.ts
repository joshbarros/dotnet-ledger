export interface Account {
  id: string;
  code: string;
  holder: string;
  currency: string;
  branch: string;
  openedAt: string;
  openingCents: number;
  availableCents: number;
}

export type PaymentStatus = 'Requested' | 'Held' | 'Settled' | 'Reversed' | 'Rejected';

export interface Payment {
  id: string;
  accountCode: string;
  ownerId: string;
  reference: string;
  amountCents: number;
  feeCents: number;
  status: PaymentStatus;
  reason: string | null;
  receiptNumber: string | null;
  createdAt: string;
  updatedAt: string;
  fundsReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}

export interface UserInfo {
  id: string;
  email: string;
  roles: string[];
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
  user: UserInfo;
}
