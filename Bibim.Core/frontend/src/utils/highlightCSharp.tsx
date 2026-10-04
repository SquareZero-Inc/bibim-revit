import React from 'react';

/**
 * Tiny dependency-free C# syntax highlighter (keywords / strings / comments /
 * numbers). Token-priority regex, React elements only (XSS-safe), no CDN —
 * WebView2 runs offline. Good enough for generated Revit scripts; not a
 * general-purpose grammar.
 */

const KEYWORDS = new Set([
  'abstract','as','base','bool','break','case','catch','char','checked','class',
  'const','continue','decimal','default','delegate','do','double','else','enum',
  'event','explicit','extern','false','finally','fixed','float','for','foreach',
  'goto','if','implicit','in','int','interface','internal','is','lock','long',
  'namespace','new','null','object','operator','out','override','params',
  'private','protected','public','readonly','ref','return','sbyte','sealed',
  'short','sizeof','stackalloc','static','string','struct','switch','this',
  'throw','true','try','typeof','uint','ulong','unchecked','unsafe','ushort',
  'using','var','virtual','void','volatile','while','async','await','get','set',
]);

// Order matters: comments > strings > numbers > words.
// String alternation order matters: verbatim forms (@"..." — backslash is
// LITERAL, only "" escapes a quote) must be tried before regular strings, or a
// trailing-backslash path like @"C:\Temp\" swallows its closing quote and
// bleeds string color across the rest of the file.
const TOKEN_RE =
  /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|((?:\$@|@\$|@)"(?:[^"]|"")*"|\$?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)')|(\b\d[\d_]*(?:\.\d+)?[fFdDmML]?\b)|([A-Za-z_][A-Za-z0-9_]*)/g;

const COLORS = {
  comment: 'var(--color-text-muted)',
  str: '#7cc379',
  num: '#d19a66',
  kw: '#79b8ff',
};

export function highlightCSharp(code: string): React.ReactNode[] {
  const out: React.ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  let i = 0;
  TOKEN_RE.lastIndex = 0;
  while ((m = TOKEN_RE.exec(code)) !== null) {
    if (m.index > last) out.push(code.slice(last, m.index));
    const [full, comment, str, num, word] = m;
    if (comment !== undefined) {
      out.push(<span key={i++} style={{ color: COLORS.comment, fontStyle: 'italic' }}>{full}</span>);
    } else if (str !== undefined) {
      out.push(<span key={i++} style={{ color: COLORS.str }}>{full}</span>);
    } else if (num !== undefined) {
      out.push(<span key={i++} style={{ color: COLORS.num }}>{full}</span>);
    } else if (word !== undefined && KEYWORDS.has(word)) {
      out.push(<span key={i++} style={{ color: COLORS.kw }}>{full}</span>);
    } else {
      out.push(full);
    }
    last = m.index + full.length;
  }
  if (last < code.length) out.push(code.slice(last));
  return out;
}
