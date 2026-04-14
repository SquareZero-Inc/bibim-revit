/** Shared type definitions for BIBIM v3 frontend */

export interface ChatMsg {
  id: string;
  text: string;
  isUser: boolean;
  type: MsgType;
  csharpCode?: string;
  guideContent?: string;
  createdAt: string;
  inputTokens?: number;
  outputTokens?: number;
  elapsedMs?: number;
  actionId?: string;
  taskId?: string;
  canUndo?: boolean;
  feedbackEnabled?: boolean;
  feedbackState?: 'up' | 'down' | null;
  // Separate feedback-request bubble state
  feedbackStep?: 'awaiting' | 'up_confirmed' | 'down_detail' | 'regen_offer';
  feedbackDetail?: string;
}

export type MsgType =
  | 'normal'
  | 'question'
  | 'code'
  | 'guide'
  | 'spec'
  | 'analysis'
  | 'system'
  | 'error'
  | 'feedback_request'
  | 'revit_warning';

export interface SessionInfo {
  id: string;
  title: string;
  createdAt: string;
  messageCount: number;
  parentSessionId?: string;
}

export interface SpecData {
  title: string;
  description: string;
  steps: string[];
  status: 'pending' | 'confirmed' | 'revised' | 'rejected';
}

export type TaskStage =
  | 'needs_details'
  | 'review'
  | 'working'
  | 'preview_ready'
  | 'completed'
  | 'cancelled';

export interface TaskDiagnostic {
  id: string;
  message: string;
  severity: string;
  line: number;
}

export interface TaskReview {
  safeCount: number;
  versionSpecificCount: number;
  deprecatedCount: number;
  affectedElementCount: number;
  previewSuccess: boolean;
  previewError?: string;
  executionSummary?: string;
  analyzerDiagnostics?: TaskDiagnostic[];
}

export interface QuestionItem {
  id: string;
  text: string;
  selectionType: 'single' | 'multi';
  options: string[];
  answer?: string;
  skipped?: boolean;
}

export interface TaskItem {
  taskId: string;
  title: string;
  summary: string;
  kind: 'read' | 'write';
  stage: TaskStage;
  requiresApply: boolean;
  wasApplied: boolean;
  autoOpen?: boolean;
  hasError?: boolean;
  steps: string[];
  questions: QuestionItem[];
  resultSummary?: string;
  createdAt: string;
  updatedAt: string;
  review?: TaskReview | null;
}

export interface TaskSummary {
  taskId: string;
  title: string;
  summary: string;
  kind: 'read' | 'write';
  stage: TaskStage;
  requiresApply: boolean;
  wasApplied: boolean;
  questionCount: number;
  updatedAt: string;
}

export interface ApiUsage {
  apiName: string;
  fullExpression: string;
  status: 'safe' | 'versionSpecific' | 'deprecated';
  note?: string;
  line: number;
}

export interface DryRunResult {
  success: boolean;
  affectedElementCount: number;
  errorMessage?: string;
  memoryDeltaBytes: number;
}

export interface ApiReport {
  apiUsages: ApiUsage[];
  safeCount: number;
  versionSpecificCount: number;
  deprecatedCount: number;
  dryRunSummary?: DryRunResult;
  analyzerDiagnostics?: AnalyzerDiag[];
}

export interface AnalyzerDiag {
  id: string;
  message: string;
  severity: 'info' | 'warning' | 'error';
  line: number;
}

export interface ProgressStep {
  label: string;
  status: 'pending' | 'active' | 'done' | 'error';
}

export interface ContextSuggestion {
  tag: string;
  label: string;
  description: string;
}

export interface CodeFolder {
  id: string;
  name: string;
  parentId?: string;
}

export interface CodeSnippetInfo {
  id: string;
  title: string;
  summary: string;
  revitVersion: string;
  taskKind: 'read' | 'write';
  createdAt: string;
  folderId?: string;
}

export interface CodeSnippetDetail extends CodeSnippetInfo {
  code: string;
  sourceSessionId?: string;
}

export interface UpdateInfo {
  updateAvailable: boolean;
  isMandatory: boolean;
  currentVersion: string;
  latestVersion: string;
  downloadUrl?: string;
  releaseNotes?: string;
}
