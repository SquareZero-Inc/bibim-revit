import { useState } from 'react';
import { t } from '../i18n';

interface Props {
  code: string;
  language?: string;
}

const COLLAPSE_THRESHOLD_LINES = 20;

export default function CodeBlock({ code, language = 'csharp' }: Props) {
  const [copied, setCopied] = useState(false);
  const [expanded, setExpanded] = useState(false);

  const lineCount = code.split('\n').length;
  const canExpand = lineCount > COLLAPSE_THRESHOLD_LINES;

  const handleCopy = () => {
    navigator.clipboard.writeText(code).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  return (
    <div style={{
      position: 'relative',
      background: 'var(--color-bg-code)',
      borderRadius: 'var(--radius-md)',
      margin: 'var(--space-sm) 0',
      overflow: 'hidden',
    }}>
      <div style={{
        display: 'flex',
        justifyContent: 'space-between',
        alignItems: 'center',
        padding: 'var(--space-xs) var(--space-md)',
        background: 'var(--color-bg-tertiary)',
        fontSize: 'var(--text-xs)',
        color: 'var(--color-text-muted)',
      }}>
        <span>{language}</span>
        <div style={{ display: 'flex', gap: 'var(--space-sm)', alignItems: 'center' }}>
          {canExpand && (
            <button
              onClick={() => setExpanded((prev) => !prev)}
              style={{
                background: 'none', border: 'none', cursor: 'pointer',
                color: 'var(--color-text-muted)',
                fontSize: 'var(--text-xs)',
              }}
            >
              {expanded ? t('collapse2') : t('expand')}
            </button>
          )}
          <button
            onClick={handleCopy}
            style={{
              background: 'none', border: 'none', cursor: 'pointer',
              color: copied ? 'var(--color-success)' : 'var(--color-text-muted)',
              fontSize: 'var(--text-xs)',
            }}
          >
            {copied ? `✓ ${t('copied')}` : t('copy')}
          </button>
        </div>
      </div>
      <pre style={{
        padding: 'var(--space-md)',
        overflow: 'auto',
        maxHeight: canExpand && !expanded ? '400px' : 'none',
        fontSize: 'var(--text-sm)',
        lineHeight: 'var(--leading-relaxed)',
        color: 'var(--color-text-primary)',
        margin: 0,
      }}>
        <code>{code}</code>
      </pre>
      {canExpand && !expanded && (
        <div
          style={{
            position: 'absolute',
            bottom: 0,
            left: 0,
            right: 0,
            height: 48,
            background: 'linear-gradient(to bottom, transparent, var(--color-bg-code))',
            display: 'flex',
            alignItems: 'flex-end',
            justifyContent: 'center',
            paddingBottom: 6,
          }}
        >
          <button
            onClick={() => setExpanded(true)}
            style={{
              background: 'var(--color-bg-tertiary)',
              border: '1px solid var(--color-border)',
              borderRadius: 'var(--radius-sm)',
              cursor: 'pointer',
              color: 'var(--color-text-muted)',
              fontSize: 'var(--text-xs)',
              padding: '2px 10px',
            }}
          >
            {t('expand')} ({lineCount} lines)
          </button>
        </div>
      )}
    </div>
  );
}
