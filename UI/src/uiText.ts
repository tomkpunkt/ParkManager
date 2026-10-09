/**
 * Resolves language-neutral messages published by the C# systems
 * (see Tools/UiText.cs). Wire format: {"k":"key","a":[arg, ...]}; an
 * argument may itself be a message and is resolved recursively. Templates use
 * {0}, {1}, ... and {n:d} to round a numeric argument to d decimals.
 * Plain text without the message prefix is returned unchanged, so older
 * bindings and the browser mock keep working.
 */
export type UiMessage = { k: string; a?: unknown[] };

const PREFIX = "{\"k\":";

const isMessage = (value: unknown): value is UiMessage =>
  typeof value === "object" && value !== null
  && typeof (value as UiMessage).k === "string";

export const parseUiText = (raw: string): UiMessage | string => {
  if (!raw || !raw.startsWith(PREFIX)) return raw ?? "";
  try {
    const parsed = JSON.parse(raw);
    return isMessage(parsed) ? parsed : raw;
  } catch {
    return raw;
  }
};

const formatArgument = <T extends Record<string, string>>(value: unknown,
  decimals: string | undefined, messages: T, depth: number): string => {
  if (value === null || value === undefined) return "";
  if (isMessage(value)) return formatMessage(value, messages, depth + 1);
  if (typeof value === "number") {
    if (decimals === undefined) return String(value);
    const rounded = value.toFixed(Number(decimals));
    // Avoid "-0" for values that round to zero.
    return Number(rounded) === 0 ? (0).toFixed(Number(decimals)) : rounded;
  }
  return String(value);
};

const formatMessage = <T extends Record<string, string>>(message: UiMessage,
  messages: T, depth: number): string => {
  const args = Array.isArray(message.a) ? message.a : [];
  // Unknown keys fall back to the key itself so a missing translation is
  // visible instead of silently hiding the notice.
  const template = messages[message.k] ?? message.k;
  if (depth > 8) return template;
  return template.replace(/\{(\d+)(?::(\d+))?\}/g, (match, index: string,
    decimals: string | undefined) => {
    const position = Number(index);
    return position < args.length
      ? formatArgument(args[position], decimals, messages, depth) : match;
  });
};

/** Formats a published binding value for the active language. */
export const formatUiText = <T extends Record<string, string>>(raw: string,
  messages: T): string => {
  const parsed = parseUiText(raw);
  return typeof parsed === "string" ? parsed : formatMessage(parsed, messages, 0);
};
