import { useState, useEffect, useRef } from 'react';
import { t } from '../i18n';

const FEEDBACK_URL_BUG = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';
const FEEDBACK_URL_FEATURE = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';

// Claude models with display labels and estimated cost per typical Revit query
const CLAUDE_MODELS = [
  {
    id: 'claude-haiku-4-5-20251001',
    label: 'Haiku 4.5',
    cost: '~$0.01 / 1회',
    note: '빠름 · 간단한 작업',
  },
  {
    id: 'claude-sonnet-4-6',
    label: 'Sonnet 4.6',
    cost: '~$0.04 / 1회',
    note: '추천 · 균형',
    recommended: true,
  },
  {
    id: 'claude-opus-4-6',
    label: 'Opus 4.6',
    cost: '~$0.20 / 1회',
    note: '고품질 · 복잡한 작업',
  },
  {
    id: 'claude-opus-4-7',
    label: 'Opus 4.7',
    cost: '~$0.20 / 1회',
    note: '최신 · 에이전트 작업',
  },
];

interface Props {
  apiKeyConfigured: boolean;
  apiKeyMasked: string;
  onSaveApiKey: (key: string) => void;
  saveResult: 'idle' | 'saved' | 'error';
  claudeModel: string;
  onSaveModel: (modelId: string) => void;
  geminiConfigured: boolean;
  geminiMasked: string;
  onSaveGeminiApiKey: (key: string) => void;
  geminiSaveResult: 'idle' | 'saved' | 'error';
  onOpenUrl: (url: string) => void;
}

export default function SettingsPanel({
  apiKeyConfigured,
  apiKeyMasked,
  onSaveApiKey,
  saveResult,
  claudeModel,
  onSaveModel,
  geminiConfigured: _geminiConfigured,
  geminiMasked: _geminiMasked,
  onSaveGeminiApiKey: _onSaveGeminiApiKey,
  geminiSaveResult: _geminiSaveResult,
  onOpenUrl,
}: Props) {
  const [open, setOpen] = useState(false);
  const [inputKey, setInputKey] = useState('');
  const [showKey, setShowKey] = useState(false);
  const [geminiInput, setGeminiInput] = useState('');
  const [showGemini, setShowGemini] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  // Reset inputs when panel closes
  useEffect(() => {
    if (!open) {
      setInputKey('');
      setShowKey(false);
      setGeminiInput('');
      setShowGemini(false);
    }
  }, [open]);

  const handleSaveClaude = () => {
    const trimmed = inputKey.trim();
    if (!trimmed) return;
    onSaveApiKey(trimmed);
  };


  return (
    <div ref={panelRef} style={{ position: 'relative' }}>
      <button
        onClick={() => setOpen((prev) => !prev)}
        title={t('settings')}
        style={{
          background: 'none',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-full)',
          cursor: 'pointer',
          color: apiKeyConfigured ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)',
          padding: '3px 8px',
          fontSize: 'var(--text-xs)',
          display: 'flex',
          alignItems: 'center',
          gap: 4,
        }}
      >
        <span>⚙</span>
        <span style={{
          width: 6,
          height: 6,
          borderRadius: '50%',
          background: apiKeyConfigured ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)',
          display: 'inline-block',
        }} />
      </button>

      {open && (
        <div style={{
          position: 'absolute',
          top: 'calc(100% + 6px)',
          right: 0,
          width: 340,
          background: 'var(--color-bg-secondary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-lg)',
          padding: 'var(--space-md)',
          boxShadow: '0 12px 32px rgba(0,0,0,0.24)',
          zIndex: 20,
          display: 'flex',
          flexDirection: 'column',
          gap: 'var(--space-md)',
        }}>

          {/* ── Section: Claude API Key ── */}
          <Section title={t('settings')}>
            <StatusRow configured={apiKeyConfigured} label={apiKeyConfigured ? `${t('apiKeyConfigured')}: ${apiKeyMasked}` : t('apiKeyNotConfigured')} />
            <FieldLabel>{t('apiKey')}</FieldLabel>
            <KeyInputRow
              value={inputKey}
              show={showKey}
              placeholder={t('apiKeyPlaceholder')}
              onChange={setInputKey}
              onToggleShow={() => setShowKey(p => !p)}
              onKeyDown={(e) => { if (e.key === 'Enter') handleSaveClaude(); }}
              showLabel={showKey ? t('apiKeyHideKey') : t('apiKeyShowKey')}
            />
            <SaveRow
              disabled={!inputKey.trim()}
              onSave={handleSaveClaude}
              result={saveResult}
              savedText={t('apiKeySaved')}
              errorText={t('apiKeySaveError')}
              saveLabel={t('apiKeySave')}
            />
            <HelpText>{t('apiKeyHelp')}</HelpText>
          </Section>

          <Divider />

          {/* ── Section: Claude Model ── */}
          <Section title={t('claudeModel')}>
            {!apiKeyConfigured ? (
              <HelpText>{t('claudeModelHelp')}</HelpText>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {CLAUDE_MODELS.map(m => (
                  <ModelOption
                    key={m.id}
                    id={m.id}
                    label={m.label}
                    cost={m.cost}
                    note={m.note}
                    recommended={'recommended' in m && m.recommended}
                    selected={claudeModel === m.id}
                    onSelect={() => onSaveModel(m.id)}
                  />
                ))}
              </div>
            )}
          </Section>

          <Divider />

          {/* ── Section: Gemini API Key (disabled — RAG coming soon) ── */}
          <Section title={t('geminiSection')}>
            <div style={{ opacity: 0.45, pointerEvents: 'none' }}>
              <KeyInputRow
                value={geminiInput}
                show={showGemini}
                placeholder={t('geminiKeyPlaceholder')}
                onChange={setGeminiInput}
                onToggleShow={() => setShowGemini(p => !p)}
                onKeyDown={() => {}}
                showLabel={showGemini ? t('apiKeyHideKey') : t('apiKeyShowKey')}
              />
              <SaveRow
                disabled={true}
                onSave={() => {}}
                result="idle"
                savedText={t('geminiKeySaved')}
                errorText={t('geminiKeySaveError')}
                saveLabel={t('apiKeySave')}
              />
            </div>
            <HelpText>{t('geminiKeyHelp')}</HelpText>
          </Section>

          <Divider />

          {/* ── Section: Feedback ── */}
          <Section title={t('feedbackSectionTitle')}>
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <button
                onClick={() => onOpenUrl(FEEDBACK_URL_BUG)}
                style={feedbackButtonStyle}
              >
                🐛 {t('reportBug')}
              </button>
              <button
                onClick={() => onOpenUrl(FEEDBACK_URL_FEATURE)}
                style={feedbackButtonStyle}
              >
                💡 {t('suggestFeature')}
              </button>
            </div>
          </Section>

        </div>
      )}
    </div>
  );
}

// ── Sub-components ──────────────────────────────────────────────

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 'var(--space-xs)' }}>
      <div style={{ fontWeight: 600, fontSize: 'var(--text-sm)', marginBottom: 2 }}>{title}</div>
      {children}
    </div>
  );
}

function Divider() {
  return <div style={{ height: 1, background: 'var(--color-border)', margin: '0 -4px' }} />;
}

function StatusRow({ configured, label }: { configured: boolean; label: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-xs)' }}>
      <span style={{
        width: 8, height: 8, borderRadius: '50%', flexShrink: 0,
        background: configured ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)',
      }} />
      <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)' }}>{label}</span>
    </div>
  );
}

function FieldLabel({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontWeight: 500, fontSize: 'var(--text-xs)', color: 'var(--color-text-primary)', marginTop: 2 }}>
      {children}
    </div>
  );
}

function HelpText({ children }: { children: React.ReactNode }) {
  return (
    <div style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)', lineHeight: 1.5 }}>
      {children}
    </div>
  );
}

function KeyInputRow({
  value, show, placeholder, onChange, onToggleShow, onKeyDown, showLabel,
}: {
  value: string;
  show: boolean;
  placeholder: string;
  onChange: (v: string) => void;
  onToggleShow: () => void;
  onKeyDown: (e: React.KeyboardEvent) => void;
  showLabel: string;
}) {
  return (
    <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
      <input
        type={show ? 'text' : 'password'}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={onKeyDown}
        placeholder={placeholder}
        style={{
          flex: 1,
          padding: '5px 8px',
          background: 'var(--color-bg-tertiary)',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-md)',
          color: 'var(--color-text-primary)',
          fontSize: 'var(--text-xs)',
          fontFamily: 'monospace',
          outline: 'none',
        }}
      />
      <button onClick={onToggleShow} style={smallButtonStyle}>{showLabel}</button>
    </div>
  );
}

function SaveRow({
  disabled, onSave, result, savedText, errorText, saveLabel,
}: {
  disabled: boolean;
  onSave: () => void;
  result: 'idle' | 'saved' | 'error';
  savedText: string;
  errorText: string;
  saveLabel: string;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-sm)' }}>
      <button
        onClick={onSave}
        disabled={disabled}
        style={{ ...saveButtonStyle, opacity: disabled ? 0.5 : 1, cursor: disabled ? 'default' : 'pointer' }}
      >
        {saveLabel}
      </button>
      {result === 'saved' && <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-accent)' }}>✓ {savedText}</span>}
      {result === 'error' && <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-error, #ef4444)' }}>{errorText}</span>}
    </div>
  );
}

function ModelOption({
  label, cost, note, recommended, selected, onSelect,
}: {
  id?: string;
  label: string;
  cost: string;
  note: string;
  recommended?: boolean;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      onClick={onSelect}
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        padding: '6px 10px',
        background: selected ? 'var(--color-accent)' : 'var(--color-bg-tertiary)',
        border: `1px solid ${selected ? 'var(--color-accent)' : 'var(--color-border)'}`,
        borderRadius: 'var(--radius-md)',
        cursor: 'pointer',
        color: selected ? '#fff' : 'var(--color-text-primary)',
        width: '100%',
        textAlign: 'left',
        gap: 8,
      }}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
        <span style={{ fontSize: 'var(--text-xs)', fontWeight: 600 }}>
          {label}
          {recommended && (
            <span style={{
              marginLeft: 6,
              fontSize: 10,
              padding: '1px 5px',
              borderRadius: 4,
              background: selected ? 'rgba(255,255,255,0.25)' : 'var(--color-accent)',
              color: '#fff',
              verticalAlign: 'middle',
            }}>추천</span>
          )}
        </span>
        <span style={{ fontSize: 10, opacity: 0.75 }}>{note}</span>
      </div>
      <span style={{ fontSize: 10, opacity: 0.85, whiteSpace: 'nowrap', flexShrink: 0 }}>{cost}</span>
    </button>
  );
}

const smallButtonStyle: React.CSSProperties = {
  padding: '4px 8px',
  background: 'var(--color-bg-tertiary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  whiteSpace: 'nowrap',
};

const saveButtonStyle: React.CSSProperties = {
  padding: 'var(--space-xs) var(--space-md)',
  background: 'var(--color-accent)',
  border: 'none',
  borderRadius: 'var(--radius-md)',
  color: '#fff',
  fontSize: 'var(--text-sm)',
  cursor: 'pointer',
};

const feedbackButtonStyle: React.CSSProperties = {
  flex: 1,
  padding: '6px 8px',
  background: 'var(--color-bg-tertiary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  color: 'var(--color-text-muted)',
  fontSize: 'var(--text-xs)',
  cursor: 'pointer',
  textAlign: 'center' as const,
};
