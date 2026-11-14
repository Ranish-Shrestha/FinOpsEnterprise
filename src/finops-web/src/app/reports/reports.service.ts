import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { map, Observable } from 'rxjs';

export interface ReportParameterInfo { name: 'from' | 'to' | 'year' | 'month'; kind: string; required: boolean; description: string; }
export interface ReportCatalogEntry { key: string; title: string; description: string; parameters: ReportParameterInfo[]; formats: string[]; }
export interface ReportColumn { key: string; header: string; kind: 'Text' | 'Date' | 'DateTime' | 'Money' | 'Integer' | 'Percent'; total: boolean; }
export interface ReportTable {
  key: string; title: string; description: string; columns: ReportColumn[];
  rows: Record<string, string | number | null>[]; parameters: { label: string; value: string }[];
  scope: string; generatedBy: string; generatedAtUtc: string;
}
export type ReportArgs = Partial<Record<'from' | 'to' | 'year' | 'month', string>>;
export interface DownloadedFile { blob: Blob; fileName: string; }

/** Reports API. Which reports appear and which departments they include is decided by the server. */
@Injectable({ providedIn: 'root' })
export class ReportsService {
  private readonly http = inject(HttpClient);

  catalog(): Observable<ReportCatalogEntry[]> {
    return this.http.get<ReportCatalogEntry[]>('/api/reports');
  }

  preview(key: string, args: ReportArgs): Observable<ReportTable> {
    return this.http.get<ReportTable>(`/api/reports/${key}`, { params: this.params(args) });
  }

  /** Downloads through HttpClient so the same authentication is applied as for every other API call. */
  download(key: string, format: 'pdf' | 'xlsx', args: ReportArgs): Observable<DownloadedFile> {
    return this.http.get(`/api/reports/${key}`, {
      params: this.params(args).set('format', format), responseType: 'blob', observe: 'response',
    }).pipe(map((response: HttpResponse<Blob>) => ({
      blob: response.body!,
      fileName: fileNameFrom(response.headers.get('Content-Disposition')) ?? `${key}.${format}`,
    })));
  }

  private params(args: ReportArgs): HttpParams {
    let params = new HttpParams();
    for (const [name, value] of Object.entries(args)) if (value) params = params.set(name, value);
    return params;
  }
}

export function fileNameFrom(disposition: string | null): string | null {
  if (!disposition) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(disposition);
  return plain ? plain[1] : null;
}

/** Saves a downloaded file using a temporary object URL. */
export function saveFile(file: DownloadedFile): void {
  const url = URL.createObjectURL(file.blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = file.fileName;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
