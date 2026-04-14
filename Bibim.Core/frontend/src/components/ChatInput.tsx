import { useState, useRef, useCallback, useEffect } from 'react';
import { getContextSuggestions, t } from '../i18n';
import type { ContextSuggestion } from '../types';

interface Props {
  onSend: (text: string) => void;
  onCancel: () => void;
  disabled: boolean;
  isBusy: boolean;
}

export default function ChatInput({ onSend, onCancel, disabled, isBusy }: Props) {
  const [text, setText] = useState('');
  const [suggestions, setSuggestions] = useState<ContextSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  // Auto-resize textarea based on content
  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [text]);

  const handleChange = useCallback((value: string) => {
    setText(value);

    const contextTags = getContextSuggestions();
    const atIdx = value.lastIndexOf('@');
    if (atIdx >= 0) {
      const query = value.slice(atIdx).toLowerCase();
      const matches = contextTags.filter((tag) =>
        tag.tag.toLowerCase().startsWith(query),
      );
      setSuggestions(matches);
      setShowSuggestions(matches.length > 0 && query.length >= 1);
    } else {
      setShowSuggestions(false);
    }
  }, []);

  const handleSelectSuggestion = useCallback((tag: string) => {
    const atIdx = text.lastIndexOf('@');
    if (atIdx >= 0) {
      const newText = text.slice(0, atIdx) + tag + ' ';
      setText(newText);
    }
    setShowSuggestions(false);
    inputRef.current?.focus();
  }, [text]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      if (text.trim() && !disabled) {
        onSend(text.trim());
        setText('');
        setShowSuggestions(false);
        if (inputRef.current) {
          inputRef.current.style.height = 'auto';
        }
      }
    }
    if (e.key === 'Escape') {
      setShowSuggestions(false);
    }
  };

  return (
    <div style={{ position: 'relative' }}>
      {showSuggestions && (
        <div style={{
          position: 'absolute', bottom: '100%', left: 0, right: 0,
          background: 'var(--color-bg-secondary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-md)',
          marginBottom: 'var(--space-xs)',
          overflow: 'hidden',
          zIndex: 10,
        }}>
          {suggestions.map((suggestion) => (
            <button
              key={suggestion.tag}
              onClick={() => handleSelectSuggestion(suggestion.tag)}
              style={{
                display: 'flex', alignItems: 'center', gap: 'var(--space-sm)',
                width: '100%', padding: 'var(--space-sm) var(--space-md)',
                background: 'none', border: 'none', cursor: 'pointer',
                color: 'var(--color-text-primary)',
                fontSize: 'var(--text-sm)',
                textAlign: 'left',
              }}
              onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--color-bg-hover)')}
              onMouseLeave={(e) => (e.currentTarget.style.background = 'none')}
            >
              <span style={{ color: 'var(--color-accent)', fontFamily: 'var(--font-mono)' }}>
                {suggestion.label}
              </span>
              <span style={{ color: 'var(--color-text-muted)', fontSize: 'var(--text-xs)' }}>
                {suggestion.description}
              </span>
            </button>
          ))}
        </div>
      )}

      <div style={{
        display: 'flex', alignItems: 'flex-end', gap: 'var(--space-sm)',
        padding: 'var(--space-sm)',
        background: 'var(--color-bg-input)',
        border: '1px solid var(--color-border)',
        borderRadius: 'var(--radius-lg)',
      }}>
        <textarea
          ref={inputRef}
          value={text}
          onChange={(e) => handleChange(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={t('inputPlaceholder')}
          disabled={disabled}
          rows={1}
          style={{
            flex: 1, background: 'none', border: 'none', outline: 'none',
            color: 'var(--color-text-primary)',
            fontSize: 'var(--text-sm)',
            resize: 'none',
            maxHeight: 200,
            overflow: 'auto',
            lineHeight: 'var(--leading-normal)',
          }}
        />
        {isBusy ? (
          <button onClick={onCancel} style={{
            padding: 'var(--space-xs) var(--space-md)',
            background: 'var(--color-error)',
            border: 'none', borderRadius: 'var(--radius-md)',
            color: '#fff', fontSize: 'var(--text-sm)', cursor: 'pointer',
          }}>
            {t('stop')}
          </button>
        ) : (
          <button
            onClick={() => {
              if (text.trim()) {
                onSend(text.trim());
                setText('');
                if (inputRef.current) {
                  inputRef.current.style.height = 'auto';
                }
              }
            }}
            disabled={disabled || !text.trim()}
            style={{
              padding: 'var(--space-xs) var(--space-md)',
              background: text.trim() ? 'var(--color-accent)' : 'var(--color-bg-tertiary)',
              border: 'none', borderRadius: 'var(--radius-md)',
              color: text.trim() ? '#fff' : 'var(--color-text-muted)',
              fontSize: 'var(--text-sm)', cursor: text.trim() ? 'pointer' : 'default',
            }}
          >
            {t('send')}
          </button>
        )}
      </div>
    </div>
  );
}
