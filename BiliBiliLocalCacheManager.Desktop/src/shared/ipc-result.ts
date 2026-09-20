// Plain objects survive both Electron IPC and the isolated preload bridge.
export interface IpcFailure {
  ipcFailure: { code: string; message: string };
}

export class IpcError extends Error {
  constructor(message: string, readonly code: string) {
    super(message);
    this.name = 'IpcError';
  }
}

export function ipcFailure(error: unknown): IpcFailure {
  const code = error instanceof Error && 'code' in error && typeof error.code === 'string'
    ? error.code : error instanceof TypeError ? 'INVALID_REQUEST' : 'IPC_ERROR';
  return { ipcFailure: { code, message: error instanceof Error ? error.message : 'IPC request failed.' } };
}

export function unwrapIpcResult<T>(value: T | IpcFailure): T {
  if (value && typeof value === 'object' && 'ipcFailure' in value) {
    const failure = value.ipcFailure;
    if (!failure || typeof failure.code !== 'string' || typeof failure.message !== 'string')
      throw new IpcError('Invalid IPC error response.', 'IPC_PROTOCOL_ERROR');
    throw new IpcError(failure.message, failure.code);
  }
  return value as T;
}

export function errorCode(error: unknown): string | undefined {
  return error instanceof Error && 'code' in error && typeof error.code === 'string' ? error.code : undefined;
}
