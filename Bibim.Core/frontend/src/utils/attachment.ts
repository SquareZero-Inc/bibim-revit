/**
 * Attached document (.md / .txt / .csv) read in the panel and sent with one message.
 * The body stays in memory: it goes to the backend with `user_message` and from there
 * only to the LLM request — nothing here stores or uploads it.
 */

export const ATTACH_ACCEPT = '.md,.markdown,.txt,.csv';
export const ATTACH_MAX_CHARS = 200_000;
/** Hard stop before reading: a multi-MB "text" file is almost certainly the wrong file. */
export const ATTACH_MAX_BYTES = 10 * 1024 * 1024;
/** Chars kept for the expandable preview in the user bubble. */
const PREVIEW_CHARS = 20_000;

const ALLOWED = ['.md', '.markdown', '.txt', '.csv'];
export const MARKER_PREFIX = '📎 ';

export interface PendingAttachment {
  name: string;
  content: string;       // possibly cut at ATTACH_MAX_CHARS
  sizeBytes: number;
  omittedChars: number;  // > 0 when cut
}

/** What the user bubble keeps (no full body). */
export interface MessageAttachment {
  name: string;
  sizeBytes?: number;
  truncated?: boolean;
  preview?: string;
}

export type AttachError = 'unsupported' | 'tooLarge' | 'unreadable';

export function isAllowedAttachment(name: string): boolean {
  const lower = name.toLowerCase();
  return ALLOWED.some((ext) => lower.endsWith(ext));
}

/** UTF-8 first (BOM consumed by the decoder); invalid UTF-8 → CP949 (Korean Windows default). */
export function decodeText(bytes: Uint8Array): string {
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    text = new TextDecoder('euc-kr').decode(bytes); // WHATWG "euc-kr" = windows-949 (CP949)
  }
  return text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
}

export async function readAttachment(file: File): Promise<PendingAttachment | AttachError> {
  if (!isAllowedAttachment(file.name)) return 'unsupported';
  if (file.size > ATTACH_MAX_BYTES) return 'tooLarge';
  try {
    const text = decodeText(new Uint8Array(await file.arrayBuffer()));
    const omittedChars = Math.max(0, text.length - ATTACH_MAX_CHARS);
    return {
      name: file.name,
      content: omittedChars > 0 ? text.slice(0, ATTACH_MAX_CHARS) : text,
      sizeBytes: file.size,
      omittedChars,
    };
  } catch {
    return 'unreadable';
  }
}

export function toMessageAttachment(a: PendingAttachment): MessageAttachment {
  return {
    name: a.name,
    sizeBytes: a.sizeBytes,
    truncated: a.omittedChars > 0,
    preview: a.content.length > PREVIEW_CHARS ? a.content.slice(0, PREVIEW_CHARS) + '\n…' : a.content,
  };
}

export function formatSize(bytes?: number): string {
  if (bytes == null || bytes < 0) return '';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Saved sessions store "📎 name\ninstruction" instead of the body; split it back into a
 * chip + text when a session is reloaded.
 */
export function parseAttachmentMarker(text: string): { attachment: MessageAttachment; text: string } | null {
  if (!text || !text.startsWith(MARKER_PREFIX)) return null;
  const nl = text.indexOf('\n');
  const name = (nl < 0 ? text : text.slice(0, nl)).slice(MARKER_PREFIX.length).trim();
  if (!name || !isAllowedAttachment(name)) return null;
  return { attachment: { name }, text: nl < 0 ? '' : text.slice(nl + 1) };
}
