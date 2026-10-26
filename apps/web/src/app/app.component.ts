import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { Payment, PaymentStatus, LogEntry, Account } from './core/models';

const BADGES: Record<PaymentStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Settled: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Reversed: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-root',
  imports: [DatePipe],
  templateUrl: './app.component.html',
})
export class AppComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly accounts = signal<Account[]>([]);
  readonly selectedCode = signal('');
  readonly payments = signal<Payment[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly holder = signal('Acme Treasury');
  readonly currency = signal('USD');
  readonly branch = signal('Main Branch');
  readonly capacity = signal('100000');

  readonly reference = signal('');
  readonly amountCents = signal('10000');

  readonly selected = computed(() => this.accounts().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: PaymentStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const accounts = await this.api.accounts();
      this.accounts.set(accounts);
      if (!this.selectedCode() && accounts.length > 0) this.selectedCode.set(accounts[0].code);
      if (this.selectedCode()) this.payments.set(await this.api.payments(this.selectedCode()));
      const id = this.logFor();
      if (id) this.log.set(await this.api.events(id));
    } catch {
      this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openAccount(): Promise<void> {
    await this.run(async () => {
      const account = await this.api.openAccount({
        code: this.code(),
        holder: this.holder(),
        currency: this.currency(),
        branch: this.branch(),
        openingCents: Number(this.capacity()),
      });
      this.code.set('');
      this.selectedCode.set(account.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      await this.api.book({
        accountCode: this.selectedCode(),
        reference: this.reference(),
        amountCents: Number(this.amountCents()),
      });
      this.reference.set('');
    });
  }

  async reverse(payment: Payment): Promise<void> {
    await this.run(() => this.api.reverse(payment.id));
  }

  async showLog(payment: Payment): Promise<void> {
    this.logFor.set(payment.id);
    this.log.set([]);
    await this.refresh();
  }

  canReverse(payment: Payment): boolean {
    return payment.status === 'Held' || payment.status === 'Settled';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
