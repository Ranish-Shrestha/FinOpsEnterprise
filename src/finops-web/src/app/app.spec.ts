import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { App } from './app';
import { FoundationService, FoundationState } from './foundation.service';

describe('Foundation workspace', () => {
  it('renders unavailable departments and offers a retry', async () => {
    const load = vi.fn().mockReturnValue(of({ apiOnline: true, databaseReady: false, departments: null, user: null }));
    await TestBed.configureTestingModule({ imports: [App], providers: [{ provide: FoundationService, useValue: { load } }] }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain("We couldn't load the departments.");
    fixture.nativeElement.querySelector('button').click();
    expect(load).toHaveBeenCalledTimes(2);
  });

  it('shows loading before rendering real results', async () => {
    const result = new Subject<FoundationState>();
    await TestBed.configureTestingModule({ imports: [App], providers: [{ provide: FoundationService, useValue: { load: () => result } }] }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('button').disabled).toBe(true);
    result.next({ apiOnline: true, databaseReady: true, departments: [{ id: 'test', name: 'Demo Finance' }], user: null });
    result.complete();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Demo Finance');
    expect(fixture.nativeElement.querySelector('button').disabled).toBe(false);
  });
});
