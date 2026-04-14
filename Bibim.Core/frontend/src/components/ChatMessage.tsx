import { useState, type CSSProperties } from 'react';
import { formatTime, t } from '../i18n';
import type { ChatMsg } from '../types';
import CodeBlock from './CodeBlock';

interface Props {
  msg: ChatMsg;
  onUndo?: (actionId: string, taskId?: string) => void;
  onFeedback?: (actionId: string, vote: 'up' | 'down', taskId?: string) => void;
  onFeedbackDetail?: (actionId: string, taskId: string | undefined, detail: string) => void;
  onRegenerate?: (actionId: string, taskId: string | undefined, detail: string) => void;
  onRerun?: (code: string, createdAt: string) => void;
  onWarningResponse?: (choice: 'yes' | 'no' | 'add', taskId: string | undefined, text?: string) => void;
}

export default function ChatMessage({ msg, onUndo, onFeedback, onFeedbackDetail, onRegenerate, onRerun, onWarningResponse }: Props) {
  if (msg.type === 'feedback_request') {
    return (
      <FeedbackBubble
        msg={msg}
        onFeedback={onFeedback}
        onFeedbackDetail={onFeedbackDetail}
        onRegenerate={onRegenerate}
      />
    );
  }

  if (msg.type === 'revit_warning') {
    return <RevitWarningBubble msg={msg} onResponse={onWarningResponse} />;
  }
  const isStreaming = msg.id === '__streaming__';
  const isQuestion = msg.type === 'question';
  const isError = msg.type === 'error';
  const showActions = !msg.isUser && !isStreaming && Boolean(msg.actionId) &&
    (msg.canUndo || msg.feedbackEnabled);
  const showRerun = !msg.isUser && !isStreaming && Boolean(msg.csharpCode) && Boolean(onRerun);
  const background = msg.isUser
    ? 'var(--color-accent-muted)'
    : isQuestion
      ? 'rgba(245, 158, 11, 0.10)'
      : isError
        ? 'rgba(239, 68, 68, 0.10)'
        : msg.type === 'system'
          ? 'var(--color-bg-tertiary)'
          : 'var(--color-bg-secondary)';
  const border = msg.isUser
    ? '1px solid var(--color-accent)'
    : isQuestion
      ? '1px solid var(--color-warning)'
      : isError
        ? '1px solid var(--color-error)'
        : '1px solid var(--color-border)';

  return (
    <div style={{
      display: 'flex',
      justifyContent: msg.isUser ? 'flex-end' : 'flex-start',
      padding: 'var(--space-xs) 0',
    }}>
      <div style={{
        maxWidth: '85%',
        padding: 'var(--space-sm) var(--space-md)',
        borderRadius: 'var(--radius-lg)',
        background,
        border,
        fontSize: 'var(--text-sm)',
        lineHeight: 'var(--leading-relaxed)',
      }}>
        <div style={{
          color: msg.type === 'system'
            ? 'var(--color-text-muted)'
            : 'var(--color-text-primary)',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
        }}>
          {renderText(msg.text)}
          {isStreaming && <span style={{ opacity: 0.5 }}>...</span>}
        </div>

        {msg.csharpCode && <CodeBlock code={msg.csharpCode} />}

        {(showActions || showRerun) && (
          <div style={{
            marginTop: 'var(--space-sm)',
            display: 'flex',
            gap: 'var(--space-xs)',
            flexWrap: 'wrap',
          }}>
            {showRerun && (
              <button
                onClick={() => onRerun?.(msg.csharpCode!, msg.createdAt)}
                style={secondaryButtonStyle}
                title="Run this saved code in a new session"
              >
                Rerun
              </button>
            )}

            {showActions && msg.actionId && msg.canUndo && (
              <button
                onClick={() => onUndo?.(msg.actionId!, msg.taskId)}
                style={secondaryButtonStyle}
                title={t('undoLastApply')}
              >
                {t('undoLastApply')}
              </button>
            )}

            {showActions && msg.actionId && msg.feedbackEnabled && (
              <>
                <button
                  onClick={() => onFeedback?.(msg.actionId!, 'up', msg.taskId)}
                  style={{
                    ...feedbackButtonStyle,
                    borderColor: msg.feedbackState === 'up'
                      ? 'var(--color-success)'
                      : 'var(--color-border)',
                    background: msg.feedbackState === 'up'
                      ? 'rgba(34, 197, 94, 0.12)'
                      : 'transparent',
                  }}
                  title={t('helpful')}
                >
                  Up
                </button>
                <button
                  onClick={() => onFeedback?.(msg.actionId!, 'down', msg.taskId)}
                  style={{
                    ...feedbackButtonStyle,
                    borderColor: msg.feedbackState === 'down'
                      ? 'var(--color-error)'
                      : 'var(--color-border)',
                    background: msg.feedbackState === 'down'
                      ? 'rgba(239, 68, 68, 0.12)'
                      : 'transparent',
                  }}
                  title={t('notHelpful')}
                >
                  Down
                </button>
              </>
            )}
          </div>
        )}

        {!msg.isUser && !isStreaming && msg.inputTokens != null && msg.inputTokens > 0 && (
          <div style={{
            marginTop: 'var(--space-sm)',
            paddingTop: 'var(--space-xs)',
            borderTop: '1px solid var(--color-border)',
            fontSize: 'var(--text-xs)',
            color: 'var(--color-text-muted)',
            display: 'flex',
            justifyContent: 'space-between',
            gap: 'var(--space-sm)',
            flexWrap: 'wrap',
          }}>
            <span>
              {((msg.elapsedMs ?? 0) / 1000).toFixed(1)}s
              {' '}
              ({msg.inputTokens.toLocaleString()} in / {(msg.outputTokens ?? 0).toLocaleString()} out)
            </span>
          </div>
        )}

        {!isStreaming && (
          <div style={{
            fontSize: 'var(--text-xs)',
            color: 'var(--color-text-muted)',
            marginTop: 'var(--space-xs)',
            textAlign: msg.isUser ? 'right' : 'left',
          }}>
            {formatTime(msg.createdAt)}
          </div>
        )}
      </div>
    </div>
  );
}

function renderText(text: string) {
  if (!text) return null;

  const parts = text.split(/(```[\s\S]*?```)/g);
  return parts.map((part, index) => {
    if (part.startsWith('```')) {
      const lines = part.split('\n');
      const language = lines[0].replace('```', '').trim() || 'csharp';
      const code = lines.slice(1, -1).join('\n');
      return <CodeBlock key={index} code={code} language={language} />;
    }
    return <span key={index}>{part}</span>;
  });
}

const secondaryButtonStyle: CSSProperties = {
  padding: 'var(--space-xs) var(--space-sm)',
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-xs)',
  fontWeight: 600,
};

const feedbackButtonStyle: CSSProperties = {
  width: 48,
  height: 32,
  borderRadius: 'var(--radius-md)',
  border: '1px solid var(--color-border)',
  background: 'transparent',
  color: 'var(--color-text-secondary)',
  cursor: 'pointer',
  fontSize: 'var(--text-xs)',
  lineHeight: 1,
};

// --- Separate feedback-request bubble ---

interface FeedbackBubbleProps {
  msg: ChatMsg;
  onFeedback?: (actionId: string, vote: 'up' | 'down', taskId?: string) => void;
  onFeedbackDetail?: (actionId: string, taskId: string | undefined, detail: string) => void;
  onRegenerate?: (actionId: string, taskId: string | undefined, detail: string) => void;
}

function FeedbackBubble({ msg, onFeedback, onFeedbackDetail, onRegenerate }: FeedbackBubbleProps) {
  const [otherText, setOtherText] = useState('');
  const [showOther, setShowOther] = useState(false);
  const step = msg.feedbackStep ?? 'awaiting';

  return (
    <div style={{ display: 'flex', justifyContent: 'flex-start', padding: 'var(--space-xs) 0' }}>
      <div style={{
        maxWidth: '85%',
        padding: 'var(--space-sm) var(--space-md)',
        borderRadius: 'var(--radius-lg)',
        background: 'var(--color-bg-secondary)',
        border: '1px solid var(--color-border)',
        fontSize: 'var(--text-sm)',
        lineHeight: 'var(--leading-relaxed)',
      }}>

        {step === 'awaiting' && (
          <>
            <div style={{ color: 'var(--color-text-primary)', marginBottom: 'var(--space-sm)' }}>
              {t('executionFeedbackPrompt')}
            </div>
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <button
                onClick={() => onFeedback?.(msg.actionId!, 'up', msg.taskId)}
                style={{ ...secondaryButtonStyle, color: 'var(--color-success)', borderColor: 'var(--color-success)' }}
              >
                👍 Up
              </button>
              <button
                onClick={() => onFeedback?.(msg.actionId!, 'down', msg.taskId)}
                style={{ ...secondaryButtonStyle, color: 'var(--color-error)', borderColor: 'var(--color-error)' }}
              >
                👎 Down
              </button>
            </div>
          </>
        )}

        {step === 'up_confirmed' && (
          <div style={{ color: 'var(--color-success)' }}>
            {t('feedbackUpConfirmed')}
          </div>
        )}

        {step === 'down_detail' && !showOther && (
          <>
            <div style={{ color: 'var(--color-text-primary)', marginBottom: 'var(--space-sm)' }}>
              {t('feedbackDownDetailPrompt')}
            </div>
            <div style={{ display: 'flex', gap: 'var(--space-xs)', flexWrap: 'wrap' }}>
              {(['feedbackDetailNotWorking', 'feedbackDetailNotWanted'] as const).map((key) => (
                <button
                  key={key}
                  onClick={() => onFeedbackDetail?.(msg.actionId!, msg.taskId, t(key))}
                  style={secondaryButtonStyle}
                >
                  {t(key)}
                </button>
              ))}
              <button onClick={() => setShowOther(true)} style={secondaryButtonStyle}>
                {t('feedbackDetailOther')}
              </button>
            </div>
          </>
        )}

        {step === 'down_detail' && showOther && (
          <>
            <div style={{ color: 'var(--color-text-primary)', marginBottom: 'var(--space-sm)' }}>
              {t('feedbackDetailOther')}
            </div>
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <input
                autoFocus
                value={otherText}
                onChange={(e) => setOtherText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && otherText.trim()) {
                    onFeedbackDetail?.(msg.actionId!, msg.taskId, otherText.trim());
                  }
                }}
                placeholder={t('feedbackDetailOtherPlaceholder')}
                style={{
                  flex: 1,
                  padding: 'var(--space-xs) var(--space-sm)',
                  borderRadius: 'var(--radius-md)',
                  border: '1px solid var(--color-border)',
                  background: 'var(--color-bg-tertiary)',
                  color: 'var(--color-text-primary)',
                  fontSize: 'var(--text-sm)',
                  outline: 'none',
                }}
              />
              <button
                onClick={() => { if (otherText.trim()) onFeedbackDetail?.(msg.actionId!, msg.taskId, otherText.trim()); }}
                style={secondaryButtonStyle}
                disabled={!otherText.trim()}
              >
                {t('feedbackDetailSubmit')}
              </button>
            </div>
          </>
        )}

        {step === 'regen_offer' && (
          <>
            <div style={{ color: 'var(--color-text-primary)', marginBottom: 'var(--space-sm)' }}>
              {t('feedbackRegenOffer')}
            </div>
            <button
              onClick={() => onRegenerate?.(msg.actionId!, msg.taskId, msg.feedbackDetail ?? '')}
              style={{ ...secondaryButtonStyle, borderColor: 'var(--color-accent)', color: 'var(--color-accent)' }}
            >
              {t('feedbackRegenButton')}
            </button>
          </>
        )}

        <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', marginTop: 'var(--space-xs)' }}>
          {formatTime(msg.createdAt)}
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// RevitWarningBubble — shown after execution when Revit warnings fired
// ---------------------------------------------------------------------------

interface RevitWarningBubbleProps {
  msg: ChatMsg;
  onResponse?: (choice: 'yes' | 'no' | 'add', taskId: string | undefined, text?: string) => void;
}

function RevitWarningBubble({ msg, onResponse }: RevitWarningBubbleProps) {
  const [mode, setMode] = useState<'idle' | 'add' | 'done'>('idle');
  const [addText, setAddText] = useState('');

  if (mode === 'done') {
    return (
      <div style={{ display: 'flex', justifyContent: 'flex-start', marginBottom: 'var(--space-sm)' }}>
        <div style={{
          background: 'var(--color-bg-secondary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-md)',
          padding: 'var(--space-sm) var(--space-md)',
          maxWidth: '80%',
          fontSize: 'var(--text-sm)',
          color: 'var(--color-text-muted)',
          whiteSpace: 'pre-wrap',
        }}>
          {msg.text}
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', justifyContent: 'flex-start', marginBottom: 'var(--space-sm)' }}>
      <div style={{
        background: 'var(--color-bg-secondary)',
        border: '1px solid var(--color-warning, #f59e0b)',
        borderRadius: 'var(--radius-md)',
        padding: 'var(--space-md)',
        maxWidth: '85%',
        fontSize: 'var(--text-sm)',
      }}>
        <div style={{ whiteSpace: 'pre-wrap', marginBottom: 'var(--space-sm)' }}>{msg.text}</div>

        {mode === 'add' ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-xs)' }}>
            <textarea
              autoFocus
              value={addText}
              onChange={(e) => setAddText(e.target.value)}
              placeholder={t('revitWarningAddPlaceholder')}
              rows={3}
              style={{
                width: '100%',
                resize: 'vertical',
                background: 'var(--color-bg-primary)',
                border: '1px solid var(--color-border)',
                borderRadius: 'var(--radius-sm)',
                padding: 'var(--space-xs)',
                color: 'var(--color-text-primary)',
                fontSize: 'var(--text-sm)',
                boxSizing: 'border-box',
              }}
            />
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <button
                onClick={() => {
                  if (!addText.trim()) return;
                  onResponse?.('add', msg.taskId, addText.trim());
                  setMode('done');
                }}
                disabled={!addText.trim()}
                style={{ flex: 1 }}
                className="btn-primary"
              >
                {t('revitWarningAddConfirm')}
              </button>
              <button onClick={() => setMode('idle')} className="btn-secondary">
                {t('questionCardSkipCancel')}
              </button>
            </div>
          </div>
        ) : (
          <div style={{ display: 'flex', gap: 'var(--space-xs)', flexWrap: 'wrap' }}>
            <button
              className="btn-primary"
              onClick={() => { onResponse?.('yes', msg.taskId); setMode('done'); }}
            >
              {t('revitWarningYes')}
            </button>
            <button
              className="btn-secondary"
              onClick={() => setMode('add')}
            >
              {t('revitWarningAdd')}
            </button>
            <button
              className="btn-secondary"
              onClick={() => { onResponse?.('no', msg.taskId); setMode('done'); }}
            >
              {t('revitWarningNo')}
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
