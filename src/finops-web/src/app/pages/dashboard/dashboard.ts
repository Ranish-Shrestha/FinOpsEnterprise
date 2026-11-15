import { Component, inject } from '@angular/core';
import { SessionStore } from '../../core/session.store';

/** Overview: API, database and identity status, plus the departments the caller can see. */
@Component({
  selector: 'app-dashboard',
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard {
  readonly session = inject(SessionStore);
  readonly state = this.session.state;

  refresh(): void {
    if (!this.session.loading()) void this.session.refresh();
  }
}
