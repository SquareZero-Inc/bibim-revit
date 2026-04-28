import { useState, useEffect, useRef } from 'react';
import { t } from '../i18n';

const FEEDBACK_URL_BUG = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';
const FEEDBACK_URL_FEATURE = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';

type Provider = 'anthropic' | 'openai' | 'gemini';
type SaveResult = 'idle' | 'saved' | 'error';

// Model catalogue exposed in v1.1.0. Order = display order.
// Labels/notes localised via t() at render time.
// `speed` is observed responsiveness on typical Revit codegen tasks (Apr 2026):
//   '⚡⚡⚡' fast | '⚡⚡' medium | '⚡' slow
type SpeedRating = '⚡⚡⚡' | '⚡⚡' | '⚡';

const MODELS: ReadonlyArray<{
  id: string;
  label: string;
  cost: string;
  speed: SpeedRating;
  speedKey: 'modelSpeedFast' | 'modelSpeedMedium' | 'modelSpeedSlow';
  noteKey: 'modelNoteSonnet' | 'modelNoteOpus47' | 'modelNoteGpt55' | 'modelNoteGemini31Pro';
  provider: Provider;
  recommended?: boolean;
}> = [
  { id: 'claude-sonnet-4-6',     label: 'Claude Sonnet 4.6', cost: '~$0.04', speed: '⚡⚡⚡', speedKey: 'modelSpeedFast',   noteKey: 'modelNoteSonnet',       provider: 'anthropic', recommended: true },
  { id: 'claude-opus-4-7',       label: 'Claude Opus 4.7',   cost: '~$0.20', speed: '⚡⚡',  speedKey: 'modelSpeedMedium', noteKey: 'modelNoteOpus47',       provider: 'anthropic' },
  { id: 'gpt-5.5',                label: 'GPT-5.5',           cost: '~$0.08', speed: '⚡⚡',  speedKey: 'modelSpeedMedium', noteKey: 'modelNoteGpt55',        provider: 'openai' },
  { id: 'gemini-3.1-pro-preview', label: 'Gemini 3.1 Pro',    cost: '~$0.03', speed: '⚡',    speedKey: 'modelSpeedSlow',   noteKey: 'modelNoteGemini31Pro',  provider: 'gemini' },
];

interface Props {
  // Anthropic
  anthropicConfigured: boolean;
  anthropicMasked: string;
  onSaveAnthropicKey: (key: string) => void;
  anthropicSaveResult: SaveResult;
  // OpenAI
  openaiConfigured: boolean;
  openaiMasked: string;
  onSaveOpenAiKey: (key: string) => void;
  openaiSaveResult: SaveResult;
  // Gemini
  geminiConfigured: boolean;
  geminiMasked: string;
  onSaveGeminiKey: (key: string) => void;
  geminiSaveResult: SaveResult;
  // Active model
  activeModel: string;
  onSaveModel: (modelId: string) => void;
  // Misc
  onOpenUrl: (url: string) => void;
}

export default function SettingsPanel(props: Props) {
  const {
    anthropicConfigured, anthropicMasked, onSaveAnthropicKey, anthropicSaveResult,
    openaiConfigured, openaiMasked, onSaveOpenAiKey, openaiSaveResult,
    geminiConfigured, geminiMasked, onSaveGeminiKey, geminiSaveResult,
    activeModel, onSaveModel, onOpenUrl,
  } = props;

  const [open, setOpen] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);

  // Close on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  // The header gear icon turns green only if the *active* model has its key configured.
  const activeProvider = MODELS.find(m => m.id === activeModel)?.provider ?? 'anthropic';
  const activeReady = isProviderReady(activeProvider, anthropicConfigured, openaiConfigured, geminiConfigured);

  return (
    <div ref={panelRef} style={{ position: 'relative' }}>
      <button
        onClick={() => setOpen(prev => !prev)}
        title={t('settings')}
        style={{
          background: 'none',
          border: '1px solid var(--color-border)',
          borderRadius: 'var(--radius-full)',
          cursor: 'pointer',
          color: activeReady ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)',
          padding: '3px 8px',
          fontSize: 'var(--text-xs)',
          display: 'flex',
          alignItems: 'center',
          gap: 4,
        }}
      >
        <span>⚙</span>
        <span style={{
          width: 6, height: 6, borderRadius: '50%',
          background: activeReady ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)',
          display: 'inline-block',
        }} />
      </button>

      {open && (
        <div style={{
          position: 'absolute',
          top: 'calc(100% + 6px)',
          right: 0,
          width: 360,
          maxHeight: '78vh',
          overflowY: 'auto',
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

          {/* ── Top: API Key Setup Guide link (language-aware) ── */}
          <button
            onClick={() => onOpenUrl(t('apiKeyGuideUrl'))}
            style={guideButtonStyle}
          >
            {t('apiKeyGuideLink')}
          </button>

          {/* ── Section: Active Model ── */}
          <Section title={t('claudeModel')}>
            <HelpText>{t('claudeModelHelp')}</HelpText>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
              {MODELS.map(m => {
                const enabled = isProviderReady(m.provider, anthropicConfigured, openaiConfigured, geminiConfigured);
                return (
                  <ModelOption
                    key={m.id}
                    label={m.label}
                    cost={`${m.cost} ${t('modelCostUnit')}`}
                    note={t(m.noteKey)}
                    speed={m.speed}
                    speedTooltip={t(m.speedKey)}
                    recommended={m.recommended}
                    selected={activeModel === m.id}
                    enabled={enabled}
                    lockTooltip={
                      m.provider === 'anthropic' ? t('modelLockedTooltipAnthropic') :
                      m.provider === 'openai'    ? t('modelLockedTooltipOpenAI') :
                                                   t('modelLockedTooltipGemini')
                    }
                    lockBadge={t('modelLocked')}
                    onSelect={() => enabled && onSaveModel(m.id)}
                  />
                );
              })}
            </div>
          </Section>

          <Divider />

          {/* ── Section: Anthropic Key ── */}
          <ProviderKeySection
            title={t('apiKey')}
            placeholder={t('apiKeyPlaceholder')}
            configured={anthropicConfigured}
            masked={anthropicMasked}
            saveResult={anthropicSaveResult}
            onSave={onSaveAnthropicKey}
            help={t('apiKeyHelp')}
          />

          <Divider />

          {/* ── Section: OpenAI Key ── */}
          <ProviderKeySection
            title={t('openaiSection')}
            placeholder={t('openaiKeyPlaceholder')}
            configured={openaiConfigured}
            masked={openaiMasked}
            saveResult={openaiSaveResult}
            onSave={onSaveOpenAiKey}
            help={t('openaiKeyHelp')}
          />

          <Divider />

          {/* ── Section: Gemini Key ── */}
          <ProviderKeySection
            title={t('geminiSection')}
            placeholder={t('geminiKeyPlaceholder')}
            configured={geminiConfigured}
            masked={geminiMasked}
            saveResult={geminiSaveResult}
            onSave={onSaveGeminiKey}
            help={t('geminiKeyHelp')}
          />

          <Divider />

          {/* ── Section: Feedback ── */}
          <Section title={t('feedbackSectionTitle')}>
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <button onClick={() => onOpenUrl(FEEDBACK_URL_BUG)} style={feedbackButtonStyle}>
                🐛 {t('reportBug')}
              </button>
              <button onClick={() => onOpenUrl(FEEDBACK_URL_FEATURE)} style={feedbackButtonStyle}>
                💡 {t('suggestFeature')}
              </button>
            </div>
          </Section>

        </div>
      )}
    </div>
  );
}

function isProviderReady(p: Provider, anth: boolean, oai: boolean, gem: boolean): boolean {
  return p === 'anthropic' ? anth : p === 'openai' ? oai : gem;
}

// ── Sub-components ──────────────────────────────────────────────

function ProviderKeySection({
  title, placeholder, configured, masked, saveResult, onSave, help,
}: {
  title: string;
  placeholder: string;
  configured: boolean;
  masked: string;
  saveResult: SaveResult;
  onSave: (key: string) => void;
  help: string;
}) {
  const [input, setInput] = useState('');
  const [show, setShow] = useState(false);

  const handleSave = () => {
    const trimmed = input.trim();
    if (!trimmed) return;
    onSave(trimmed);
    setInput('');
    setShow(false);
  };

  return (
    <Section title={title}>
      <StatusRow
        configured={configured}
        label={configured ? `${t('apiKeyConfigured')}: ${masked}` : t('apiKeyNotConfigured')}
      />
      <KeyInputRow
        value={input}
        show={show}
        placeholder={placeholder}
        onChange={setInput}
        onToggleShow={() => setShow(p => !p)}
        onKeyDown={(e) => { if (e.key === 'Enter') handleSave(); }}
        showLabel={show ? t('apiKeyHideKey') : t('apiKeyShowKey')}
      />
      <SaveRow
        disabled={!input.trim()}
        onSave={handleSave}
        result={saveResult}
        savedText={t('apiKeySaved')}
        errorText={t('apiKeySaveError')}
        saveLabel={t('apiKeySave')}
      />
      <HelpText>{help}</HelpText>
    </Section>
  );
}

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
  result: SaveResult;
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
  label, cost, note, speed, speedTooltip, recommended, selected, enabled, lockTooltip, lockBadge, onSelect,
}: {
  label: string;
  cost: string;
  note: string;
  speed: '⚡⚡⚡' | '⚡⚡' | '⚡';
  speedTooltip: string;
  recommended?: boolean;
  selected: boolean;
  enabled: boolean;
  lockTooltip: string;
  lockBadge: string;
  onSelect: () => void;
}) {
  // Disabled = greyed + cursor not-allowed + tooltip hint
  const baseColor = enabled
    ? (selected ? 'var(--color-accent)' : 'var(--color-bg-tertiary)')
    : 'var(--color-bg-tertiary)';
  const borderColor = enabled
    ? (selected ? 'var(--color-accent)' : 'var(--color-border)')
    : 'var(--color-border)';

  return (
    <button
      onClick={onSelect}
      disabled={!enabled}
      title={enabled ? '' : lockTooltip}
      style={{
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        padding: '6px 10px',
        background: baseColor,
        border: `1px solid ${borderColor}`,
        borderRadius: 'var(--radius-md)',
        cursor: enabled ? 'pointer' : 'not-allowed',
        color: selected && enabled ? '#fff' : 'var(--color-text-primary)',
        opacity: enabled ? 1 : 0.45,
        width: '100%',
        textAlign: 'left',
        gap: 8,
      }}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
        <span style={{ fontSize: 'var(--text-xs)', fontWeight: 600 }}>
          {label}
          {recommended && enabled && (
            <span style={{
              marginLeft: 6,
              fontSize: 10,
              padding: '1px 5px',
              borderRadius: 4,
              background: selected ? 'rgba(255,255,255,0.25)' : 'var(--color-accent)',
              color: '#fff',
              verticalAlign: 'middle',
            }}>★</span>
          )}
          <span
            title={speedTooltip}
            style={{
              marginLeft: 6,
              fontSize: 10,
              opacity: enabled ? 0.85 : 0.5,
              verticalAlign: 'middle',
              letterSpacing: -1,
            }}
          >
            {speed}
          </span>
        </span>
        <span style={{ fontSize: 10, opacity: 0.75 }}>
          {enabled ? note : lockBadge}
        </span>
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

const guideButtonStyle: React.CSSProperties = {
  width: '100%',
  padding: '8px 10px',
  background: 'var(--color-bg-tertiary)',
  border: '1px solid var(--color-accent)',
  borderRadius: 'var(--radius-md)',
  color: 'var(--color-accent)',
  fontSize: 'var(--text-xs)',
  fontWeight: 600,
  cursor: 'pointer',
  textAlign: 'center' as const,
};
