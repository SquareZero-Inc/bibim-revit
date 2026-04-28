import { useState, useCallback, useRef, useEffect } from 'react';
import { sendToBackend, onBackendMessage } from '../bridge';
import { useAppInfo } from './useAppInfo';
import { useCodeLibrary } from './useCodeLibrary';
import type {
  ChatMsg,
  ProgressStep,
  SessionInfo,
  TaskItem,
  TaskSummary,
} from '../types';

function uid(): string {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 7);
}

export function useChat() {
  // Domain-specific hooks (handle their own backend events)
  const appInfo = useAppInfo();
  const library = useCodeLibrary();

  // Core chat state
  const [messages, setMessages] = useState<ChatMsg[]>([]);
  const messagesRef = useRef<ChatMsg[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [isPendingRequest, setIsPendingRequest] = useState(false);
  const [steps, setSteps] = useState<ProgressStep[]>([]);
  const [currentTask, setCurrentTask] = useState<TaskItem | null>(null);
  const [taskList, setTaskList] = useState<TaskSummary[]>([]);
  const [sessions, setSessions] = useState<SessionInfo[]>([]);
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null);
  // Multi-provider key state (v1.1.0+)
  const [anthropicConfigured, setAnthropicConfigured] = useState(false);
  const [anthropicMasked, setAnthropicMasked] = useState('');
  const [anthropicSaveResult, setAnthropicSaveResult] = useState<'idle' | 'saved' | 'error'>('idle');
  const [openaiConfigured, setOpenaiConfigured] = useState(false);
  const [openaiMasked, setOpenaiMasked] = useState('');
  const [openaiSaveResult, setOpenaiSaveResult] = useState<'idle' | 'saved' | 'error'>('idle');
  const [geminiConfigured, setGeminiConfigured] = useState(false);
  const [geminiMasked, setGeminiMasked] = useState('');
  const [geminiKeySaveResult, setGeminiKeySaveResult] = useState<'idle' | 'saved' | 'error'>('idle');
  // Active model (id includes provider prefix, e.g. claude-*, gpt-*, gemini-*)
  const [claudeModel, setClaudeModel] = useState('claude-sonnet-4-6');
  // Aggregate "any active key configured" — kept under legacy name for back-compat with header chip.
  const [apiKeyConfigured, setApiKeyConfigured] = useState(false);
  const [pendingLoadSessionId, setPendingLoadSessionId] = useState<string | null>(null);
  const streamBuf = useRef('');
  const requestLockRef = useRef(false);
  const currentTaskRef = useRef<TaskItem | null>(null);
  const streamingRef = useRef(false);

  // Keep a sync ref so loadSession can check message count without capturing state
  messagesRef.current = messages;

  const startRequest = useCallback(() => {
    if (requestLockRef.current) return false;
    requestLockRef.current = true;
    setIsPendingRequest(true);
    return true;
  }, []);

  const finishRequest = useCallback(() => {
    requestLockRef.current = false;
    setIsPendingRequest(false);
  }, []);

  useEffect(() => {
    onBackendMessage('streaming_delta', (payload) => {
      const delta = payload as string;
      streamBuf.current += delta;
      setIsPendingRequest(false);
      setIsStreaming(true);
      streamingRef.current = true;
      setMessages((prev) => {
        const last = prev[prev.length - 1];
        if (last && !last.isUser && last.id === '__streaming__') {
          return [...prev.slice(0, -1), { ...last, text: streamBuf.current }];
        }
        return [...prev, {
          id: '__streaming__',
          text: streamBuf.current,
          isUser: false,
          type: 'normal',
          createdAt: new Date().toISOString(),
        }];
      });
    });

    onBackendMessage('streaming_end', (payload) => {
      const data = payload as {
        text: string;
        csharpCode?: string;
        type: string;
        isUser?: boolean;
        inputTokens: number;
        outputTokens: number;
        elapsedMs: number;
        actionId?: string;
        taskId?: string;
        canUndo?: boolean;
        feedbackEnabled?: boolean;
        feedbackState?: 'up' | 'down' | null;
        createdAt?: string;
      };
      setIsStreaming(false);
      streamingRef.current = false;
      finishRequest();
      streamBuf.current = '';
      setMessages((prev) => {
        const filtered = prev.filter((m) => m.id !== '__streaming__');
        return [...filtered, {
          id: uid(),
          text: data.text,
          isUser: data.isUser ?? false,
          type: (data.type as ChatMsg['type']) || 'normal',
          csharpCode: data.csharpCode,
          createdAt: data.createdAt ?? new Date().toISOString(),
          inputTokens: data.inputTokens,
          outputTokens: data.outputTokens,
          elapsedMs: data.elapsedMs,
          actionId: data.actionId,
          taskId: data.taskId,
          canUndo: data.canUndo,
          feedbackEnabled: data.feedbackEnabled,
          feedbackState: data.feedbackState ?? null,
        }];
      });
    });

    onBackendMessage('message_action_state', (payload) => {
      const data = payload as {
        actionId?: string;
        canUndo?: boolean;
        feedbackEnabled?: boolean;
        feedbackState?: 'up' | 'down' | null;
      };

      if (!data?.actionId) return;

      setMessages((prev) => prev.map((msg) => {
        if (msg.actionId !== data.actionId) return msg;

        const next = { ...msg };
        if (Object.prototype.hasOwnProperty.call(data, 'canUndo')) {
          next.canUndo = data.canUndo;
        }
        if (Object.prototype.hasOwnProperty.call(data, 'feedbackEnabled')) {
          next.feedbackEnabled = data.feedbackEnabled;
        }
        if (Object.prototype.hasOwnProperty.call(data, 'feedbackState')) {
          next.feedbackState = data.feedbackState ?? null;
        }
        return next;
      }));
    });

    onBackendMessage('progress', (payload) => {
      const next = payload as ProgressStep[];
      setIsPendingRequest(false);
      setSteps(next);
    });

    onBackendMessage('task_state', (payload) => {
      const next = (payload as TaskItem | null) ?? null;
      currentTaskRef.current = next;
      if (next && next.stage !== 'working') {
        finishRequest();
      }
      setCurrentTask(next);
    });

    onBackendMessage('task_list', (payload) => {
      setTaskList(payload as TaskSummary[]);
    });

    onBackendMessage('sessions', (payload) => {
      setSessions(payload as SessionInfo[]);
    });

    onBackendMessage('system_message', (payload) => {
      const text = payload as string;
      if (!streamingRef.current && currentTaskRef.current?.stage !== 'working') {
        finishRequest();
      }
      setMessages((prev) => [...prev, {
        id: uid(),
        text,
        isUser: false,
        type: 'system',
        createdAt: new Date().toISOString(),
      }]);
    });

    onBackendMessage('active_session', (payload) => {
      const data = payload as { sessionId?: string | null };
      setActiveSessionId(data?.sessionId ?? null);
    });

    onBackendMessage('session_deleted', (payload) => {
      const data = payload as { sessionId?: string; wasActive?: boolean };
      if (data?.sessionId) {
        setSessions((prev) => prev.filter((s) => s.id !== data.sessionId));
        setPendingLoadSessionId((prev) => (prev === data.sessionId ? null : prev));
        if (data.wasActive) {
          setMessages([]);
          setCurrentTask(null);
          currentTaskRef.current = null;
          setTaskList([]);
          setSteps([]);
          setActiveSessionId(null);
          streamBuf.current = '';
          setIsStreaming(false);
          streamingRef.current = false;
          finishRequest();
        }
      }
    });

    onBackendMessage('session_renamed', (payload) => {
      const data = payload as { sessionId?: string; title?: string };
      if (data?.sessionId) {
        setSessions((prev) =>
          prev.map((s) => s.id === data.sessionId ? { ...s, title: data.title ?? s.title } : s)
        );
      }
    });

    onBackendMessage('feedback_request', (payload) => {
      const data = payload as { actionId: string; taskId?: string };
      if (!data?.actionId) return;
      setMessages((prev) => [...prev, {
        id: uid(),
        text: '',
        isUser: false,
        type: 'feedback_request',
        actionId: data.actionId,
        taskId: data.taskId,
        feedbackStep: 'awaiting',
        createdAt: new Date().toISOString(),
      }]);
    });

    onBackendMessage('feedback_update', (payload) => {
      const data = payload as {
        actionId: string;
        step: 'up_confirmed' | 'down_detail' | 'regen_offer';
        detail?: string;
      };
      if (!data?.actionId) return;
      setMessages((prev) => prev.map((msg) => {
        if (msg.type !== 'feedback_request' || msg.actionId !== data.actionId) return msg;
        return {
          ...msg,
          feedbackStep: data.step,
          feedbackDetail: data.detail ?? msg.feedbackDetail,
        };
      }));
    });

    onBackendMessage('revit_warning', (payload) => {
      const data = payload as { message: string; warnings: string[]; taskId?: string };
      if (!data?.message) return;
      setMessages((prev) => [...prev, {
        id: uid(),
        text: data.message,
        isUser: false,
        type: 'revit_warning',
        taskId: data.taskId,
        createdAt: new Date().toISOString(),
      }]);
    });

    onBackendMessage('api_key_status', (payload) => {
      const data = payload as {
        // v1.1.0+ canonical fields
        configured?: boolean;
        activeProvider?: string;
        activeModel?: string;
        anthropic?: { configured?: boolean; maskedKey?: string };
        openai?:    { configured?: boolean; maskedKey?: string };
        gemini?:    { configured?: boolean; maskedKey?: string };
        // Legacy fields kept for backward compat with older backends
        maskedKey?: string;
        claudeModel?: string;
        geminiConfigured?: boolean;
        geminiMaskedKey?: string;
      };

      // Active model = primary signal for the header chip + selector highlight
      const activeModel = data?.activeModel ?? data?.claudeModel ?? 'claude-sonnet-4-6';
      setClaudeModel(activeModel);
      setApiKeyConfigured(data?.configured ?? false);

      // Per-provider sections
      setAnthropicConfigured(data?.anthropic?.configured ?? Boolean(data?.maskedKey));
      setAnthropicMasked(data?.anthropic?.maskedKey ?? data?.maskedKey ?? '');
      setOpenaiConfigured(data?.openai?.configured ?? false);
      setOpenaiMasked(data?.openai?.maskedKey ?? '');
      setGeminiConfigured(data?.gemini?.configured ?? data?.geminiConfigured ?? false);
      setGeminiMasked(data?.gemini?.maskedKey ?? data?.geminiMaskedKey ?? '');
    });

    onBackendMessage('api_key_save_result', (payload) => {
      const data = payload as { success?: boolean; provider?: string };
      const status: 'saved' | 'error' = data?.success ? 'saved' : 'error';
      // v1.1.0+ payloads include provider; route to the matching state
      switch (data?.provider) {
        case 'anthropic': setAnthropicSaveResult(status); setTimeout(() => setAnthropicSaveResult('idle'), 3000); break;
        case 'openai':    setOpenaiSaveResult(status);    setTimeout(() => setOpenaiSaveResult('idle'), 3000);    break;
        case 'gemini':    setGeminiKeySaveResult(status); setTimeout(() => setGeminiKeySaveResult('idle'), 3000); break;
        default:
          // Legacy backend: assume Anthropic (the only provider before v1.1.0)
          setAnthropicSaveResult(status);
          setTimeout(() => setAnthropicSaveResult('idle'), 3000);
          break;
      }
    });

    // Legacy event for older backend builds
    onBackendMessage('gemini_key_save_result', (payload) => {
      const data = payload as { success?: boolean };
      setGeminiKeySaveResult(data?.success ? 'saved' : 'error');
      setTimeout(() => setGeminiKeySaveResult('idle'), 3000);
    });

    // get_app_info → useAppInfo, get_code_library → useCodeLibrary
    sendToBackend('get_sessions', {});
    sendToBackend('get_task_state', {});
    sendToBackend('get_task_list', {});
    sendToBackend('get_api_key_status', {});
  }, []);

  const sendMessage = useCallback((text: string) => {
    if (!text.trim() || isStreaming || isPendingRequest) return;
    if (!startRequest()) return;

    const userMsg: ChatMsg = {
      id: uid(),
      text,
      isUser: true,
      type: 'normal',
      createdAt: new Date().toISOString(),
    };
    setMessages((prev) => [...prev, userMsg]);

    streamBuf.current = '';

    sendToBackend('user_message', { text });
  }, [isStreaming, isPendingRequest, startRequest]);

  const confirmTask = useCallback(() => {
    if (isStreaming || isPendingRequest || !startRequest()) return;
    sendToBackend('task_action', { action: 'confirm' });
  }, [isPendingRequest, isStreaming, startRequest]);

  const cancelTask = useCallback(() => {
    sendToBackend('task_action', { action: 'cancel' });
  }, []);

  const undoLastApply = useCallback((actionId: string, taskId?: string) => {
    if (isStreaming || isPendingRequest || !startRequest()) return;
    sendToBackend('undo_last_apply', { actionId, taskId });
  }, [isPendingRequest, isStreaming, startRequest]);

  const sendExecutionFeedback = useCallback((actionId: string, vote: 'up' | 'down', taskId?: string) => {
    sendToBackend('task_feedback', { actionId, taskId, vote });
  }, []);

  const executeCode = useCallback((mode: 'dryrun' | 'commit') => {
    if (isStreaming || isPendingRequest || !startRequest()) return;
    sendToBackend('execute', { mode });
  }, [isPendingRequest, isStreaming, startRequest]);

  const doLoadSession = useCallback((sessionId: string) => {
    setMessages([]);
    setCurrentTask(null);
    currentTaskRef.current = null;
    setTaskList([]);
    setSteps([]);
    setIsStreaming(false);
    streamingRef.current = false;
    finishRequest();
    streamBuf.current = '';
    setPendingLoadSessionId(null);
    setActiveSessionId(sessionId);
    sendToBackend('load_session', { sessionId });
  }, [finishRequest]);

  const loadSession = useCallback((sessionId: string) => {
    // messagesRef.current is always current (assigned during render above)
    if (messagesRef.current.length > 0) {
      setPendingLoadSessionId(sessionId);
    } else {
      doLoadSession(sessionId);
    }
  }, [doLoadSession]);

  const confirmLoadSession = useCallback(() => {
    if (pendingLoadSessionId) doLoadSession(pendingLoadSessionId);
  }, [pendingLoadSessionId, doLoadSession]);

  const cancelLoadSession = useCallback(() => {
    setPendingLoadSessionId(null);
  }, []);

  const newSession = useCallback(() => {
    setMessages([]);
    setActiveSessionId(null);
    setCurrentTask(null);
    currentTaskRef.current = null;
    setTaskList([]);
    setSteps([]);
    setIsStreaming(false);
    streamingRef.current = false;
    finishRequest();
    streamBuf.current = '';
    sendToBackend('new_session', {});
  }, [finishRequest]);

  const cancelStreaming = useCallback(() => {
    sendToBackend('cancel', {});
    setIsStreaming(false);
    streamingRef.current = false;
    finishRequest();
  }, [finishRequest]);

  const rerunCode = useCallback((sourceSessionId: string, sourceTitle: string, code: string) => {
    if (!code.trim()) return;
    if (isStreaming || isPendingRequest || !startRequest()) return;

    setMessages([]);
    setCurrentTask(null);
    currentTaskRef.current = null;
    setTaskList([]);
    setSteps([]);
    streamBuf.current = '';
    streamingRef.current = false;
    sendToBackend('rerun_code', { sourceSessionId, sourceTitle, code });
  }, [isPendingRequest, isStreaming, startRequest]);

  const deleteSession = useCallback((sessionId: string) => {
    sendToBackend('delete_session', { sessionId });
  }, []);

  const renameSession = useCallback((sessionId: string, title: string) => {
    sendToBackend('rename_session', { sessionId, title });
  }, []);

  const editCodeFromLibrary = useCallback((sourceTitle: string, code: string) => {
    if (!code.trim()) return;

    setMessages([]);
    setCurrentTask(null);
    currentTaskRef.current = null;
    setTaskList([]);
    setSteps([]);
    streamBuf.current = '';
    streamingRef.current = false;
    sendToBackend('edit_code', { sourceTitle, code });
  }, []);

  const submitQuestionAnswers = useCallback((answers: { id: string; answer: string; skipped: boolean }[]) => {
    sendToBackend('question_answers', { answers });
  }, []);

  const sendFeedbackDetail = useCallback((actionId: string, taskId: string | undefined, detail: string) => {
    sendToBackend('task_feedback_detail', { actionId, taskId, detail });
  }, []);

  const sendRegenerateWithFeedback = useCallback((actionId: string, taskId: string | undefined, detail: string) => {
    if (isStreaming || isPendingRequest || !startRequest()) return;
    sendToBackend('regenerate_with_feedback', { actionId, taskId, detail });
  }, [isStreaming, isPendingRequest, startRequest]);

  const sendWarningResponse = useCallback((
    choice: 'yes' | 'no' | 'add',
    taskId: string | undefined,
    text?: string,
  ) => {
    if (choice !== 'no' && (!isStreaming && !isPendingRequest)) startRequest();
    sendToBackend('warning_response', { choice, taskId, text: text ?? '' });
  }, [isStreaming, isPendingRequest, startRequest]);

  // v1.1.0+ provider-specific savers. Bridge handler names match backend.
  const saveAnthropicApiKey = useCallback((apiKey: string) => {
    sendToBackend('save_api_key', { apiKey });
  }, []);

  const saveOpenAiApiKey = useCallback((apiKey: string) => {
    sendToBackend('save_openai_api_key', { apiKey });
  }, []);

  const saveGeminiApiKey = useCallback((apiKey: string) => {
    sendToBackend('save_gemini_api_key', { apiKey });
  }, []);

  const saveModel = useCallback((modelId: string) => {
    sendToBackend('save_model', { modelId });
  }, []);

  // Back-compat aliases (older parts of the UI may still call saveApiKey)
  const saveApiKey = saveAnthropicApiKey;

  return {
    // Core chat
    messages,
    isStreaming,
    isBusy: isPendingRequest || isStreaming || steps.length > 0,
    steps,
    currentTask,
    taskList,
    sessions,
    activeSessionId,
    pendingLoadSessionId,
    // Actions
    sendMessage,
    confirmTask,
    cancelTask,
    undoLastApply,
    sendExecutionFeedback,
    executeCode,
    loadSession,
    newSession,
    rerunCode,
    cancelStreaming,
    editCodeFromLibrary,
    submitQuestionAnswers,
    sendFeedbackDetail,
    sendRegenerateWithFeedback,
    sendWarningResponse,
    confirmLoadSession,
    cancelLoadSession,
    deleteSession,
    renameSession,
    // BYOK — multi-provider (v1.1.0+)
    anthropicConfigured,
    anthropicMasked,
    anthropicSaveResult,
    saveAnthropicApiKey,
    openaiConfigured,
    openaiMasked,
    openaiSaveResult,
    saveOpenAiApiKey,
    geminiConfigured,
    geminiMasked,
    geminiKeySaveResult,
    saveGeminiApiKey,
    claudeModel,
    saveModel,
    // Legacy aliases — older UI components may still consume these.
    apiKeyConfigured,
    apiKeyMasked: anthropicMasked,
    apiKeySaveResult: anthropicSaveResult,
    saveApiKey,
    // Delegated to sub-hooks (spread for convenience)
    ...appInfo,
    ...library,
  };
}
