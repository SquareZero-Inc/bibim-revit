import { useState } from 'react';
import { t } from '../i18n';
import type { SpecData } from '../types';

interface Props {
  spec: SpecData;
  onAction: (action: 'confirm' | 'revise' | 'reject', feedback?: string) => void;
}

export default function SpecCard({ spec, onAction }: Props) {
  const [feedback, setFeedback] = useState('');
  const [showRevise, setShowRevise] = useState(false);

  return (
    <div style={{
      background: 'var(--color-bg-secondary)',
      border: '1px solid var(--color-border)',
      borderRadius: 'var(--radius-lg)',
      padding: 'var(--space-lg)',
      margin: 'var(--space-sm) 0',
    }}>
      <div style={{
        fontSize: 'var(--text-sm)',
        color: 'var(--color-accent)',
        marginBottom: 'var(--space-sm)',
      }}>
        📋 {t('specReview')}
      </div>

      <div style={{
        fontSize: 'var(--text-base)',
        fontWeight: 600,
        marginBottom: 'var(--space-sm)',
      }}>
        {spec.title}
      </div>

      <div style={{
        fontSize: 'var(--text-sm)',
        color: 'var(--color-text-secondary)',
        marginBottom: 'var(--space-md)',
      }}>
        {spec.description}
      </div>

      {spec.steps.length > 0 && (
        <ol style={{
          paddingLeft: 'var(--space-lg)',
          fontSize: 'var(--text-sm)',
          color: 'var(--color-text-secondary)',
          marginBottom: 'var(--space-md)',
        }}>
          {spec.steps.map((step, index) => (
            <li key={index} style={{ padding: 'var(--space-xs) 0' }}>{step}</li>
          ))}
        </ol>
      )}

      {showRevise && (
        <textarea
          value={feedback}
          onChange={(e) => setFeedback(e.target.value)}
          placeholder={t('revisePlaceholder')}
          style={{
            width: '100%', minHeight: 60,
            background: 'var(--color-bg-input)',
            border: '1px solid var(--color-border)',
            borderRadius: 'var(--radius-md)',
            padding: 'var(--space-sm)',
            color: 'var(--color-text-primary)',
            fontSize: 'var(--text-sm)',
            resize: 'vertical',
            marginBottom: 'var(--space-sm)',
          }}
        />
      )}

      <div style={{ display: 'flex', gap: 'var(--space-sm)' }}>
        <button onClick={() => onAction('confirm')} style={btnStyle('var(--color-success)')}>
          {t('confirm')}
        </button>
        <button
          onClick={() => {
            if (showRevise && feedback.trim()) {
              onAction('revise', feedback);
            } else {
              setShowRevise(true);
            }
          }}
          style={btnStyle('var(--color-warning)')}
        >
          {t('revise')}
        </button>
        <button onClick={() => onAction('reject')} style={btnStyle('var(--color-error)')}>
          {t('reject')}
        </button>
      </div>
    </div>
  );
}

function btnStyle(color: string): React.CSSProperties {
  return {
    padding: 'var(--space-xs) var(--space-lg)',
    background: 'transparent',
    border: `1px solid ${color}`,
    borderRadius: 'var(--radius-md)',
    color,
    fontSize: 'var(--text-sm)',
    cursor: 'pointer',
  };
}
