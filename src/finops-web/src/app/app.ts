import { Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { FoundationService, FoundationState } from './foundation.service';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly service = inject(FoundationService);
  private readonly destroyRef = inject(DestroyRef);
  readonly auth = inject(AuthService);
  readonly loading = signal(false);
  readonly state = signal<FoundationState | null>(null);

  ngOnInit(): void {
    if (this.auth.authenticated()) this.refresh();
    else void this.auth.completeRedirect().then(() => this.refresh());
  }

  choosePersona(objectId: string): void {
    this.auth.selectPersona(objectId);
    this.refresh();
  }

  refresh(): void {
    if (this.loading()) return;
    this.loading.set(true);
    this.service.load().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.loading.set(false)),
    ).subscribe((state) => this.state.set(state));
  }
}
