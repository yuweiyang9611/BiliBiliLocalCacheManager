import type { JsonObject, JsonValue } from '../shared/contracts';

export interface HostRequest {
  id: string;
  method: string;
  params: JsonObject;
}

export interface HostError {
  code: string;
  message: string;
  details?: JsonValue;
}

export type HostMessage =
  | { id: string; result: JsonValue }
  | { id: string; error: HostError }
  | { event: string; payload: JsonValue };

const MAX_LINE_LENGTH = 64 * 1024 * 1024;

export class JsonLineDecoder {
  #buffer = '';

  push(chunk: string): HostMessage[] {
    this.#buffer += chunk;
    if (this.#buffer.length > MAX_LINE_LENGTH && !this.#buffer.includes('\n')) {
      this.#buffer = '';
      throw new Error('Desktop Host 返回了超过 64 MiB 的无分隔消息。');
    }

    const messages: HostMessage[] = [];
    let newline = this.#buffer.indexOf('\n');
    while (newline >= 0) {
      const line = this.#buffer.slice(0, newline).trimEnd();
      this.#buffer = this.#buffer.slice(newline + 1);
      if (line.length > 0) messages.push(parseHostMessage(line));
      newline = this.#buffer.indexOf('\n');
    }
    return messages;
  }

  finish(): HostMessage[] {
    const remainder = this.#buffer.trim();
    this.#buffer = '';
    return remainder ? [parseHostMessage(remainder)] : [];
  }
}

export function parseHostMessage(line: string): HostMessage {
  if (line.length > MAX_LINE_LENGTH) throw new Error('Desktop Host 消息超过 64 MiB。');
  let value: unknown;
  try {
    value = JSON.parse(line);
  } catch {
    throw new Error('Desktop Host 返回了无效 JSON。');
  }
  if (!isRecord(value)) throw new Error('Desktop Host 消息必须是对象。');

  if (typeof value.event === 'string' && 'payload' in value) {
    return { event: value.event, payload: value.payload as JsonValue };
  }
  if (typeof value.id !== 'string' || value.id.length === 0) {
    throw new Error('Desktop Host 响应缺少请求 ID。');
  }
  if ('error' in value) {
    if (!isRecord(value.error) || typeof value.error.code !== 'string' || typeof value.error.message !== 'string') {
      throw new Error('Desktop Host 返回了无效错误对象。');
    }
    return {
      id: value.id,
      error: {
        code: value.error.code,
        message: value.error.message,
        details: value.error.details as JsonValue | undefined,
      },
    };
  }
  if (!('result' in value)) throw new Error('Desktop Host 响应缺少 result 或 error。');
  return { id: value.id, result: value.result as JsonValue };
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
