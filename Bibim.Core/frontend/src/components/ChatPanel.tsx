import { useRef, useEffect } from 'react';
import { t } from '../i18n';
import type { ChatMsg, ProgressStep, TaskItem, TaskSummary } from '../types';
import ChatMessage from './ChatMessage';
import ChatInput from './ChatInput';
import CurrentTaskPanel from './CurrentTaskPanel';
import LoadingModal from './LoadingModal';
import QuestionCard from './QuestionCard';

interface Props {
  messages: ChatMsg[];
  isBusy: boolean;
  steps: ProgressStep[];
  currentTask: TaskItem | null;
  taskList: TaskSummary[];
  appVersion: string;
  onSend: (text: string) => void;
  onCancel: () => void;
  onTaskConfirm: () => void;
  onTaskCancel: () => void;
  onApply: (mode: 'dryrun' | 'commit') => void;
  onUndo: (actionId: string, taskId?: string) => void;
  onFeedback: (actionId: string, vote: 'up' | 'down', taskId?: string) => void;
  onFeedbackDetail?: (actionId: string, taskId: string | undefined, detail: string) => void;
  onRegenerate?: (actionId: string, taskId: string | undefined, detail: string) => void;
  onRerun?: (code: string, createdAt: string) => void;
  onQuestionAnswers?: (answers: { id: string; answer: string; skipped: boolean }[]) => void;
  onWarningResponse?: (choice: 'yes' | 'no' | 'add', taskId: string | undefined, text?: string) => void;
  isMandatoryUpdate?: boolean;
}

export default function ChatPanel({
  messages, isBusy, steps, currentTask, taskList, appVersion,
  onSend, onCancel, onTaskConfirm, onTaskCancel, onApply, onUndo, onFeedback,
  onFeedbackDetail, onRegenerate, onRerun, onQuestionAnswers, onWarningResponse, isMandatoryUpdate,
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const hasQuestions = currentTask?.stage === 'needs_details'
    && currentTask.questions
    && currentTask.questions.length > 0
    && currentTask.questions.some(q => q.options && q.options.length > 0);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages]);

  return (
    <div style={{
      display: 'flex', flexDirection: 'column',
      height: '100%', overflow: 'hidden',
    }}>
      {/* Non-blocking loading banner — sits above the scroll area so chat remains scrollable */}
      <LoadingModal open={isBusy} steps={steps} onCancel={onCancel} />

      <div
        ref={scrollRef}
        style={{
          flex: 1, overflow: 'auto',
          padding: 'var(--space-md) var(--space-lg)',
        }}
      >
        {messages.length === 0 && (
          <div style={{
            display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            height: '100%', gap: 'var(--space-md)',
          }}>
            <img src="./bibim-icon.png" alt="BIBIM" style={{ width: 40, height: 40 }} />
            <div style={{
              fontSize: 'var(--text-lg)', fontWeight: 600,
              color: 'var(--color-accent)',
            }}>
              BIBIM AI {appVersion ? `v${appVersion}` : ''}
            </div>
            <div style={{
              fontSize: 'var(--text-sm)',
              color: 'var(--color-text-muted)',
              textAlign: 'center', maxWidth: 320,
            }}>
              {t('welcomeBody')}
            </div>
          </div>
        )}

        {messages.map((msg) => (
          <ChatMessage
            key={msg.id}
            msg={msg}
            onUndo={onUndo}
            onFeedback={onFeedback}
            onFeedbackDetail={onFeedbackDetail}
            onRegenerate={onRegenerate}
            onRerun={onRerun}
            onWarningResponse={onWarningResponse}
          />
        ))}

      </div>

      <div style={{
        padding: 'var(--space-sm) var(--space-lg) var(--space-lg)',
        borderTop: '1px solid var(--color-border)',
        display: 'flex',
        flexDirection: 'column',
        gap: 'var(--space-sm)',
      }}>
        <CurrentTaskPanel
          task={currentTask}
          tasks={taskList}
          isBusy={isBusy}
          onConfirm={onTaskConfirm}
          onCancel={onTaskCancel}
          onApply={() => onApply('commit')}
        />
        {hasQuestions && onQuestionAnswers ? (
          <QuestionCard
            questions={currentTask!.questions}
            onComplete={onQuestionAnswers}
          />
        ) : (
          <ChatInput
            onSend={onSend}
            onCancel={onCancel}
            disabled={isBusy || !!hasQuestions || !!isMandatoryUpdate}
            isBusy={isBusy}
          />
        )}
      </div>

    </div>
  );
}
