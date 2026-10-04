import { useRef, useEffect, useState } from 'react';
import { t } from '../i18n';
import type { ChatMsg, ProgressStep, TaskItem, TaskSummary } from '../types';
import type { PendingAttachment } from '../utils/attachment';
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
  onSend: (text: string, attachment?: PendingAttachment) => void;
  onCancel: () => void;
  onForceUnstick?: () => void;
  isStreaming?: boolean;
  onTaskConfirm: () => void;
  onTaskCancel: () => void;
  onApply: (mode: 'dryrun' | 'commit', taskId?: string) => void;
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
  onSend, onCancel, onForceUnstick, isStreaming, onTaskConfirm, onTaskCancel, onApply, onUndo, onFeedback,
  onFeedbackDetail, onRegenerate, onRerun, onQuestionAnswers, onWarningResponse, isMandatoryUpdate,
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  // Auto-scroll only when the user is already near the bottom — yanking them
  // down on every streaming delta made reading earlier messages impossible.
  const nearBottomRef = useRef(true);
  // Show the question card whenever the task needs details and has questions.
  // Empty options array is valid — it means a free-text-only question (U-1 fix).
  const hasQuestions = currentTask?.stage === 'needs_details'
    && currentTask.questions
    && currentTask.questions.length > 0;
  // The card used to REPLACE the input, leaving no way to just answer in
  // prose when the planner asked something odd. Now they coexist; dismissing
  // the card re-arms when a different question set arrives.
  const [questionsDismissed, setQuestionsDismissed] = useState(false);
  // Key by question IDs — 'update' re-plans reuse the taskId and can swap in a
  // DIFFERENT same-length question set, which a length-based key missed.
  const questionsKey = `${currentTask?.taskId ?? ''}:${(currentTask?.questions ?? []).map(q => (typeof q === 'string' ? q : q.id)).join(',')}`;
  useEffect(() => { setQuestionsDismissed(false); }, [questionsKey]);

  useEffect(() => {
    if (scrollRef.current && nearBottomRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages]);

  const handleScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    nearBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
  };

  return (
    <div style={{
      display: 'flex', flexDirection: 'column',
      height: '100%', overflow: 'hidden',
    }}>
      {/* Non-blocking loading banner — sits above the scroll area so chat remains scrollable */}
      <LoadingModal open={isBusy} steps={steps} streamingActive={isStreaming} onForceUnstick={onForceUnstick} onCancel={onCancel} />

      <div
        ref={scrollRef}
        onScroll={handleScroll}
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
              textAlign: 'left', maxWidth: 340,
              whiteSpace: 'pre-line',
              lineHeight: 1.6,
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
          onApply={() => onApply('commit', currentTask?.taskId)}
          onRerun={() => onApply('dryrun', currentTask?.taskId)}
        />
        {hasQuestions && !questionsDismissed && onQuestionAnswers && (
          <QuestionCard
            questions={currentTask!.questions}
            onComplete={onQuestionAnswers}
            onDismiss={() => setQuestionsDismissed(true)}
          />
        )}
        {hasQuestions && questionsDismissed && (
          <button
            onClick={() => setQuestionsDismissed(false)}
            style={{
              alignSelf: 'flex-start',
              background: 'none',
              border: '1px dashed var(--color-border)',
              borderRadius: 'var(--radius-full)',
              padding: '2px 10px',
              cursor: 'pointer',
              color: 'var(--color-text-muted)',
              fontSize: 'var(--text-xs)',
            }}
          >
            📋 {t('questionCardReopen')}
          </button>
        )}
        <ChatInput
          onSend={(text, attachment) => {
            // The user's own message must always come into view, even if they
            // were scrolled up reading history when they sent it.
            nearBottomRef.current = true;
            onSend(text, attachment);
          }}
          onCancel={onCancel}
          disabled={isBusy || !!isMandatoryUpdate}
          isBusy={isBusy}
        />
      </div>

    </div>
  );
}
