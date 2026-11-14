import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CurrentUser } from '../foundation.service';
import { Reports } from './reports';
import { fileNameFrom, ReportCatalogEntry, ReportsService, ReportTable } from './reports.service';

const summary: ReportCatalogEntry = {
  key: 'expense-summary', title: 'Department expense summary', description: 'Spend by department',
  parameters: [{ name: 'from', kind: 'date', required: false, description: '' }, { name: 'to', kind: 'date', required: false, description: '' }],
  formats: ['json', 'pdf', 'xlsx'],
};

const table: ReportTable = {
  key: 'expense-summary', title: 'Department expense summary', description: '', scope: '1 department(s) you are a member of',
  generatedBy: 'Demo Manager', generatedAtUtc: '2026-11-14T12:00:00Z', parameters: [{ label: 'From', value: '2026-11-01' }],
  columns: [{ key: 'department', header: 'Department', kind: 'Text', total: false },
            { key: 'total', header: 'Total', kind: 'Money', total: true }],
  rows: [{ department: 'Demo Finance', total: 100 }, { department: 'Demo Finance', total: 50.5 }],
};

const manager: CurrentUser = { displayName: 'M', email: 'm@example.invalid', roles: [], departmentIds: ['d'], permissions: ['reports.read.department'] };

describe('ReportsService', () => {
  let http: HttpTestingController;
  let service: ReportsService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ReportsService);
  });
  afterEach(() => http.verify());

  it('downloads as a blob with the format and the server file name', () => {
    let fileName = '';
    service.download('expense-summary', 'xlsx', { from: '2026-11-01', to: '' }).subscribe((f) => fileName = f.fileName);
    const request = http.expectOne((r) => r.url === '/api/reports/expense-summary');
    expect(request.request.params.get('format')).toBe('xlsx');
    expect(request.request.params.get('from')).toBe('2026-11-01');
    expect(request.request.params.has('to')).toBe(false); // empty values are not sent
    expect(request.request.responseType).toBe('blob');
    request.flush(new Blob(['x']), { headers: { 'Content-Disposition': "attachment; filename=x.xlsx; filename*=UTF-8''expense-summary-20261114.xlsx" } });
    expect(fileName).toBe('expense-summary-20261114.xlsx');
  });

  it('parses both Content-Disposition forms', () => {
    expect(fileNameFrom('attachment; filename="ledger.pdf"')).toBe('ledger.pdf');
    expect(fileNameFrom(null)).toBeNull();
  });
});

describe('Reports panel', () => {
  async function render(entries: ReportCatalogEntry[]) {
    const api = { catalog: vi.fn().mockReturnValue(of(entries)), preview: vi.fn().mockReturnValue(of(table)), download: vi.fn() };
    await TestBed.configureTestingModule({ imports: [Reports], providers: [{ provide: ReportsService, useValue: api }] }).compileComponents();
    const fixture = TestBed.createComponent(Reports);
    fixture.componentRef.setInput('user', manager);
    fixture.detectChanges();
    return { fixture, api, text: () => fixture.nativeElement.textContent as string };
  }

  it('is hidden when the server offers no reports', async () => {
    const { text } = await render([]);
    expect(text().trim()).toBe('');
  });

  it('previews a report with its scope and a totals row', async () => {
    const { fixture, api, text } = await render([summary]);
    fixture.nativeElement.querySelector('.report-card').click();
    fixture.detectChanges();
    fixture.nativeElement.querySelector('form button[type=submit]').click();
    fixture.detectChanges();

    expect(api.preview).toHaveBeenCalledWith('expense-summary', expect.objectContaining({ from: expect.any(String), to: expect.any(String) }));
    expect(api.preview.mock.calls[0][1]).not.toHaveProperty('year'); // only this report's parameters are sent
    expect(text()).toContain('1 department(s) you are a member of');
    expect(text()).toContain('$150.50');
  });
});
