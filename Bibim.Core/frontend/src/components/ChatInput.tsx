import { useState, useRef, useCallback, useEffect } from 'react';
import { isImeComposing } from '../utils/ime';
import { getContextSuggestions, t } from '../i18n';
import type { ContextSuggestion } from '../types';
import {
  ATTACH_ACCEPT, readAttachment, toMessageAttachment,
  type AttachError, type PendingAttachment,
} from '../utils/attachment';
import AttachmentChip, { PaperclipIcon } from './AttachmentChip';

interface Props {
  onSend: (text: string, attachment?: PendingAttachment) => void;
  onCancel: () => void;
  disabled: boolean;
  isBusy: boolean;
}

const ATTACH_ERROR_KEY: Record<AttachError, 'attachErrorUnsupported' | 'attachErrorTooLarge' | 'attachErrorUnreadable'> = {
  unsupported: 'attachErrorUnsupported',
  tooLarge: 'attachErrorTooLarge',
  unreadable: 'attachErrorUnreadable',
};

export default function ChatInput({ onSend, onCancel, disabled, isBusy }: Props) {
  const [text, setText] = useState('');
  const [suggestions, setSuggestions] = useState<ContextSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [attachment, setAttachment] = useState<PendingAttachment | null>(null);
  const [attachNotice, setAttachNotice] = useState<string | null>(null);
  const [dragActive, setDragActive] = useState(false);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  // Auto-resize textarea based on content
  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
  }, [text]);

  const attachFiles = useCallback(async (files: FileList | File[] | null) => {
    const list = files ? Array.from(files) : [];
    if (list.length === 0) return;
    const result = await readAttachment(list[0]);   // single file (spec P0)
    if (typeof result === 'string') {
      setAttachNotice(t(ATTACH_ERROR_KEY[result]));
      return;
    }
    setAttachment(result);
    setAttachNotice(list.length > 1 ? t('attachOnlyFirst') : null);
    inputRef.current?.focus();
  }, []);

  // Drag & drop anywhere in the panel. The window-level preventDefault also stops
  // WebView2 from navigating away to a dropped file.
  useEffect(() => {
    let depth = 0;
    const hasFiles = (e: DragEvent) => Array.from(e.dataTransfer?.types ?? []).includes('Files');
    const onDragEnter = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      depth++;
      setDragActive(true);
    };
    const onDragOver = (e: DragEvent) => {
      e.preventDefault();
      if (e.dataTransfer) e.dataTransfer.dropEffect = hasFiles(e) ? 'copy' : 'none';
    };
    const onDragLeave = (e: DragEvent) => {
      if (!hasFiles(e)) return;
      depth = Math.max(0, depth - 1);
      if (depth === 0) setDragActive(false);
    };
    const onDrop = (e: DragEvent) => {
      e.preventDefault();
      depth = 0;
      setDragActive(false);
      if (e.dataTransfer?.files?.length) void attachFiles(e.dataTransfer.files);
    };
    window.addEventListener('dragenter', onDragEnter);
    window.addEventListener('dragover', onDragOver);
    window.addEventListener('dragleave', onDragLeave);
    window.addEventListener('drop', onDrop);
    return () => {
      window.removeEventListener('dragenter', onDragEnter);
      window.removeEventListener('dragover', onDragOver);
      window.removeEventListener('dragleave', onDragLeave);
      window.removeEventListener('drop', onDrop);
    };
  }, [attachFiles]);

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

  const canSend = !disabled && (Boolean(text.trim()) || attachment != null);

  const submit = () => {
    if (!canSend) return;
    // Empty instruction + document → the spec's default instruction (backend applies
    // the same default, this keeps the bubble and the request identical).
    const instruction = text.trim() || (attachment ? t('attachDefaultInstruction') : '');
    onSend(instruction, attachment ?? undefined);
    setText('');
    setAttachment(null);
    setAttachNotice(null);
    setShowSuggestions(false);
    if (inputRef.current) {
      inputRef.current.style.height = 'auto';
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey && !isImeComposing(e)) {
      e.preventDefault();
      submit();
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

      {(attachment || attachNotice) && (
        <div style={{
          display: 'flex', flexDirection: 'column', gap: 4,
          marginBottom: 'var(--space-xs)',
        }}>
          {attachment && (
            <AttachmentChip
              attachment={toMessageAttachment(attachment)}
              onRemove={() => { setAttachment(null); setAttachNotice(null); }}
            />
          )}
          {attachNotice && (
            <div style={{ color: 'var(--color-error)', fontSize: 'var(--text-xs)' }}>{attachNotice}</div>
          )}
        </div>
      )}

      <div style={{
        display: 'flex', alignItems: 'flex-end', gap: 'var(--space-sm)',
        padding: 'var(--space-sm)',
        background: 'var(--color-bg-input)',
        border: dragActive ? '1px dashed var(--color-accent)' : '1px solid var(--color-border)',
        borderRadius: 'var(--radius-lg)',
      }}>
        <input
          ref={fileRef}
          type="file"
          accept={ATTACH_ACCEPT}
          style={{ display: 'none' }}
          onChange={(e) => {
            void attachFiles(e.target.files);
            e.target.value = '';   // re-selecting the same file must fire again
          }}
        />
        <button
          type="button"
          onClick={() => fileRef.current?.click()}
          disabled={disabled}
          title={t('attachTooltip')}
          aria-label={t('attachTooltip')}
          style={{
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
            padding: '4px',
            background: 'none', border: 'none',
            color: attachment ? 'var(--color-accent)' : 'var(--color-text-muted)',
            cursor: disabled ? 'default' : 'pointer',
            opacity: disabled ? 0.5 : 1,
          }}
        >
          <PaperclipIcon />
        </button>
        <textarea
          ref={inputRef}
          value={text}
          onChange={(e) => handleChange(e.target.value)}
          onKeyDown={handleKeyDown}
          placeholder={dragActive ? t('attachDropHere') : attachment ? t('attachInstructionPlaceholder') : t('inputPlaceholder')}
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
            onClick={submit}
            disabled={!canSend}
            style={{
              padding: 'var(--space-xs) var(--space-md)',
              background: canSend ? 'var(--color-accent)' : 'var(--color-bg-tertiary)',
              border: 'none', borderRadius: 'var(--radius-md)',
              color: canSend ? '#fff' : 'var(--color-text-muted)',
              fontSize: 'var(--text-sm)', cursor: canSend ? 'pointer' : 'default',
            }}
          >
            {t('send')}
          </button>
        )}
      </div>
    </div>
  );
}
