import { useEffect, useState, type CSSProperties } from 'react';
import { formatTime, t } from '../i18n';
import type { TaskItem, TaskStage, TaskSummary } from '../types';

interface Props {
  task: TaskItem | null;
  tasks: TaskSummary[];
  isBusy: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  onApply: () => void;
}

const stageColors: Record<TaskStage, string> = {
  needs_details: 'var(--color-warning)',
  review: 'var(--color-accent)',
  working: 'var(--color-accent)',
  preview_ready: 'var(--color-success)',
  completed: 'var(--color-success)',
  cancelled: 'var(--color-text-muted)',
};

export default function CurrentTaskPanel({
  task,
  tasks,
  isBusy,
  onConfirm,
  onCancel,
  onApply,
}: Props) {
  const [expanded, setExpanded] = useState(false);
  const [showTaskList, setShowTaskList] = useState(false);

  useEffect(() => {
    if (!task) {
      setExpanded(false);
      return;
    }

    if (task.autoOpen) {
      setExpanded(true);
    }
  }, [task?.taskId, task?.updatedAt, task?.autoOpen]);

  if (!task && tasks.length === 0) {
    return null;
  }

  const color = task
    ? (task.stage === 'completed' && task.hasError)
      ? 'var(--color-error)'
      : stageColors[task.stage]
    : 'var(--color-text-muted)';

  return (
    <div style={{
      border: '1px solid var(--color-border)',
      borderRadius: 'var(--radius-lg)',
      background: 'var(--color-bg-secondary)',
      overflow: 'hidden',
    }}>
      <div style={{
        display: 'flex',
        alignItems: 'center',
        gap: 'var(--space-sm)',
        padding: 'var(--space-sm) var(--space-md)',
        borderBottom: expanded ? '1px solid var(--color-border)' : 'none',
      }}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0, flex: 1 }}>
          <div style={{
            display: 'flex',
            alignItems: 'center',
            gap: 'var(--space-sm)',
            minWidth: 0,
          }}>
            <span style={{
              fontSize: 'var(--text-xs)',
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
              color: 'var(--color-text-muted)',
            }}>
              {t('currentTask')}
            </span>
            {task && (
              <span style={{
                display: 'inline-flex',
                alignItems: 'center',
                padding: '2px 8px',
                borderRadius: 999,
                border: `1px solid ${color}`,
                color,
                fontSize: 'var(--text-xs)',
                whiteSpace: 'nowrap',
              }}>
                {task.stage === 'completed' && task.hasError
                  ? t('stageCompletedWithErrors')
                  : stageLabel(task.stage)}
              </span>
            )}
          </div>

          <div style={{
            fontSize: 'var(--text-sm)',
            fontWeight: 600,
            color: 'var(--color-text-primary)',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}>
            {task?.title ?? t('noCurrentTask')}
          </div>
        </div>

        <button onClick={() => setShowTaskList((prev) => !prev)} style={ghostButtonStyle}>
          {showTaskList ? t('hideTaskList') : t('taskList')}
        </button>
        {task && (
          <button onClick={() => setExpanded((prev) => !prev)} style={ghostButtonStyle}>
            {expanded ? t('collapse') : t('details')}
          </button>
        )}
      </div>

      {expanded && task && (
        <div style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 'var(--space-sm)',
          padding: 'var(--space-md)',
        }}>
          <div style={{
            fontSize: 'var(--text-sm)',
            color: 'var(--color-text-secondary)',
            whiteSpace: 'pre-wrap',
          }}>
            {task.summary}
          </div>

          {task.steps.length > 0 && (
            <div>
              <div style={sectionLabelStyle}>{t('taskPlan')}</div>
              <ol style={listStyle}>
                {task.steps.map((step, index) => (
                  <li key={`${task.taskId}-step-${index}`}>{step}</li>
                ))}
              </ol>
            </div>
          )}

          {task.stage === 'needs_details' && task.questions.length > 0 && (
            <div>
              <div style={sectionLabelStyle}>{t('moreDetailsNeeded')}</div>
              <ol style={listStyle}>
                {task.questions.map((question, index) => (
                  <li key={`${task.taskId}-question-${index}`}>{typeof question === 'string' ? question : question.text}</li>
                ))}
              </ol>
            </div>
          )}

          {task.review && (
            <div>
              <div style={sectionLabelStyle}>{t('reviewResults')}</div>
              <div style={reviewGridStyle}>
                <ReviewMetric label={t('safeApis')} value={task.review.safeCount} />
                <ReviewMetric label={t('versionWarnings')} value={task.review.versionSpecificCount} />
                <ReviewMetric label={t('deprecatedApis')} value={task.review.deprecatedCount} />
                <ReviewMetric label={t('affectedElements')} value={task.review.affectedElementCount} />
              </div>
              {task.review.previewError && (
                <div style={{ ...hintStyle, color: 'var(--color-error)' }}>
                  {task.review.previewError}
                </div>
              )}
              {task.review.analyzerDiagnostics && task.review.analyzerDiagnostics.length > 0 && (
                <div style={{ marginTop: 'var(--space-sm)' }}>
                  <div style={sectionLabelStyle}>{t('codeReview')}</div>
                  {task.review.analyzerDiagnostics.slice(0, 4).map((diag, index) => (
                    <div key={`${task.taskId}-diag-${index}`} style={hintStyle}>
                      [{diag.id}] L{diag.line}: {diag.message}
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}

          {task.resultSummary && (
            <div style={hintStyle}>{task.resultSummary}</div>
          )}

          <div style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            gap: 'var(--space-sm)',
            flexWrap: 'wrap',
          }}>
            <div style={hintStyle}>
              {t('updatedAt')}: {formatTime(task.updatedAt)}
            </div>

            <div style={{ display: 'flex', gap: 'var(--space-sm)', flexWrap: 'wrap' }}>
              {task.stage === 'review' && (
                <>
                  <button
                    onClick={onConfirm}
                    style={{ ...primaryButtonStyle, opacity: isBusy ? 0.6 : 1, cursor: isBusy ? 'not-allowed' : 'pointer' }}
                    disabled={isBusy}
                    title={isBusy ? t('stageWorking') : undefined}
                  >
                    {isBusy ? '⋯' : t('confirmTask')}
                  </button>
                  <button onClick={onCancel} style={secondaryButtonStyle} disabled={isBusy}>
                    {t('cancelTask')}
                  </button>
                </>
              )}

              {task.stage === 'preview_ready' && task.requiresApply && (
                <>
                  <button
                    onClick={onApply}
                    style={{ ...primaryButtonStyle, opacity: isBusy ? 0.6 : 1, cursor: isBusy ? 'not-allowed' : 'pointer' }}
                    disabled={isBusy}
                    title={isBusy ? t('applying') : undefined}
                  >
                    {isBusy ? t('applying') : t('applyChanges')}
                  </button>
                  <button onClick={onCancel} style={secondaryButtonStyle} disabled={isBusy}>
                    {t('cancelTask')}
                  </button>
                </>
              )}
            </div>
          </div>
        </div>
      )}

      {showTaskList && (
        <div style={{
          borderTop: '1px solid var(--color-border)',
          padding: 'var(--space-sm) var(--space-md)',
          display: 'flex',
          flexDirection: 'column',
          gap: 'var(--space-xs)',
        }}>
          <div style={sectionLabelStyle}>{t('currentSessionTasks')}</div>
          {tasks.length === 0 && (
            <div style={hintStyle}>{t('noTasksYet')}</div>
          )}
          {tasks.map((item) => {
            const isActive = item.taskId === task?.taskId;
            return (
              <div
                key={item.taskId}
                style={{
                  padding: 'var(--space-sm)',
                  borderRadius: 'var(--radius-md)',
                  border: `1px solid ${isActive ? 'var(--color-accent)' : 'var(--color-border)'}`,
                  background: isActive ? 'var(--color-accent-muted)' : 'var(--color-bg-primary)',
                }}
              >
                <div style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  gap: 'var(--space-sm)',
                }}>
                  <div style={{
                    fontSize: 'var(--text-sm)',
                    fontWeight: 600,
                    color: 'var(--color-text-primary)',
                  }}>
                    {item.title}
                  </div>
                  <div style={{
                    fontSize: 'var(--text-xs)',
                    color: stageColors[item.stage],
                    whiteSpace: 'nowrap',
                  }}>
                    {stageLabel(item.stage)}
                  </div>
                </div>
                <div style={hintStyle}>{item.summary}</div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function stageLabel(stage: TaskStage): string {
  switch (stage) {
    case 'needs_details':
      return t('stageNeedsDetails');
    case 'review':
      return t('stageReview');
    case 'working':
      return t('stageWorking');
    case 'preview_ready':
      return t('stagePreviewReady');
    case 'completed':
      return t('stageCompleted');
    case 'cancelled':
      return t('stageCancelled');
    default:
      return stage;
  }
}

function ReviewMetric({ label, value }: { label: string; value: number }) {
  return (
    <div style={{
      padding: 'var(--space-sm)',
      borderRadius: 'var(--radius-md)',
      border: '1px solid var(--color-border)',
      background: 'var(--color-bg-primary)',
    }}>
      <div style={sectionLabelStyle}>{label}</div>
      <div style={{
        fontSize: 'var(--text-base)',
        fontWeight: 700,
        color: 'var(--color-text-primary)',
      }}>
        {value}
      </div>
    </div>
  );
}

const ghostButtonStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-sm)',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-xs)',
};

const primaryButtonStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-md)',
  borderRadius: 'var(--radius-md)',
  border: 'none',
  background: 'var(--color-accent)',
  color: 'var(--color-text-inverse)',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
  fontWeight: 600,
  opacity: 1,
};

const secondaryButtonStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-md)',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-sm)',
  opacity: 1,
};

const sectionLabelStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-muted)',
  marginBottom: 'var(--space-xs)',
  textTransform: 'uppercase',
  letterSpacing: '0.06em',
};

const hintStyle: CSSProperties = {
  fontSize: 'var(--text-xs)',
  color: 'var(--color-text-muted)',
  lineHeight: 1.6,
  whiteSpace: 'pre-wrap',
};

const listStyle: CSSProperties = {
  margin: 0,
  paddingLeft: 'var(--space-lg)',
  display: 'grid',
  gap: 'var(--space-xs)',
  fontSize: 'var(--text-sm)',
  color: 'var(--color-text-secondary)',
};

const reviewGridStyle: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
  gap: 'var(--space-sm)',
};
