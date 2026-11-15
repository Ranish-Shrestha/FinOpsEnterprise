import { bootstrapApplication } from '@angular/platform-browser';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { App } from './app/app';
import { authInterceptor } from './app/auth.interceptor';
import { loadRuntimeConfig } from './app/runtime-config';

// The deployment's /config.json must be applied before any service reads authConfig.
loadRuntimeConfig()
  .then(() => bootstrapApplication(App, { providers: [provideHttpClient(withInterceptors([authInterceptor]))] }))
  .catch((error: unknown) => console.error('Application startup failed', error));
