import { useMemo, useState } from 'react';
import { t } from './i18n';
import { useChat } from './hooks/useChat';
import { sendToBackend } from './bridge';
import ChatPanel from './components/ChatPanel';
import HistoryPanel from './components/HistoryPanel';
import CodeLibraryPanel from './components/CodeLibraryPanel';
import CodeDetailModal from './components/CodeDetailModal';
import SettingsPanel from './components/SettingsPanel';
import RerunModal from './components/RerunModal';

export default function App() {
  const chat = useChat();
  const [showHistory, setShowHistory] = useState(false);
  const [showCodeLibrary, setShowCodeLibrary] = useState(false);
  const [rerunDraft, setRerunDraft] = useState<{
    sourceSessionId: string;
    sourceTitle: string;
    sourceCreatedAt: string;
    code: string;
  } | null>(null);

  const activeSession = useMemo(
    () => chat.sessions.find((session) => session.id === chat.activeSessionId) ?? null,
    [chat.activeSessionId, chat.sessions],
  );

  return (
    <div style={{
      display: 'flex',
      height: '100%',
      overflow: 'hidden',
    }}>
      {showHistory && (
        <div style={{ width: 280, flexShrink: 0 }}>
          <HistoryPanel
            sessions={chat.sessions}
            activeId={chat.activeSessionId}
            pendingLoadSessionId={chat.pendingLoadSessionId}
            onSelect={chat.loadSession}
            onNew={chat.newSession}
            onClose={() => setShowHistory(false)}
            onDelete={chat.deleteSession}
            onRename={chat.renameSession}
            onConfirmLoad={chat.confirmLoadSession}
            onCancelLoad={chat.cancelLoadSession}
          />
        </div>
      )}

      {showCodeLibrary && (
        <div style={{ width: 280, flexShrink: 0 }}>
          <CodeLibraryPanel
            snippets={chat.codeSnippets}
            folders={chat.codeFolders}
            onSelect={(id) => chat.fetchCodeSnippet(id)}
            onClose={() => setShowCodeLibrary(false)}
            onRenameSnippet={chat.renameSnippet}
            onDeleteSnippet={(id) => chat.deleteCodeSnippet(id)}
            onMoveSnippet={chat.moveSnippet}
            onCreateFolder={chat.createFolder}
            onRenameFolder={chat.renameFolder}
            onDeleteFolder={chat.deleteFolder}
          />
        </div>
      )}

      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <header style={{
          display: 'flex',
          alignItems: 'center',
          padding: 'var(--space-sm) var(--space-lg)',
          borderBottom: '1px solid var(--color-border)',
          gap: 'var(--space-sm)',
          flexShrink: 0,
        }}>
          <button
            onClick={() => setShowHistory((prev) => !prev)}
            style={{
              background: 'none',
              border: 'none',
              cursor: 'pointer',
              color: 'var(--color-text-muted)',
              fontSize: 'var(--text-xs)',
              padding: 'var(--space-xs)',
            }}
            title={t('history')}
          >
            Menu
          </button>
          <button
            onClick={() => setShowCodeLibrary((prev) => !prev)}
            style={{
              background: 'none',
              border: 'none',
              cursor: 'pointer',
              color: 'var(--color-text-muted)',
              fontSize: 'var(--text-xs)',
              padding: 'var(--space-xs)',
            }}
            title={t('codeLibrary')}
          >
            {'</>'}
          </button>
          <img src="./bibim-icon.png" alt="BIBIM" style={{ width: 18, height: 18 }} />
          <span style={{
            fontSize: 'var(--text-base)',
            fontWeight: 600,
            color: 'var(--color-accent)',
          }}>
            BIBIM AI
          </span>
          <span style={{
            fontSize: 'var(--text-xs)',
            color: 'var(--color-text-muted)',
          }}>
            {chat.appVersion ? `v${chat.appVersion}` : ''}
          </span>

          <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 'var(--space-sm)' }}>
            <SettingsPanel
              apiKeyConfigured={chat.apiKeyConfigured}
              apiKeyMasked={chat.apiKeyMasked}
              onSaveApiKey={chat.saveApiKey}
              saveResult={chat.apiKeySaveResult}
              claudeModel={chat.claudeModel}
              onSaveModel={chat.saveModel}
              geminiConfigured={chat.geminiConfigured}
              geminiMasked={chat.geminiMasked}
              onSaveGeminiApiKey={chat.saveGeminiApiKey}
              geminiSaveResult={chat.geminiKeySaveResult}
              onOpenUrl={(url) => sendToBackend('open_url', { url })}
            />
          </div>
        </header>

        {chat.updateInfo && (
          <div style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: 'var(--space-sm) var(--space-lg)',
            background: chat.updateInfo.isMandatory ? 'var(--color-error, #d32f2f)' : 'var(--color-accent)',
            color: '#fff',
            fontSize: 'var(--text-xs)',
            flexShrink: 0,
          }}>
            <span>
              {chat.downloadState === 'complete'
                ? t('downloadComplete')
                : chat.updateInfo.isMandatory ? t('updateMandatory') : t('updateAvailable')}
              {chat.downloadState === 'idle' && ` — v${chat.updateInfo.latestVersion}`}
              {(chat.downloadState === 'idle' && chat.updateInfo.releaseNotes) ? ` · ${chat.updateInfo.releaseNotes}` : ''}
            </span>
            <div style={{ display: 'flex', gap: 'var(--space-sm)' }}>
              {chat.downloadState === 'complete' ? (
                <button
                  onClick={() => chat.openDownloadFolder(chat.downloadFolderPath)}
                  style={{
                    background: '#fff',
                    color: chat.updateInfo.isMandatory ? 'var(--color-error, #d32f2f)' : 'var(--color-accent)',
                    border: 'none',
                    borderRadius: 4,
                    padding: '2px 10px',
                    cursor: 'pointer',
                    fontWeight: 600,
                    fontSize: 'var(--text-xs)',
                  }}
                >
                  {t('openFolder')}
                </button>
              ) : (
                <button
                  disabled={chat.downloadState === 'downloading'}
                  onClick={() => chat.startDownloadUpdate(chat.updateInfo!.downloadUrl ?? '', chat.updateInfo!.latestVersion ?? '')}
                  style={{
                    background: chat.downloadState === 'downloading' ? 'rgba(255,255,255,0.5)' : '#fff',
                    color: chat.updateInfo.isMandatory ? 'var(--color-error, #d32f2f)' : 'var(--color-accent)',
                    border: 'none',
                    borderRadius: 4,
                    padding: '2px 10px',
                    cursor: chat.downloadState === 'downloading' ? 'default' : 'pointer',
                    fontWeight: 600,
                    fontSize: 'var(--text-xs)',
                  }}
                >
                  {chat.downloadState === 'downloading' ? t('downloading') : t('updateNow')}
                </button>
              )}
              {!chat.updateInfo.isMandatory && chat.downloadState === 'idle' && (
                <button
                  onClick={chat.dismissUpdate}
                  style={{
                    background: 'transparent',
                    color: '#fff',
                    border: '1px solid rgba(255,255,255,0.5)',
                    borderRadius: 4,
                    padding: '2px 10px',
                    cursor: 'pointer',
                    fontSize: 'var(--text-xs)',
                  }}
                >
                  {t('dismiss')}
                </button>
              )}
            </div>
          </div>
        )}

        <ChatPanel
          messages={chat.messages}
          isBusy={chat.isBusy}
          isMandatoryUpdate={chat.updateInfo?.isMandatory === true}
          steps={chat.steps}
          currentTask={chat.currentTask}
          taskList={chat.taskList}
          appVersion={chat.appVersion}
          onSend={chat.sendMessage}
          onCancel={chat.cancelStreaming}
          onTaskConfirm={chat.confirmTask}
          onTaskCancel={chat.cancelTask}
          onApply={chat.executeCode}
          onUndo={chat.undoLastApply}
          onFeedback={chat.sendExecutionFeedback}
          onFeedbackDetail={chat.sendFeedbackDetail}
          onRegenerate={chat.sendRegenerateWithFeedback}
          onWarningResponse={chat.sendWarningResponse}
          onRerun={(code, createdAt) => {
            setRerunDraft({
              sourceSessionId: chat.activeSessionId ?? '',
              sourceTitle: activeSession?.title ?? t('untitled'),
              sourceCreatedAt: createdAt,
              code,
            });
          }}
          onQuestionAnswers={chat.submitQuestionAnswers}
        />
      </div>

      {chat.selectedSnippet && (
        <CodeDetailModal
          snippet={chat.selectedSnippet}
          onRun={(code) => {
            const s = chat.selectedSnippet;
            if (!s) return;
            chat.clearSelectedSnippet();
            setShowCodeLibrary(false);
            chat.rerunCode(s.sourceSessionId ?? '', s.title, code);
          }}
          onEdit={(code) => {
            const s = chat.selectedSnippet;
            if (!s) return;
            chat.clearSelectedSnippet();
            setShowCodeLibrary(false);
            chat.editCodeFromLibrary(s.title, code);
          }}
          onDelete={(id) => {
            chat.deleteCodeSnippet(id);
          }}
          onClose={chat.clearSelectedSnippet}
        />
      )}

      <RerunModal
        open={rerunDraft != null}
        sourceTitle={rerunDraft?.sourceTitle ?? ''}
        sourceCreatedAt={rerunDraft?.sourceCreatedAt ?? ''}
        initialCode={rerunDraft?.code ?? ''}
        onConfirm={(code) => {
          if (!rerunDraft) return;
          chat.rerunCode(rerunDraft.sourceSessionId, rerunDraft.sourceTitle, code);
          setRerunDraft(null);
        }}
        onClose={() => setRerunDraft(null)}
      />
    </div>
  );
}
