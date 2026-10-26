import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Payment, LogEntry, Account } from './models';

export interface NewAccount {
  code: string;
  holder: string;
  currency: string;
  branch: string;
  openingCents: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  accounts = () => firstValueFrom(this.http.get<Account[]>('/api/accounts'));
  openAccount = (body: NewAccount) =>
    firstValueFrom(this.http.post<Account>('/api/accounts', body));

  payments = (accountCode: string) =>
    firstValueFrom(this.http.get<Payment[]>('/api/payments', { params: { accountCode } }));
  book = (body: { accountCode: string; reference: string; amountCents: number }) =>
    firstValueFrom(this.http.post<Payment>('/api/payments', body));
  reverse = (id: string) =>
    firstValueFrom(this.http.post<Payment>(`/api/payments/${id}/reversal`, {}));
  events = (id: string) => firstValueFrom(this.http.get<LogEntry[]>(`/api/payments/${id}/events`));
}
