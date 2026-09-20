type Reject = (message: string) => never;
const reject: Reject = message => { throw new TypeError(`${message}。`); };

export function boundedString(value: unknown, name: string, maximum: number, invalid = reject): string {
  if (typeof value !== 'string' || value.length > maximum) invalid(`${name} 必须是长度不超过 ${maximum} 的字符串`);
  return value as string;
}

export function booleanValue(value: unknown, name: string, invalid = reject): boolean {
  if (typeof value !== 'boolean') invalid(`${name} 必须是布尔值`);
  return value as boolean;
}

export function boundedInteger(value: unknown, name: string, minimum: number, maximum: number, invalid = reject): number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum || (value as number) > maximum)
    invalid(`${name} 必须是 ${minimum}–${maximum} 之间的整数`);
  return value as number;
}
