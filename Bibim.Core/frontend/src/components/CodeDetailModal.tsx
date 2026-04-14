import { useState, type CSSProperties } from 'react';
import { formatDateTime, t } from '../i18n';
import type { CodeSnippetDetail } from '../types';

interface Props {
  snippet: CodeSnippetDetail;
  onRun: (code: string) => void;
  onEdit: (code: string) => void;
  onDelete: (id: string) => void;
  onClose: () => void;
}

export default function CodeDetailModal({ snippet, onRun, onEdit, onDelete, onClose }: Props) {
  const [copied, setCopied] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const handleCopy = () => {
    navigator.clipboard.writeText(snippet.code).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };

  const handleDeleteRequest = () => {
    setConfirmingDelete(true);
  };

  const handleDeleteConfirm = () => {
    onDelete(snippet.id);
    setConfirmingDelete(false);
  };

  const handleDeleteCancel = () => {
    setConfirmingDelete(false);
  };

  const previewLines = snippet.code.split('\n').slice(0, 15);
  const hasMore = snippet.code.split('\n').length > 15;

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={modalStyle} onClick={(e) => e.stopPropagation()}>
        {/* Header */}
        <div style={headerStyle}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={titleStyle}>{snippet.title || '—'}</div>
            <div style={metaStyle}>
              {formatDateTime(snippet.createdAt)} · Revit {snippet.revitVersion || '—'} ·{' '}
              {snippet.taskKind === 'read' ? 'Read' : 'Write'}
            </div>
          </div>
          <button onClick={onClose} style={closeBtnStyle}>✕</button>
        </div>

        {/* Summary */}
        {snippet.summary && (
          <div style={summaryStyle}>{snippet.summary}</div>
        )}

        {/* Warning */}
        <div style={warningStyle}>⚠ {t('codeWarning')}</div>

        {/* Code preview */}
        <div style={codeSectionStyle}>
          <div style={codeLabelRow}>
            <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)' }}>
              {t('codePreview')}
            </span>
            <button onClick={handleCopy} style={copyBtnStyle}>
              {copied ? t('copied') : t('copy')}
            </button>
          </div>
          <pre style={codeBlockStyle}>
            {previewLines.join('\n')}{hasMore ? '\n// ...' : ''}
          </pre>
        </div>

        {/* Actions */}
        {confirmingDelete ? (
          <div style={{ ...actionRow, background: 'var(--color-bg-tertiary)', borderRadius: 'var(--radius-md)', padding: 'var(--space-sm) var(--space-md)' }}>
            <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-secondary)', flex: 1 }}>
              {t('confirmDelete')}
            </span>
            <button onClick={handleDeleteConfirm} style={{ ...primaryBtnStyle, background: 'var(--color-error)', padding: 'var(--space-xs) var(--space-md)' }}>
              {t('deleteSnippet')}
            </button>
            <button onClick={handleDeleteCancel} style={secondaryBtnStyle}>
              {t('cancelRename')}
            </button>
          </div>
        ) : (
          <div style={actionRow}>
            <button onClick={handleDeleteRequest} style={deleteBtnStyle}>{t('deleteSnippet')}</button>
            <div style={{ flex: 1 }} />
            <button onClick={() => onEdit(snippet.code)} style={secondaryBtnStyle}>{t('editCode')}</button>
            <button onClick={() => onRun(snippet.code)} style={primaryBtnStyle}>{t('runCode')}</button>
          </div>
        )}
      </div>
    </div>
  );
}

const overlayStyle: CSSProperties = {
  position: 'fixed',
  inset: 0,
  background: 'rgba(0,0,0,0.5)',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  zIndex: 100,
};

const modalStyle: CSSProperties = {
  background: 'var(--color-bg-primary)',
  borderRadius: 'var(--radius-lg)',
  width: '90%',
  maxWidth: 560,
  maxHeight: '80vh',
  overflow: 'auto',
  padding: 'var(--space-lg)',
  boxShadow: '0 16px 48px rgba(0,0,0,0.3)',
};

const headerStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: 'var(--space-sm)',
  marginBottom: 'var(--space-md)',
};

const titleStyle: CSSProperties = {
  fontSize: 'var(--text-base)',
  fontWeight: 600,
  color: 'var(--color-text-primary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const metaStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-muted)',
  marginTop: 'var(--space-xs)',
};

const closeBtnStyle: CSSProperties = {
  background: 'none',
  border: 'none',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-base)',
  padding: 'var(--space-xs)',
};

const summaryStyle: CSSProperties = {
  fontSize: 'var(--text-sm)',
  color: 'var(--color-text-secondary)',
  marginBottom: 'var(--space-md)',
  lineHeight: 1.5,
};

const warningStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-warning)',
  background: 'var(--color-bg-tertiary)',
  padding: 'var(--space-sm) var(--space-md)',
  borderRadius: 'var(--radius-md)',
  marginBottom: 'var(--space-md)',
};

const codeSectionStyle: CSSProperties = {
  marginBottom: 'var(--space-md)',
};

const codeLabelRow: CSSProperties = {
  display: 'flex',
  justifyContent: 'space-between',
  alignItems: 'center',
  marginBottom: 'var(--space-xs)',
};

const copyBtnStyle: CSSProperties = {
  background: 'none',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-sm)',
  cursor: 'pointer',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  padding: '2px 8px',
};

const codeBlockStyle: CSSProperties = {
  background: 'var(--color-bg-secondary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  padding: 'var(--space-md)',
  fontSize: 'var(--text-xs)',
  fontFamily: 'monospace',
  overflow: 'auto',
  maxHeight: 240,
  whiteSpace: 'pre',
  color: 'var(--color-text-primary)',
  margin: 0,
};

const actionRow: CSSProperties = {
  display: 'flex',
  gap: 'var(--space-sm)',
  alignItems: 'center',
};

const primaryBtnStyle: CSSProperties = {
  padding: 'var(--space-sm) var(--space-lg)',
  background: 'var(--color-accent)',
  border: 'none',
  borderRadius: 'var(--radius-md)',
  color: '#fff',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
  fontWeight: 600,
};

const secondaryBtnStyle: CSSProperties = {
  ...primaryBtnStyle,
  background: 'var(--color-bg-tertiary)',
  color: 'var(--color-text-primary)',
};

const deleteBtnStyle: CSSProperties = {
  ...primaryBtnStyle,
  background: 'transparent',
  color: 'var(--color-error)',
  fontWeight: 400,
};
