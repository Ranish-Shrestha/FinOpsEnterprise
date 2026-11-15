import { spawn } from 'node:child_process';
import { workerDirectory, workerEnvironment } from './stack';

/**
 * Starts FinOps.Worker for the run (it has no HTTP port, so it can't be a Playwright webServer) and returns the
 * teardown that stops it. Resolves once the worker logs that it is polling the outbox.
 */
export default async function globalSetup(): Promise<() => Promise<void>> {
  const worker = spawn('dotnet', ['FinOps.Worker.dll'], {
    cwd: workerDirectory,
    env: { ...process.env, ...workerEnvironment() },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  let output = '';
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`Worker did not start within 60 s.\n${output.slice(-2000)}`)), 60_000);
    const onData = (chunk: Buffer) => {
      output += chunk.toString();
      if (output.includes('Outbox worker started')) {
        clearTimeout(timer);
        resolve();
      }
    };
    worker.stdout.on('data', onData);
    worker.stderr.on('data', onData);
    worker.once('exit', (code) => {
      clearTimeout(timer);
      reject(new Error(`Worker exited with code ${code}.\n${output.slice(-2000)}`));
    });
  });

  return async () => {
    worker.removeAllListeners('exit');
    worker.kill();
  };
}
