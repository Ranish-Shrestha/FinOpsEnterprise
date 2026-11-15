import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';
import { loadRuntimeConfig } from './app/runtime-config';

// The deployment's /config.json must be applied before any service reads authConfig.
loadRuntimeConfig()
  .then(() => bootstrapApplication(App, appConfig))
  .catch((error: unknown) => console.error('Application startup failed', error));
