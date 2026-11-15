import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/** The root: every page renders inside a layout chosen by the router (see app.routes.ts). */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
