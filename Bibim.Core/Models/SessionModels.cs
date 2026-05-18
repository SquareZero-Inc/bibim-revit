// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Bibim.Core
{
    /// <summary>
    /// Chat session containing messages. v3: C# code instead of Python.
    /// </summary>
    public class ChatSession
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonProperty("messages")]
        public List<SessionMessage> Messages { get; set; }

        [JsonProperty("contextData")]
        public string ContextData { get; set; }

        [JsonProperty("parentSessionId")]
        public string ParentSessionId { get; set; }

        public ChatSession()
        {
            Messages = new List<SessionMessage>();
        }
    }

    /// <summary>
    /// Individual message in a session.
    /// </summary>
    public class SessionMessage
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("contentType")]
        public string ContentType { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("csharpCode")]
        public string CSharpCode { get; set; }

        /// <summary>
        /// Legacy Python code from v2 sessions (read-only compat).
        /// </summary>
        [JsonProperty("pythonCode")]
        public string PythonCode { get; set; }

        /// <summary>
        /// Unified code accessor: prefers CSharpCode, falls back to PythonCode.
        /// Design doc §5 — backward compatibility.
        /// </summary>
        public string Code => CSharpCode ?? PythonCode;

        [JsonProperty("sequenceOrder")]
        public int SequenceOrder { get; set; }

        [JsonProperty("inputTokens")]
        public int InputTokens { get; set; }

        [JsonProperty("outputTokens")]
        public int OutputTokens { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        public SessionMessage()
        {
            Id = Guid.NewGuid().ToString();
            CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Root container for local JSON session storage.
    /// </summary>
    public class SessionStorage
    {
        [JsonProperty("sessions")]
        public List<ChatSession> Sessions { get; set; }

        public SessionStorage()
        {
            Sessions = new List<ChatSession>();
        }
    }

    // ============================================================
    // Error-Resilient Context Models (ported from v2)
    // ============================================================

    public class ConversationTurn
    {
        [JsonProperty("userMessage")]
        public string UserMessage { get; set; }

        [JsonProperty("assistantResponse")]
        public string AssistantResponse { get; set; }

        [JsonProperty("isError")]
        public bool IsError { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class WorkflowState
    {
        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("documentPath")]
        public string DocumentPath { get; set; }

        [JsonProperty("pendingAction")]
        public string PendingAction { get; set; }

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class RetryContext
    {
        [JsonProperty("originalUserMessage")]
        public string OriginalUserMessage { get; set; }

        [JsonProperty("conversationHistory")]
        public List<ConversationTurn> ConversationHistory { get; set; } = new List<ConversationTurn>();

        [JsonProperty("workflowState")]
        public WorkflowState WorkflowState { get; set; }

        [JsonProperty("failedAt")]
        public DateTime FailedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("errorType")]
        public string ErrorType { get; set; }
    }

    public class SessionContext
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("turns")]
        public List<ConversationTurn> Turns { get; set; } = new List<ConversationTurn>();

        [JsonProperty("tasks")]
        public List<TaskState> Tasks { get; set; } = new List<TaskState>();

        [JsonProperty("activeTaskId")]
        public string ActiveTaskId { get; set; }

        [JsonProperty("currentWorkflow")]
        public WorkflowState CurrentWorkflow { get; set; }

        [JsonProperty("pendingRetry")]
        public RetryContext PendingRetry { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [JsonProperty("consecutiveErrors")]
        public int ConsecutiveErrors { get; set; }

        [JsonProperty("executionLog")]
        public List<ExecutionLogEntry> ExecutionLog { get; set; } = new List<ExecutionLogEntry>();

        /// <summary>
        /// Append an execution result to the ring buffer (max 10 entries).
        /// </summary>
        public void RecordExecution(ExecutionLogEntry entry)
        {
            if (entry == null) return;
            ExecutionLog.Add(entry);
            if (ExecutionLog.Count > 10)
                ExecutionLog.RemoveAt(0);
        }
    }

    /// <summary>
    /// A single code execution result stored in the session for context continuity.
    /// Ring buffer of up to 10 entries per session.
    /// </summary>
    public class ExecutionLogEntry
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonProperty("taskId")]
        public string TaskId { get; set; }

        [JsonProperty("taskTitle")]
        public string TaskTitle { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("output")]
        public string Output { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("isDryRun")]
        public bool IsDryRun { get; set; }
    }
}
