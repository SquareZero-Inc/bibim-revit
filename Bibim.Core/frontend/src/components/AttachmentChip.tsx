import { useState } from 'react';
import { t } from '../i18n';
import { formatSize, type MessageAttachment } from '../utils/attachment';

interface Props {
  attachment: MessageAttachment;
  /** Input-area chip: shows [×]. */
  onRemove?: () => void;
  /** Bubble chip: click toggles the document preview below it. */
  expandable?: boolean;
}

export function DocIcon({ size = 16 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"
      style={{ flexShrink: 0 }}>
      <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
      <path d="M14 3v5h5" />
      <path d="M9 13h6M9 17h6" />
    </svg>
  );
}

export function PaperclipIcon({ size = 18 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M21.4 11.1l-9.2 9.2a6 6 0 0 1-8.5-8.5l9.2-9.2a4 4 0 0 1 5.7 5.7l-9.2 9.2a2 2 0 0 1-2.8-2.8l8.5-8.5" />
    </svg>
  );
}

export default function AttachmentChip({ attachment, onRemove, expandable }: Props) {
  const [open, setOpen] = useState(false);
  const canExpand = Boolean(expandable && attachment.preview);
  const size = formatSize(attachment.sizeBytes);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-xs)', minWidth: 0 }}>
      <div
        role={canExpand ? 'button' : undefined}
        tabIndex={canExpand ? 0 : undefined}
        title={canExpand ? (open ? t('attachCollapse') : t('attachExpand')) : attachment.name}
        onClick={canExpand ? () => setOpen((v) => !v) : undefined}
        onKeyDown={canExpand ? (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen((v) => !v); } } : undefined}
        style={{
          display: 'inline-flex', alignItems: 'center', gap: 6,
          alignSelf: 'flex-start', maxWidth: '100%',
          padding: '4px 8px',
          background: 'var(--color-bg-secondary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-md)',
          color: 'var(--color-text-primary)',
          // Same size as bubble text (spec: chip font ≥ message font).
          fontSize: 'var(--text-sm)',
          lineHeight: 1.4,
          cursor: canExpand ? 'pointer' : 'default',
        }}
      >
        <span style={{ color: 'var(--color-accent)', display: 'inline-flex' }}><DocIcon /></span>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 }}>
          {attachment.name}
        </span>
        {size && <span style={{ color: 'var(--color-text-muted)', whiteSpace: 'nowrap' }}>· {size}</span>}
        {attachment.truncated && (
          <span style={{
            padding: '0 6px', borderRadius: 'var(--radius-sm, 4px)',
            background: 'rgba(245, 158, 11, 0.15)', color: 'var(--color-warning)',
            fontSize: 'var(--text-xs)', fontWeight: 600, whiteSpace: 'nowrap',
          }}>
            {t('attachTruncated')}
          </span>
        )}
        {canExpand && (
          <span style={{ color: 'var(--color-text-muted)', fontSize: 'var(--text-xs)' }}>{open ? '▲' : '▼'}</span>
        )}
        {onRemove && (
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); onRemove(); }}
            title={t('attachRemove')}
            aria-label={t('attachRemove')}
            style={{
              marginLeft: 2, padding: '0 4px', lineHeight: 1,
              background: 'none', border: 'none', cursor: 'pointer',
              color: 'var(--color-text-muted)', fontSize: 'var(--text-base, 16px)',
            }}
          >
            ×
          </button>
        )}
      </div>
      {canExpand && open && (
        <pre style={{
          margin: 0, maxHeight: 260, overflow: 'auto',
          padding: 'var(--space-sm)',
          background: 'var(--color-bg-tertiary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-md)',
          fontFamily: 'var(--font-mono)', fontSize: 'var(--text-xs)',
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          color: 'var(--color-text-secondary)',
          textAlign: 'left',
        }}>
          {attachment.preview}
        </pre>
      )}
    </div>
  );
}
