import { useState, useEffect, useRef } from 'react';
import { t } from '../i18n';

const FEEDBACK_URL_BUG = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';
const FEEDBACK_URL_FEATURE = 'https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose';

type Provider = 'anthropic' | 'openai' | 'gemini' | 'local';
type SaveResult = 'idle' | 'saved' | 'error';

// Model catalogue exposed in v1.1.x. Order = display order.
// Labels/notes localised via t() at render time.
// `speed` is observed responsiveness on typical Revit codegen tasks (Apr 2026):
//   '⚡⚡⚡' fast | '⚡⚡' medium | '⚡' slow
type SpeedRating = '⚡⚡⚡' | '⚡⚡' | '⚡';

type ModelNoteKey =
  | 'modelNoteSonnet'
  | 'modelNoteOpus47'
  | 'modelNoteGpt55'
  | 'modelNoteGemini31Pro'
  | 'modelNoteGemma4Local'
  | 'modelNoteLlama33Local'
  | 'modelNoteCodestralLocal';

const MODELS: ReadonlyArray<{
  id: string;
  label: string;
  cost: string;
  speed: SpeedRating;
  speedKey: 'modelSpeedFast' | 'modelSpeedMedium' | 'modelSpeedSlow';
  noteKey: ModelNoteKey;
  provider: Provider;
  recommended?: boolean;
}> = [
  { id: 'claude-sonnet-4-6',     label: 'Claude Sonnet 4.6', cost: '~$0.04', speed: '⚡⚡⚡', speedKey: 'modelSpeedFast',   noteKey: 'modelNoteSonnet',       provider: 'anthropic', recommended: true },
  { id: 'claude-opus-4-7',       label: 'Claude Opus 4.7',   cost: '~$0.20', speed: '⚡⚡',  speedKey: 'modelSpeedMedium', noteKey: 'modelNoteOpus47',       provider: 'anthropic' },
  { id: 'gpt-5.5',                label: 'GPT-5.5',           cost: '~$0.08', speed: '⚡⚡',  speedKey: 'modelSpeedMedium', noteKey: 'modelNoteGpt55',        provider: 'openai' },
  { id: 'gemini-3.1-pro-preview', label: 'Gemini 3.1 Pro',    cost: '~$0.03', speed: '⚡',    speedKey: 'modelSpeedSlow',   noteKey: 'modelNoteGemini31Pro',  provider: 'gemini' },
  // Local self-hosted (BIBIM-validated against the OpenRouter sweep, 2026-05-10).
  // For local models, the "cost" column shows estimated VRAM at 4-bit quantization
  // — there's no per-call dollar cost, but hardware is the real constraint.
  { id: 'google/gemma-4-26b-a4b-it',         label: 'Gemma 4 26B A4B IT',      cost: '~16GB VRAM', speed: '⚡',   speedKey: 'modelSpeedSlow',   noteKey: 'modelNoteGemma4Local',     provider: 'local' },
  { id: 'meta-llama/llama-3.3-70b-instruct', label: 'Llama 3.3 70B Instruct',  cost: '~40GB VRAM', speed: '⚡⚡', speedKey: 'modelSpeedMedium', noteKey: 'modelNoteLlama33Local',    provider: 'local' },
  { id: 'mistralai/codestral-2508',          label: 'Codestral 2508',          cost: '~14GB VRAM', speed: '⚡⚡⚡',speedKey: 'modelSpeedFast',   noteKey: 'modelNoteCodestralLocal',  provider: 'local' },
];

type LocalConnectionStatus = {
  state: 'idle' | 'testing' | 'success' | 'error';
  modelCount?: number;
  firstModel?: string;
  /** Full list of model ids advertised by the server's /v1/models response.
   *  Used to populate the auto-discovered model picker so the user can pick
   *  with one click instead of typing the exact server-side name. */
  models?: string[];
  error?: string;
};

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
  // Local self-hosted LLM (v1.1.x+)
  localConfigured: boolean;
  localServerUrl: string;
  localModelName: string;
  localMasked: string;
  localSaveResult: SaveResult;
  localConnectionStatus: LocalConnectionStatus;
  onSaveLocalLlmConfig: (serverUrl: string, modelName: string, apiKey: string) => void;
  onTestLocalConnection: (serverUrl: string, apiKey: string) => void;
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
    localConfigured, localServerUrl, localModelName, localMasked, localSaveResult,
    localConnectionStatus, onSaveLocalLlmConfig, onTestLocalConnection,
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
  const activeReady = isProviderReady(
    activeProvider, anthropicConfigured, openaiConfigured, geminiConfigured, localConfigured,
  );

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
                const enabled = isProviderReady(
                  m.provider, anthropicConfigured, openaiConfigured, geminiConfigured, localConfigured,
                );
                // Local-provider models don't have a per-call dollar cost — suppress unit.
                const costLabel = m.provider === 'local' ? m.cost : `${m.cost} ${t('modelCostUnit')}`;
                return (
                  <ModelOption
                    key={m.id}
                    label={m.label}
                    cost={costLabel}
                    note={t(m.noteKey)}
                    speed={m.speed}
                    speedTooltip={t(m.speedKey)}
                    recommended={m.recommended}
                    selected={activeModel === m.id}
                    enabled={enabled}
                    lockTooltip={
                      m.provider === 'anthropic' ? t('modelLockedTooltipAnthropic') :
                      m.provider === 'openai'    ? t('modelLockedTooltipOpenAI') :
                      m.provider === 'gemini'    ? t('modelLockedTooltipGemini') :
                                                   t('modelLockedTooltipLocal')
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

          {/* ── Section: Local Self-hosted LLM (v1.1.x+) ── */}
          <LocalLlmSection
            configured={localConfigured}
            initialServerUrl={localServerUrl}
            initialModelName={localModelName}
            maskedKey={localMasked}
            saveResult={localSaveResult}
            connectionStatus={localConnectionStatus}
            onSave={onSaveLocalLlmConfig}
            onTest={onTestLocalConnection}
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

function isProviderReady(
  p: Provider,
  anth: boolean,
  oai: boolean,
  gem: boolean,
  local: boolean,
): boolean {
  switch (p) {
    case 'anthropic': return anth;
    case 'openai':    return oai;
    case 'gemini':    return gem;
    case 'local':     return local;
  }
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

function LocalLlmSection({
  configured, initialServerUrl, initialModelName, maskedKey, saveResult,
  connectionStatus, onSave, onTest,
}: {
  configured: boolean;
  initialServerUrl: string;
  initialModelName: string;
  maskedKey: string;
  saveResult: SaveResult;
  connectionStatus: LocalConnectionStatus;
  onSave: (serverUrl: string, modelName: string, apiKey: string) => void;
  onTest: (serverUrl: string, apiKey: string) => void;
}) {
  const [serverUrl, setServerUrl] = useState(initialServerUrl);
  const [modelOverride, setModelOverride] = useState(initialModelName);
  const [apiKey, setApiKey] = useState('');
  const [showKey, setShowKey] = useState(false);
  // Auto-expand Advanced section if any advanced field already has a saved value
  // (API key configured or manual model name override). New users see it collapsed.
  const [advancedOpen, setAdvancedOpen] = useState(Boolean(maskedKey) || Boolean(initialModelName));

  // Reflect external prop changes (e.g. config reload) into local state
  useEffect(() => { setServerUrl(initialServerUrl); }, [initialServerUrl]);
  useEffect(() => { setModelOverride(initialModelName); }, [initialModelName]);

  const handleTestAndSave = () => {
    const u = serverUrl.trim();
    if (!u) return;
    // Single combined action: probe the server, and if it answers, save the
    // URL + model override + API key in one shot. The /v1/models response
    // (delivered via the connectionStatus prop) becomes the source of truth
    // for the model picker that appears below on success.
    onTest(u, apiKey.trim());
    onSave(u, modelOverride.trim(), apiKey.trim());
  };

  // When the user picks a different model from the auto-discovered list,
  // save immediately (no extra button click). The override field stays in
  // sync so the user can see what's currently selected.
  const handleModelPick = (id: string) => {
    setModelOverride(id);
    onSave(serverUrl.trim(), id, apiKey.trim());
  };

  // Status dot colour: green if last test succeeded OR (no test run yet AND configured),
  // red if last test failed, yellow if neither configured nor tested.
  const dotColor = (() => {
    if (connectionStatus.state === 'success') return 'var(--color-accent)';
    if (connectionStatus.state === 'error')   return 'var(--color-error, #ef4444)';
    if (connectionStatus.state === 'testing') return 'var(--color-text-muted)';
    return configured ? 'var(--color-accent)' : 'var(--color-warning, #f59e0b)';
  })();

  const statusLabel = (() => {
    if (connectionStatus.state === 'testing') return t('localTesting');
    if (connectionStatus.state === 'success')
      return t('localTestSuccess').replace('{count}', String(connectionStatus.modelCount ?? 0));
    if (connectionStatus.state === 'error')
      return `${t('localTestFailure')}: ${connectionStatus.error ?? ''}`;
    return configured ? t('localConfigured') : t('localNotConfigured');
  })();

  const discoveredModels = connectionStatus.models ?? [];
  const showModelPicker = connectionStatus.state === 'success' && discoveredModels.length > 0;
  // The currently-active model: override wins if user typed one, else the first
  // discovered id (which is also what LocalProvider lazy-resolves to).
  const activeModel = modelOverride.trim() || discoveredModels[0] || '';

  return (
    <Section title={t('localSection')}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-xs)' }}>
        <span style={{
          width: 8, height: 8, borderRadius: '50%', flexShrink: 0, background: dotColor,
        }} />
        <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-text-muted)' }}>
          {statusLabel}
        </span>
      </div>

      {/* Server URL — the only required field */}
      <LabeledRow label={
        <span>{t('localServerUrlLabel')} <span style={{ color: 'var(--color-error, #ef4444)' }}>*</span></span>
      }>
        <input
          type="text"
          value={serverUrl}
          onChange={(e) => setServerUrl(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter') handleTestAndSave(); }}
          placeholder={t('localServerUrlPlaceholder')}
          style={textInputStyle}
        />
      </LabeledRow>

      {/* Combined Test + Save — the primary call to action */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--space-sm)', flexWrap: 'wrap' }}>
        <button
          onClick={handleTestAndSave}
          disabled={!serverUrl.trim() || connectionStatus.state === 'testing'}
          style={{
            ...saveButtonStyle,
            opacity: serverUrl.trim() && connectionStatus.state !== 'testing' ? 1 : 0.5,
            cursor: serverUrl.trim() && connectionStatus.state !== 'testing' ? 'pointer' : 'default',
          }}
        >
          {connectionStatus.state === 'testing' ? t('localTesting') : t('localTestAndSave')}
        </button>
        {saveResult === 'saved' && (
          <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-accent)' }}>
            ✓ {t('apiKeySaved')}
          </span>
        )}
        {saveResult === 'error' && (
          <span style={{ fontSize: 'var(--text-xs)', color: 'var(--color-error, #ef4444)' }}>
            {t('apiKeySaveError')}
          </span>
        )}
      </div>

      {/* Auto-discovered model picker — only after a successful probe */}
      {showModelPicker && (
        <LabeledRow label={t('localModelPickerLabel')}>
          <select
            value={activeModel}
            onChange={(e) => handleModelPick(e.target.value)}
            style={{ ...textInputStyle, fontFamily: 'monospace' }}
          >
            {discoveredModels.map(id => (
              <option key={id} value={id}>{id}</option>
            ))}
            {/* If the saved override isn't in the discovered list (server changed
                between sessions), keep it as a selectable option so the user can
                see what's currently in effect. */}
            {modelOverride.trim() && !discoveredModels.includes(modelOverride.trim()) && (
              <option value={modelOverride.trim()}>{modelOverride.trim()} (saved override)</option>
            )}
          </select>
        </LabeledRow>
      )}

      <HelpText>{t('localWarnTools')}</HelpText>
      <HelpText>{t('localHelp')}</HelpText>

      {/* Advanced (optional) — API key + manual model override */}
      <button
        onClick={() => setAdvancedOpen(v => !v)}
        style={{
          background: 'none', border: 'none', cursor: 'pointer',
          color: 'var(--color-text-muted)', fontSize: 'var(--text-xs)',
          padding: 0, textAlign: 'left', display: 'flex', alignItems: 'center', gap: 4,
        }}
      >
        <span>{advancedOpen ? '▾' : '▸'}</span>
        <span>{t('localAdvancedToggle')}</span>
      </button>

      {advancedOpen && (
        <div style={{
          display: 'flex', flexDirection: 'column', gap: 'var(--space-xs)',
          paddingLeft: 'var(--space-sm)',
          borderLeft: '2px solid var(--color-border)',
          marginLeft: 2,
        }}>
          {/* API key — optional, sent as Authorization: Bearer */}
          <LabeledRow
            label={
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                {t('localApiKeyLabel')}
                <span
                  title={t('localApiKeyTooltip')}
                  style={{
                    cursor: 'help',
                    width: 14, height: 14, borderRadius: '50%',
                    background: 'var(--color-bg-tertiary)',
                    border: '1px solid var(--color-border)',
                    color: 'var(--color-text-muted)',
                    fontSize: 9, fontWeight: 700,
                    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                  }}
                >?</span>
              </span>
            }
          >
            <div style={{ display: 'flex', gap: 'var(--space-xs)' }}>
              <input
                type={showKey ? 'text' : 'password'}
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                placeholder={maskedKey ? maskedKey : t('localApiKeyPlaceholder')}
                style={{ ...textInputStyle, fontFamily: 'monospace' }}
              />
              <button onClick={() => setShowKey(s => !s)} style={smallButtonStyle}>
                {showKey ? t('apiKeyHideKey') : t('apiKeyShowKey')}
              </button>
            </div>
          </LabeledRow>

          {/* Manual model override — for users whose server names a model
              differently from the discovered list, or who want to force a
              specific variant. Leave blank to use auto-discovery. */}
          <LabeledRow label={t('localModelOverrideLabel')}>
            <input
              type="text"
              value={modelOverride}
              onChange={(e) => setModelOverride(e.target.value)}
              placeholder={t('localModelOverridePlaceholder')}
              style={textInputStyle}
            />
          </LabeledRow>
        </div>
      )}
    </Section>
  );
}

function LabeledRow({ label, children }: { label: React.ReactNode; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <span style={{ fontSize: 10, color: 'var(--color-text-muted)', fontWeight: 500 }}>{label}</span>
      {children}
    </div>
  );
}

const textInputStyle: React.CSSProperties = {
  flex: 1,
  width: '100%',
  padding: '5px 8px',
  background: 'var(--color-bg-tertiary)',
  border: '1px solid var(--color-border)',
  borderRadius: 'var(--radius-md)',
  color: 'var(--color-text-primary)',
  fontSize: 'var(--text-xs)',
  outline: 'none',
  boxSizing: 'border-box',
};

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
  speed: SpeedRating;
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
