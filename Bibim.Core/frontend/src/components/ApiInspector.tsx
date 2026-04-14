import { t } from '../i18n';
import type { ApiReport } from '../types';

interface Props {
  report: ApiReport;
  onExecute: (mode: 'dryrun' | 'commit') => void;
  onClose: () => void;
}

const statusIcon = { safe: '✅', versionSpecific: '⚠️', deprecated: '❌' } as const;

export default function ApiInspector({ report, onExecute, onClose }: Props) {
  return (
    <div style={{
      background: 'var(--color-bg-secondary)',
      border: '1px solid var(--color-border)',
      borderRadius: 'var(--radius-lg)',
      padding: 'var(--space-lg)',
      margin: 'var(--space-sm) 0',
    }}>
      <div style={{
        display: 'flex', justifyContent: 'space-between', alignItems: 'center',
        marginBottom: 'var(--space-md)',
      }}>
        <span style={{ fontSize: 'var(--text-sm)', color: 'var(--color-accent)' }}>
          📋 {t('apiInspector')}
        </span>
        <button onClick={onClose} style={{
          background: 'none', border: 'none', cursor: 'pointer',
          color: 'var(--color-text-muted)', fontSize: 'var(--text-sm)',
        }}>✕</button>
      </div>

      <div style={{
        maxHeight: 200, overflow: 'auto',
        marginBottom: 'var(--space-md)',
      }}>
        {report.apiUsages.map((api, index) => (
          <div key={index} style={{
            display: 'flex', alignItems: 'flex-start', gap: 'var(--space-sm)',
            padding: 'var(--space-xs) 0',
            fontSize: 'var(--text-sm)',
          }}>
            <span>{statusIcon[api.status]}</span>
            <div>
              <div style={{ color: 'var(--color-text-primary)' }}>{api.fullExpression}</div>
              {api.note && (
                <div style={{ color: 'var(--color-text-muted)', fontSize: 'var(--text-xs)' }}>
                  └ {api.note}
                </div>
              )}
            </div>
          </div>
        ))}
      </div>

      <div style={{
        fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)',
        marginBottom: 'var(--space-md)',
      }}>
        ✅ {report.safeCount} {t('safe')} · ⚠️ {report.versionSpecificCount} {t('versionSpecific')} · ❌ {report.deprecatedCount} {t('deprecated')}
      </div>

      {report.dryRunSummary && (
        <div style={{
          padding: 'var(--space-sm) var(--space-md)',
          background: report.dryRunSummary.success
            ? 'rgba(34,197,94,0.1)' : 'rgba(239,68,68,0.1)',
          borderRadius: 'var(--radius-md)',
          fontSize: 'var(--text-sm)',
          marginBottom: 'var(--space-md)',
        }}>
          {report.dryRunSummary.success
            ? `📊 ${t('affectedElements')}: ${report.dryRunSummary.affectedElementCount}`
            : `❌ ${report.dryRunSummary.errorMessage}`}
        </div>
      )}

      {report.analyzerDiagnostics && report.analyzerDiagnostics.length > 0 && (
        <div style={{ marginBottom: 'var(--space-md)' }}>
          <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', marginBottom: 'var(--space-xs)' }}>
            {t('analyzer')}
          </div>
          {report.analyzerDiagnostics.map((diag, index) => (
            <div key={index} style={{
              fontSize: 'var(--text-xs)',
              color: diag.severity === 'error' ? 'var(--color-error)' : 'var(--color-warning)',
              padding: 'var(--space-xs) 0',
            }}>
              [{diag.id}] L{diag.line}: {diag.message}
            </div>
          ))}
        </div>
      )}

      <div style={{ display: 'flex', gap: 'var(--space-sm)' }}>
        <button onClick={() => onExecute('dryrun')} style={{
          flex: 1, padding: 'var(--space-sm)',
          background: 'var(--color-accent-muted)',
          border: '1px solid var(--color-accent)',
          borderRadius: 'var(--radius-md)',
          color: 'var(--color-accent)',
          fontSize: 'var(--text-sm)', cursor: 'pointer',
        }}>
          {t('dryRun')}
        </button>
        <button onClick={() => onExecute('commit')} style={{
          flex: 1, padding: 'var(--space-sm)',
          background: 'var(--color-accent)',
          border: 'none',
          borderRadius: 'var(--radius-md)',
          color: 'var(--color-text-inverse)',
          fontSize: 'var(--text-sm)', cursor: 'pointer',
        }}>
          {t('execute')}
        </button>
      </div>
    </div>
  );
}
