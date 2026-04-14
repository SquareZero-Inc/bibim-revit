import { useState, useCallback, useEffect, useRef } from 'react';
import { t } from '../i18n';
import type { QuestionItem } from '../types';

interface Props {
  questions: QuestionItem[];
  onComplete: (answers: { id: string; answer: string; skipped: boolean }[]) => void;
}

export default function QuestionCard({ questions, onComplete }: Props) {
  const [currentIndex, setCurrentIndex] = useState(0);
  const [answers, setAnswers] = useState<Record<string, { answer: string; skipped: boolean }>>({});
  const [customText, setCustomText] = useState('');
  const [showCustomInput, setShowCustomInput] = useState(false);
  const [multiSelected, setMultiSelected] = useState<Set<string>>(new Set());
  const [showSkipWarning, setShowSkipWarning] = useState(false);
  const submittedRef = useRef(false);

  // Case 2: Reset internal state when questions prop changes externally
  const questionsKey = questions.map(q => q.id).join(',');
  useEffect(() => {
    setCurrentIndex(0);
    setAnswers({});
    setCustomText('');
    setShowCustomInput(false);
    setMultiSelected(new Set());
    setShowSkipWarning(false);
    submittedRef.current = false;
  }, [questionsKey]);

  const question = questions[currentIndex];
  const isMulti = question?.selectionType === 'multi';
  const total = questions.length;

  const commitAndAdvance = useCallback((answer: string, skipped: boolean) => {
    const next = {
      ...answers,
      [question.id]: { answer, skipped },
    };
    setAnswers(next);
    setCustomText('');
    setShowCustomInput(false);
    setMultiSelected(new Set());

    if (currentIndex < total - 1) {
      setCurrentIndex(currentIndex + 1);
    } else {
      // Case 6: Prevent double submission
      if (submittedRef.current) return;
      submittedRef.current = true;

      const result = questions.map(q => ({
        id: q.id,
        answer: next[q.id]?.answer ?? '',
        skipped: next[q.id]?.skipped ?? false,
      }));
      onComplete(result);
    }
  }, [answers, question, currentIndex, total, questions, onComplete]);

  const handleOptionClick = useCallback((option: string) => {
    if (isMulti) {
      setMultiSelected(prev => {
        const next = new Set(prev);
        if (next.has(option)) next.delete(option);
        else next.add(option);
        return next;
      });
    } else {
      commitAndAdvance(option, false);
    }
  }, [isMulti, commitAndAdvance]);

  const handleMultiConfirm = useCallback(() => {
    if (multiSelected.size > 0) {
      commitAndAdvance(Array.from(multiSelected).join(', '), false);
    }
  }, [multiSelected, commitAndAdvance]);

  const handleCustomSubmit = useCallback(() => {
    if (customText.trim()) {
      commitAndAdvance(customText.trim(), false);
    }
  }, [customText, commitAndAdvance]);

  const handleSkipConfirm = useCallback(() => {
    setShowSkipWarning(false);
    commitAndAdvance('', true);
  }, [commitAndAdvance]);

  const goToPrev = useCallback(() => {
    if (currentIndex > 0) {
      setCurrentIndex(currentIndex - 1);
      setCustomText('');
      setShowCustomInput(false);
      setMultiSelected(new Set());
    }
  }, [currentIndex]);

  if (!question) return null;

  return (
    <div style={{
      background: 'var(--color-bg-secondary)',
      border: '1px solid var(--color-border)',
      borderRadius: 'var(--radius-lg)',
      padding: 'var(--space-lg)',
      position: 'relative',
    }}>
      {/* Header: question text + pagination */}
      <div style={{
        display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start',
        marginBottom: 'var(--space-md)',
      }}>
        <div style={{
          fontSize: 'var(--text-sm)', fontWeight: 600,
          color: 'var(--color-text-primary)',
          flex: 1, paddingRight: 'var(--space-md)',
        }}>
          {question.text}
        </div>
        <div style={{
          fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)',
          whiteSpace: 'nowrap',
          display: 'flex', alignItems: 'center', gap: 'var(--space-xs)',
        }}>
          <button onClick={goToPrev} disabled={currentIndex === 0} style={navBtnStyle}>
            {'<'}
          </button>
          <span>{currentIndex + 1} {t('questionCardOf')} {total}</span>
          <span style={{ width: 18 }} />
        </div>
      </div>

      {isMulti && (
        <div style={{
          fontSize: 'var(--text-xs)', color: 'var(--color-accent)',
          marginBottom: 'var(--space-sm)',
        }}>
          Select all that apply
        </div>
      )}

      {/* Options */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-xs)' }}>
        {question.options.map((option, idx) => {
          const isSelected = isMulti ? multiSelected.has(option) : false;
          return (
            <button
              key={idx}
              onClick={() => handleOptionClick(option)}
              style={{
                display: 'flex', alignItems: 'center', gap: 'var(--space-sm)',
                width: '100%', padding: 'var(--space-sm) var(--space-md)',
                background: isSelected ? 'var(--color-accent-muted)' : 'var(--color-bg-tertiary)',
                border: isSelected ? '1px solid var(--color-accent)' : '1px solid var(--color-border)',
                borderRadius: 'var(--radius-md)',
                cursor: 'pointer',
                color: 'var(--color-text-primary)',
                fontSize: 'var(--text-sm)',
                textAlign: 'left',
                transition: 'var(--transition-fast)',
              }}
            >
              <span style={{
                width: 22, height: 22, borderRadius: isMulti ? 'var(--radius-sm)' : 'var(--radius-full)',
                background: isSelected ? 'var(--color-accent)' : 'var(--color-bg-primary)',
                border: '1px solid var(--color-border)',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontSize: 'var(--text-xs)', fontWeight: 600,
                color: isSelected ? '#fff' : 'var(--color-text-muted)',
                flexShrink: 0,
              }}>
                {isSelected ? '✓' : idx + 1}
              </span>
              {option}
            </button>
          );
        })}
      </div>

      {/* Multi-select confirm button */}
      {isMulti && multiSelected.size > 0 && (
        <button onClick={handleMultiConfirm} style={{
          marginTop: 'var(--space-sm)',
          padding: 'var(--space-xs) var(--space-md)',
          background: 'var(--color-accent)',
          border: 'none', borderRadius: 'var(--radius-md)',
          color: '#fff', fontSize: 'var(--text-sm)',
          cursor: 'pointer', width: '100%',
        }}>
          {t('confirm')} ({multiSelected.size})
        </button>
      )}

      {/* Something else + Skip row */}
      <div style={{
        display: 'flex', alignItems: 'center', gap: 'var(--space-sm)',
        marginTop: 'var(--space-sm)',
      }}>
        {showCustomInput ? (
          <div style={{ flex: 1, display: 'flex', gap: 'var(--space-xs)' }}>
            <input
              type="text"
              value={customText}
              onChange={e => setCustomText(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') handleCustomSubmit(); }}
              placeholder={t('questionCardSomethingElse')}
              autoFocus
              style={{
                flex: 1, padding: 'var(--space-xs) var(--space-sm)',
                background: 'var(--color-bg-input)',
                border: '1px solid var(--color-border)',
                borderRadius: 'var(--radius-md)',
                color: 'var(--color-text-primary)',
                fontSize: 'var(--text-sm)',
                outline: 'none',
              }}
            />
            <button onClick={handleCustomSubmit} disabled={!customText.trim()} style={{
              padding: 'var(--space-xs) var(--space-sm)',
              background: customText.trim() ? 'var(--color-accent)' : 'var(--color-bg-tertiary)',
              border: 'none', borderRadius: 'var(--radius-md)',
              color: customText.trim() ? '#fff' : 'var(--color-text-muted)',
              fontSize: 'var(--text-sm)', cursor: customText.trim() ? 'pointer' : 'default',
            }}>
              →
            </button>
          </div>
        ) : (
          <button
            onClick={() => setShowCustomInput(true)}
            style={{
              flex: 1, display: 'flex', alignItems: 'center', gap: 'var(--space-xs)',
              padding: 'var(--space-sm) var(--space-md)',
              background: 'none',
              border: '1px solid var(--color-border)',
              borderRadius: 'var(--radius-md)',
              cursor: 'pointer',
              color: 'var(--color-text-muted)',
              fontSize: 'var(--text-sm)',
            }}
          >
            ✏️ {t('questionCardSomethingElse')}
          </button>
        )}
        <button
          onClick={() => setShowSkipWarning(true)}
          style={{
            padding: 'var(--space-sm) var(--space-md)',
            background: 'none',
            border: '1px solid var(--color-border)',
            borderRadius: 'var(--radius-md)',
            cursor: 'pointer',
            color: 'var(--color-text-muted)',
            fontSize: 'var(--text-sm)',
          }}
        >
          {t('questionCardSkip')}
        </button>
      </div>

      {/* Skip warning overlay */}
      {showSkipWarning && (
        <div style={{
          position: 'absolute', inset: 0,
          background: 'rgba(0,0,0,0.7)',
          borderRadius: 'var(--radius-lg)',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          padding: 'var(--space-lg)',
          zIndex: 10,
        }}>
          <div style={{
            background: 'var(--color-bg-secondary)',
            border: '1px solid var(--color-border)',
            borderRadius: 'var(--radius-md)',
            padding: 'var(--space-lg)',
            maxWidth: 320,
          }}>
            <div style={{
              fontSize: 'var(--text-sm)', fontWeight: 600,
              color: 'var(--color-warning)',
              marginBottom: 'var(--space-sm)',
            }}>
              {t('questionCardSkipTitle')}
            </div>
            <div style={{
              fontSize: 'var(--text-xs)',
              color: 'var(--color-text-secondary)',
              marginBottom: 'var(--space-md)',
              lineHeight: 'var(--leading-relaxed)',
            }}>
              {t('questionCardSkipMessage')}
            </div>
            <div style={{ display: 'flex', gap: 'var(--space-sm)', justifyContent: 'flex-end' }}>
              <button onClick={() => setShowSkipWarning(false)} style={{
                padding: 'var(--space-xs) var(--space-md)',
                background: 'var(--color-bg-tertiary)',
                border: '1px solid var(--color-border)',
                borderRadius: 'var(--radius-md)',
                color: 'var(--color-text-primary)',
                fontSize: 'var(--text-sm)', cursor: 'pointer',
              }}>
                {t('questionCardSkipCancel')}
              </button>
              <button onClick={handleSkipConfirm} style={{
                padding: 'var(--space-xs) var(--space-md)',
                background: 'var(--color-warning)',
                border: 'none', borderRadius: 'var(--radius-md)',
                color: '#fff',
                fontSize: 'var(--text-sm)', cursor: 'pointer',
              }}>
                {t('questionCardSkipConfirm')}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

const navBtnStyle: React.CSSProperties = {
  background: 'none',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--color-text-muted)',
  cursor: 'pointer',
  width: 22, height: 22,
  display: 'flex', alignItems: 'center', justifyContent: 'center',
  fontSize: 'var(--text-xs)',
  padding: 0,
};
