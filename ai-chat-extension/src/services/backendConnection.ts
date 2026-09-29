import type { BackendConnection, BackendProcess } from "./BackendProcess";

let backend: BackendProcess | null = null;

/** Called once on activation with the window's backend manager. */
export function setBackendProcess(process: BackendProcess): void {
  backend = process;
}

/** The window's backend manager (null before activation). */
export function getBackendProcess(): BackendProcess | null {
  return backend;
}

/**
 * The backend to talk to: the managed process (started on first use) or the external one from settings.
 * Throws a readable error when it can't be reached, so callers show it in the chat.
 */
export async function backendConnection(): Promise<BackendConnection> {
  const connection = await backend?.ensureStarted();
  if (!connection) {
    throw new Error(backend?.lastProblem ?? "The AI Agent backend is not running. Open the backend log for the reason.");
  }
  return connection;
}

/** Headers every backend request needs (the backend rejects requests without the token). */
export function authHeaders(connection: BackendConnection): Record<string, string> {
  return { "X-Agent-Token": connection.token };
}
