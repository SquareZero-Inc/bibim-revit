import React from 'react';
import { sendToBackend } from '../bridge';

/**
 * Minimal, dependency-free markdown renderer for assistant messages.
 *
 * LLM replies are markdown-heavy (bold, lists, headings, tables) but the panel
 * used to render everything except ``` fences as plain pre-wrap text, so users
 * saw raw `**` / `-` / `#` characters. A hand-rolled renderer keeps the bundle
 * small and works offline (WebView2 — no CDN), and because it emits React
 * elements (never innerHTML) it is XSS-safe by construction.
 *
 * Supported: # ## ### headings, - / * / 1. lists, > blockquote, --- rule,
 * | tables (with header separator), **bold**, `inline code`, [text](url).
 * Deliberately NOT supported: *single-asterisk italic* (false-positives on
 * Korean text patterns), images, raw HTML.
 */

const BOLD_RE = /\*\*([^*]+)\*\*/g;
const CODE_RE = /`([^`]+)`/g;
const LINK_RE = /\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g;

function renderInline(text: string, keyPrefix: string): React.ReactNode[] {
  // Tokenize by priority: inline code > bold > link. Single pass with a
  // combined regex keeps ordering stable.
  const combined = new RegExp(
    `${CODE_RE.source}|${BOLD_RE.source}|${LINK_RE.source}`, 'g',
  );
  const out: React.ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  let i = 0;
  while ((m = combined.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const [, code, bold, linkText, linkUrl] = m;
    if (code !== undefined) {
      out.push(
        <code
          key={`${keyPrefix}-c${i++}`}
          style={{
            background: 'var(--color-bg-tertiary)',
            border: '1px solid var(--color-border)',
            borderRadius: 4,
            padding: '0 4px',
            fontSize: '0.92em',
          }}
        >{code}</code>,
      );
    } else if (bold !== undefined) {
      out.push(<strong key={`${keyPrefix}-b${i++}`}>{bold}</strong>);
    } else if (linkText !== undefined && linkUrl !== undefined) {
      // Security cue: LLM-authored links shell-open in the default browser, and
      // display text is free-form — always show the real host inline so
      // "[autodesk.com](https://evil...)" cannot masquerade.
      let host = '';
      try { host = new URL(linkUrl).host; } catch { /* leave empty */ }
      const disguised = host && !linkText.includes(host);
      out.push(
        <span
          key={`${keyPrefix}-l${i++}`}
          onClick={() => sendToBackend('open_url', { url: linkUrl })}
          title={linkUrl}
          style={{
            color: 'var(--color-accent)',
            textDecoration: 'underline',
            cursor: 'pointer',
          }}
        >{linkText}{disguised ? <span style={{ color: 'var(--color-text-muted)', textDecoration: 'none' }}> ({host})</span> : null}</span>,
      );
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

function isTableRow(line: string): boolean {
  const t = line.trim();
  return t.startsWith('|') && t.endsWith('|') && t.length > 2;
}

function isTableSeparator(line: string): boolean {
  const t = line.trim();
  return isTableRow(t) && /^\|[\s:|-]+\|$/.test(t);
}

function splitCells(line: string): string[] {
  const t = line.trim();
  return t.slice(1, -1).split('|').map((c) => c.trim());
}

const cellStyle: React.CSSProperties = {
  border: '1px solid var(--color-border)',
  padding: '4px 8px',
  textAlign: 'left',
  verticalAlign: 'top',
};

/** Render one markdown text segment (no ``` fences — caller splits those). */
export function renderMarkdown(text: string, keyPrefix = 'md'): React.ReactNode {
  const lines = text.split('\n');
  const blocks: React.ReactNode[] = [];
  let para: string[] = [];
  let k = 0;

  const flushPara = () => {
    if (para.length === 0) return;
    const joined = para.join('\n');
    blocks.push(
      <p key={`${keyPrefix}-p${k++}`} style={{ margin: '2px 0', whiteSpace: 'pre-wrap' }}>
        {renderInline(joined, `${keyPrefix}-p${k}`)}
      </p>,
    );
    para = [];
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    // horizontal rule
    if (/^(-{3,}|_{3,}|\*{3,})$/.test(trimmed)) {
      flushPara();
      blocks.push(<hr key={`${keyPrefix}-hr${k++}`} style={{ border: 'none', borderTop: '1px solid var(--color-border)', margin: '8px 0' }} />);
      continue;
    }

    // heading
    const h = /^(#{1,3})\s+(.*)$/.exec(trimmed);
    if (h) {
      flushPara();
      const level = h[1].length;
      const size = level === 1 ? 'var(--text-lg)' : level === 2 ? 'var(--text-base)' : 'var(--text-sm)';
      blocks.push(
        <div key={`${keyPrefix}-h${k++}`} style={{ fontWeight: 700, fontSize: size, margin: '8px 0 2px' }}>
          {renderInline(h[2], `${keyPrefix}-h${k}`)}
        </div>,
      );
      continue;
    }

    // blockquote — bare '>' is the standard blank-line separator inside quotes
    if (trimmed === '>' || trimmed.startsWith('> ')) {
      flushPara();
      const stripQuote = (l: string) => (l === '>' ? '' : l.slice(2));
      const quoted: string[] = [stripQuote(trimmed)];
      while (i + 1 < lines.length) {
        const nt = lines[i + 1].trim();
        if (nt !== '>' && !nt.startsWith('> ')) break;
        quoted.push(stripQuote(lines[++i].trim()));
      }
      blocks.push(
        <div key={`${keyPrefix}-q${k++}`} style={{
          borderLeft: '3px solid var(--color-border)',
          paddingLeft: 8, margin: '4px 0',
          color: 'var(--color-text-muted)', whiteSpace: 'pre-wrap',
        }}>
          {renderInline(quoted.join('\n'), `${keyPrefix}-q${k}`)}
        </div>,
      );
      continue;
    }

    // table (header row + separator row)
    if (isTableRow(trimmed) && i + 1 < lines.length && isTableSeparator(lines[i + 1])) {
      flushPara();
      const header = splitCells(trimmed);
      i += 1; // skip separator
      const rows: string[][] = [];
      while (i + 1 < lines.length && isTableRow(lines[i + 1].trim())) {
        rows.push(splitCells(lines[++i].trim()));
      }
      blocks.push(
        <div key={`${keyPrefix}-tw${k++}`} style={{ overflowX: 'auto', margin: '6px 0' }}>
          <table style={{ borderCollapse: 'collapse', fontSize: 'var(--text-xs)', minWidth: 200 }}>
            <thead>
              <tr>
                {header.map((c, ci) => (
                  <th key={ci} style={{ ...cellStyle, background: 'var(--color-bg-tertiary)', fontWeight: 600 }}>
                    {renderInline(c, `${keyPrefix}-th${k}-${ci}`)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((r, ri) => (
                <tr key={ri}>
                  {header.map((_, ci) => (
                    <td key={ci} style={cellStyle}>
                      {renderInline(r[ci] ?? '', `${keyPrefix}-td${k}-${ri}-${ci}`)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>,
      );
      continue;
    }

    // lists (unordered / ordered), with simple 2-space nesting
    const li = /^(\s*)([-*]|\d{1,2}\.)\s+(.*)$/.exec(line);
    if (li) {
      flushPara();
      type Item = { indent: number; marker: string; text: string };
      const items: Item[] = [{
        indent: Math.floor(li[1].length / 2),
        marker: /\d/.test(li[2]) ? li[2] : '•',
        text: li[3],
      }];
      while (i + 1 < lines.length) {
        const next = /^(\s*)([-*]|\d{1,2}\.)\s+(.*)$/.exec(lines[i + 1]);
        if (!next) break;
        i += 1;
        items.push({
          indent: Math.floor(next[1].length / 2),
          marker: /\d/.test(next[2]) ? next[2] : '•',
          text: next[3],
        });
      }
      blocks.push(
        <div key={`${keyPrefix}-ul${k++}`} style={{ margin: '2px 0' }}>
          {items.map((it, ii) => (
            <div key={ii} style={{ display: 'flex', gap: 6, paddingLeft: 4 + it.indent * 14 }}>
              <span style={{ color: 'var(--color-text-muted)', flexShrink: 0 }}>
                {it.marker}
              </span>
              <span style={{ whiteSpace: 'pre-wrap' }}>{renderInline(it.text, `${keyPrefix}-li${k}-${ii}`)}</span>
            </div>
          ))}
        </div>,
      );
      continue;
    }

    // blank line = paragraph break
    if (trimmed === '') {
      flushPara();
      continue;
    }

    para.push(line);
  }
  flushPara();

  return <>{blocks}</>;
}

