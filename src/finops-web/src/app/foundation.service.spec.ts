import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { FoundationService, FoundationState } from './foundation.service';

describe('FoundationService', () => {
  let http: HttpTestingController;
  let service: FoundationService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(FoundationService);
  });
  afterEach(() => http.verify());

  it('loads the API, database and actual department results', () => {
    let result: FoundationState | undefined;
    service.load().subscribe((state) => result = state);
    http.expectOne('/health').flush({ status: 'Healthy' });
    http.expectOne('/health/ready').flush({ status: 'Healthy' });
    http.expectOne('/api/departments').flush([{ id: 'demo', name: 'Demo Finance' }]);
    http.expectOne('/api/me').flush({ displayName: 'Demo', email: 'demo@example.invalid', roles: [], permissions: [], departmentIds: [] });
    expect(result).toEqual({ apiOnline: true, databaseReady: true, departments: [{ id: 'demo', name: 'Demo Finance' }],
      user: { displayName: 'Demo', email: 'demo@example.invalid', roles: [], permissions: [], departmentIds: [] } });
  });

  it('preserves API availability when SQL Server is unavailable', () => {
    let result: FoundationState | undefined;
    service.load().subscribe((state) => result = state);
    http.expectOne('/health').flush({ status: 'Healthy' });
    http.expectOne('/health/ready').flush({}, { status: 503, statusText: 'Unavailable' });
    http.expectOne('/api/departments').flush({}, { status: 500, statusText: 'Failed' });
    http.expectOne('/api/me').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(result).toEqual({ apiOnline: true, databaseReady: false, departments: null, user: null });
  });
});
