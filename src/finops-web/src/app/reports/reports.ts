import { CurrencyPipe, DatePipe, DecimalPipe } from '@angular/common';
import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { AuthService } from '../auth.service';
import { problemMessage } from '../expenses/expenses.service';
import { CurrentUser } from '../foundation.service';
import { ReportArgs, ReportCatalogEntry, ReportColumn, ReportsService, ReportTable, saveFile } from './reports.service';

function iso(date: Date): string {
  return date.toISOString().slice(0, 10);
}

/**
 * Report catalog with parameters, an in-page preview and PDF/Excel downloads. The catalog only lists
 * reports the server says this user may run; scope and authorization are enforced again on every request.
 */
@Component({
  selector: 'app-reports',
  imports: [CurrencyPipe, DatePipe, DecimalPipe],
  templateUrl: './reports.html',
  styleUrl: './reports.css',
})
export class Reports {
  private readonly api = inject(ReportsService);
  private readonly auth = inject(AuthService);

  readonly user = input<CurrentUser | null>(null);

  readonly catalog = signal<ReportCatalogEntry[]>([]);
  readonly selected = signal<ReportCatalogEntry | null>(null);
  readonly args = signal<ReportArgs>({});
  readonly preview = signal<ReportTable | null>(null);
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      this.auth.persona();
      const user = this.user();
      untracked(() => {
        this.selected.set(null);
        this.preview.set(null);
        this.error.set(null);
        if (!user) { this.catalog.set([]); return; }
        this.api.catalog().subscribe({
          next: (entries) => this.catalog.set(entries),
          error: () => this.catalog.set([]),
        });
      });
    });
  }

  choose(entry: ReportCatalogEntry): void {
    const now = new Date();
    const first = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
    this.selected.set(entry);
    this.preview.set(null);
    this.error.set(null);
    this.args.set({ from: iso(first), to: iso(now), year: String(now.getUTCFullYear()), month: '' });
  }

  setArg(name: keyof ReportArgs, value: string): void {
    this.args.update((a) => ({ ...a, [name]: value }));
  }

  hasParameter(name: string): boolean {
    return this.selected()?.parameters.some((p) => p.name === name) ?? false;
  }

  runPreview(): void {
    const entry = this.selected();
    if (!entry) return;
    this.busy.set('preview');
    this.error.set(null);
    this.api.preview(entry.key, this.relevantArgs(entry)).subscribe({
      next: (table) => { this.preview.set(table); this.busy.set(null); },
      error: (e: unknown) => { this.preview.set(null); this.error.set(problemMessage(e)); this.busy.set(null); },
    });
  }

  download(format: 'pdf' | 'xlsx'): void {
    const entry = this.selected();
    if (!entry) return;
    this.busy.set(format);
    this.error.set(null);
    this.api.download(entry.key, format, this.relevantArgs(entry)).subscribe({
      next: (file) => { saveFile(file); this.busy.set(null); },
      error: () => {
        // Errors arrive as a Blob here; preview shows the readable validation message.
        this.error.set('The download failed. Use Preview to see why.');
        this.busy.set(null);
      },
    });
  }

  isNumeric(column: ReportColumn): boolean {
    return column.kind === 'Money' || column.kind === 'Integer' || column.kind === 'Percent';
  }

  total(table: ReportTable, column: ReportColumn): number {
    return table.rows.reduce((sum, row) => sum + Number(row[column.key] ?? 0), 0);
  }

  hasTotals(table: ReportTable): boolean {
    return table.rows.length > 0 && table.columns.some((c) => c.total);
  }

  private relevantArgs(entry: ReportCatalogEntry): ReportArgs {
    const names = new Set(entry.parameters.map((p) => p.name));
    return Object.fromEntries(Object.entries(this.args()).filter(([name]) => names.has(name as never)));
  }
}
