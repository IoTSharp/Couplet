// UTF-8 注释用于冻结字节与行列位置：你好 😀
export class Formatter<T> {
  format(value: number): number { return value; }
  format(value: string): string { return value; }
  echo<TValue>(value: TValue): TValue { return value; }
}

export class Secondary {
  format(value: number): number { return value; }
}

export function format(value: string): string { return value; }
