import { useState, type CSSProperties } from 'react';
import { t } from '../i18n';
import type { ProgressStep } from '../types';

interface Props {
  open: boolean;
  steps: ProgressStep[];
  onCancel: () => void;
}

// Labels posted by C# backend when Revit is actively executing compiled code.
// Must stay in sync with BibimDockablePanelProvider "progress" PostMessage calls.
const REVIT_EXECUTING_LABELS = [
  'Running preview...',
  'Applying changes...',
  '미리보기를 실행하는 중...',
  '변경 사항 적용 중...',
];

function isRevitExecuting(steps: ProgressStep[]): boolean {
  return steps.some(
    (s) => s.status === 'active' && REVIT_EXECUTING_LABELS.some((l) => s.label.includes(l))
  );
}

/**
 * Non-blocking loading banner — renders as a sticky strip at the TOP of the
 * chat scroll container instead of a blocking overlay. Users can still scroll
 * and read prior messages while the AI is working.
 */
export default function LoadingModal({ open, steps, onCancel }: Props) {
  const [confirmOpen, setConfirmOpen] = useState(false);

  if (!open) return null;

  const activeIndex = steps.findIndex((s) => s.status === 'active');
  const activeStep = activeIndex >= 0 ? steps[activeIndex] : steps[steps.length - 1] ?? null;
  const label = activeStep?.label ?? t('progress');
  const doneCount = steps.filter((s) => s.status === 'done').length;
  const totalCount = steps.length;
  const revitRunning = isRevitExecuting(steps);

  const handleStopClick = () => setConfirmOpen(true);

  const handleConfirmOk = () => {
    setConfirmOpen(false);
    if (!revitRunning) onCancel();
  };

  const handleConfirmCancel = () => setConfirmOpen(false);

  return (
    <>
      <div style={bannerStyle}>
        <style>{'@keyframes bibim-spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }'}</style>
        <div style={spinnerStyle} />

        <div style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span style={labelStyle}>{label}</span>
          {totalCount > 1 && (
            <div style={stepsRowStyle}>
              {steps.map((step, i) => (
                <span
                  key={`${step.label}-${i}`}
                  style={{
                    color: step.status === 'done'
                      ? 'var(--color-success)'
                      : step.status === 'active'
                      ? 'var(--color-accent)'
                      : 'var(--color-text-muted)',
                    fontSize: 'var(--text-xs)',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {step.status === 'done' ? '✓' : step.status === 'active' ? '●' : '○'} {step.label}
                </span>
              ))}
            </div>
          )}
        </div>

        {totalCount > 0 && (
          <span style={countStyle}>{doneCount}/{totalCount}</span>
        )}

        <button onClick={handleStopClick} style={cancelStyle}>{t('stop')}</button>
      </div>

      {confirmOpen && (
        <div style={overlayStyle}>
          <div style={dialogStyle}>
            <p style={dialogTitleStyle}>
              {revitRunning ? t('stopRevitRunningTitle') : t('stopConfirmTitle')}
            </p>
            <p style={dialogBodyStyle}>
              {revitRunning ? t('stopRevitRunningBody') : t('stopConfirmBody')}
            </p>
            <div style={dialogActionsStyle}>
              {!revitRunning && (
                <button onClick={handleConfirmCancel} style={dialogBtnSecondaryStyle}>
                  {t('stopConfirmCancel')}
                </button>
              )}
              <button
                onClick={handleConfirmOk}
                style={revitRunning ? dialogBtnPrimaryStyle : dialogBtnDangerStyle}
              >
                {revitRunning ? t('stopRevitRunningOk') : t('stopConfirmOk')}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

const bannerStyle: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  gap: 'var(--space-sm)',
  padding: 'var(--space-sm) var(--space-md)',
  background: 'var(--color-bg-secondary)',
  borderBottom: '1px solid var(--color-border)',
  flexShrink: 0,
};

const spinnerStyle: CSSProperties = {
  width: 16,
  height: 16,
  borderRadius: '50%',
  border: '2px solid rgba(15, 23, 42, 0.12)',
  borderTopColor: 'var(--color-accent)',
  animation: 'bibim-spin 0.9s linear infinite',
  flexShrink: 0,
  marginTop: 2,
};

const labelStyle: CSSProperties = {
  fontSize: 'var(--text-sm)',
  fontWeight: 600,
  color: 'var(--color-text-primary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const stepsRowStyle: CSSProperties = {
  display: 'flex',
  gap: 'var(--space-md)',
  flexWrap: 'wrap',
};

const countStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-muted)',
  whiteSpace: 'nowrap',
  marginTop: 2,
  flexShrink: 0,
};

const cancelStyle: CSSProperties = {
  padding: '2px 10px',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-xs)',
  flexShrink: 0,
};

const overlayStyle: CSSProperties = {
  position: 'fixed',
  inset: 0,
  background: 'rgba(0,0,0,0.45)',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  zIndex: 9999,
};

const dialogStyle: CSSProperties = {
  background: 'var(--color-bg-primary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-lg)',
  padding: 'var(--space-lg)',
  maxWidth: 340,
  width: '90%',
  display: 'flex',
  flexDirection: 'column',
  gap: 'var(--space-sm)',
};

const dialogTitleStyle: CSSProperties = {
  fontSize: 'var(--text-sm)',
  fontWeight: 700,
  color: 'var(--color-text-primary)',
  margin: 0,
};

const dialogBodyStyle: CSSProperties = {
  fontSize: 'var(--text-sm)',
  color: 'var(--color-text-secondary)',
  margin: 0,
  whiteSpace: 'pre-line',
  lineHeight: 1.5,
};

const dialogActionsStyle: CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  gap: 'var(--space-sm)',
  marginTop: 'var(--space-xs)',
};

const dialogBtnSecondaryStyle: CSSProperties = {
  padding: '4px 14px',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
};

const dialogBtnDangerStyle: CSSProperties = {
  padding: '4px 14px',
  borderRadius: 'var(--radius-md)',
  border: 'none',
  background: 'var(--color-error)',
  color: '#fff',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
  fontWeight: 600,
};

const dialogBtnPrimaryStyle: CSSProperties = {
  padding: '4px 14px',
  borderRadius: 'var(--radius-md)',
  border: 'none',
  background: 'var(--color-accent)',
  color: '#fff',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
  fontWeight: 600,
};
