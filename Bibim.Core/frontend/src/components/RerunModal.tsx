import { useEffect, useState, type CSSProperties } from 'react';
import { formatDateTime } from '../i18n';

interface Props {
  open: boolean;
  sourceTitle: string;
  sourceCreatedAt: string;
  initialCode: string;
  onConfirm: (code: string) => void;
  onClose: () => void;
}

export default function RerunModal({
  open,
  sourceTitle,
  sourceCreatedAt,
  initialCode,
  onConfirm,
  onClose,
}: Props) {
  const [code, setCode] = useState(initialCode);

  useEffect(() => {
    setCode(initialCode);
  }, [initialCode, open]);

  if (!open) return null;

  return (
    <div style={overlayStyle}>
      <div style={modalStyle}>
        <div style={{ marginBottom: 'var(--space-md)' }}>
          <div style={{ fontSize: 'var(--text-sm)', fontWeight: 600 }}>Rerun Code</div>
          <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', marginTop: 4 }}>
            Source: {sourceTitle} {sourceCreatedAt ? `| ${formatDateTime(sourceCreatedAt)}` : ''}
          </div>
        </div>

        <div style={warningStyle}>
          This code was generated for an earlier Revit model state. Review and adjust it before running.
        </div>

        <textarea
          value={code}
          onChange={(e) => setCode(e.target.value)}
          style={textareaStyle}
        />

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 'var(--space-sm)', marginTop: 'var(--space-md)' }}>
          <button onClick={onClose} style={cancelBtnStyle}>Cancel</button>
          <button onClick={() => onConfirm(code)} style={confirmBtnStyle}>Run In New Session</button>
        </div>
      </div>
    </div>
  );
}

const overlayStyle: CSSProperties = {
  position: 'fixed',
  inset: 0,
  background: 'rgba(15, 23, 42, 0.52)',
  backdropFilter: 'blur(3px)',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  zIndex: 50,
  padding: '24px',
};

const modalStyle: CSSProperties = {
  width: 'min(860px, 100%)',
  borderRadius: 'var(--radius-lg)',
  border: '1px solid var(--color-border)',
  background: 'var(--color-bg-primary)',
  boxShadow: '0 24px 60px rgba(15, 23, 42, 0.28)',
  padding: 'var(--space-lg)',
};

const warningStyle: CSSProperties = {
  padding: 'var(--space-sm) var(--space-md)',
  background: 'rgba(245, 158, 11, 0.10)',
  border: '1px solid var(--color-warning)',
  borderRadius: 'var(--radius-md)',
  fontSize: 'var(--text-xs)',
  color: 'var(--color-warning)',
  marginBottom: 'var(--space-md)',
  lineHeight: 1.6,
};

const textareaStyle: CSSProperties = {
  width: '100%',
  height: 320,
  fontFamily: 'monospace',
  fontSize: 'var(--text-xs)',
  background: 'var(--color-bg-tertiary)',
  color: 'var(--color-text-primary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  padding: 'var(--space-sm)',
  resize: 'vertical',
  boxSizing: 'border-box',
};

const cancelBtnStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-md)',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
};

const confirmBtnStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-md)',
  borderRadius: 'var(--radius-md)',
  border: 'none',
  background: 'var(--color-accent)',
  color: 'var(--color-text-inverse)',
  cursor: 'pointer',
  fontWeight: 600,
};
