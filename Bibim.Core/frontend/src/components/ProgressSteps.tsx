import { t } from '../i18n';
import type { ProgressStep } from '../types';

interface Props {
  steps: ProgressStep[];
}

const icons: Record<ProgressStep['status'], string> = {
  done: '✅',
  active: '🔄',
  pending: '⬜',
  error: '❌',
};

export default function ProgressSteps({ steps }: Props) {
  if (steps.length === 0) return null;

  return (
    <div style={{
      padding: 'var(--space-md)',
      background: 'var(--color-bg-secondary)',
      borderRadius: 'var(--radius-md)',
      margin: 'var(--space-sm) 0',
      fontSize: 'var(--text-sm)',
    }}>
      <div style={{
        color: 'var(--color-text-muted)',
        fontSize: 'var(--text-xs)',
        marginBottom: 'var(--space-sm)',
      }}>
        {t('progress')}
      </div>
      {steps.map((step, index) => (
        <div key={index} style={{
          display: 'flex', alignItems: 'center', gap: 'var(--space-sm)',
          padding: 'var(--space-xs) 0',
          color: step.status === 'active' ? 'var(--color-text-primary)' :
            step.status === 'done' ? 'var(--color-text-secondary)' :
              step.status === 'error' ? 'var(--color-error)' :
                'var(--color-text-muted)',
        }}>
          <span>{icons[step.status]}</span>
          <span>{index + 1}. {step.label}</span>
        </div>
      ))}
    </div>
  );
}
