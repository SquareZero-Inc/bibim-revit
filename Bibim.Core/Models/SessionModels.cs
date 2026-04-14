// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
#if NET48
using Newtonsoft.Json;
#else
using System.Text.Json.Serialization;
#endif

namespace Bibim.Core
{
    /// <summary>
    /// Chat session containing messages. v3: C# code instead of Python.
    /// </summary>
    public class ChatSession
    {
#if NET48
        [JsonProperty("sessionId")]
#else
        [JsonPropertyName("sessionId")]
#endif
        public string SessionId { get; set; }

#if NET48
        [JsonProperty("title")]
#else
        [JsonPropertyName("title")]
#endif
        public string Title { get; set; }

#if NET48
        [JsonProperty("revitVersion")]
#else
        [JsonPropertyName("revitVersion")]
#endif
        public string RevitVersion { get; set; }

#if NET48
        [JsonProperty("createdAt")]
#else
        [JsonPropertyName("createdAt")]
#endif
        public DateTime CreatedAt { get; set; }

#if NET48
        [JsonProperty("updatedAt")]
#else
        [JsonPropertyName("updatedAt")]
#endif
        public DateTime UpdatedAt { get; set; }

#if NET48
        [JsonProperty("messages")]
#else
        [JsonPropertyName("messages")]
#endif
        public List<SessionMessage> Messages { get; set; }

#if NET48
        [JsonProperty("contextData")]
#else
        [JsonPropertyName("contextData")]
#endif
        public string ContextData { get; set; }

#if NET48
        [JsonProperty("parentSessionId")]
#else
        [JsonPropertyName("parentSessionId")]
#endif
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
#if NET48
        [JsonProperty("id")]
#else
        [JsonPropertyName("id")]
#endif
        public string Id { get; set; }

#if NET48
        [JsonProperty("role")]
#else
        [JsonPropertyName("role")]
#endif
        public string Role { get; set; }

#if NET48
        [JsonProperty("contentType")]
#else
        [JsonPropertyName("contentType")]
#endif
        public string ContentType { get; set; }

#if NET48
        [JsonProperty("content")]
#else
        [JsonPropertyName("content")]
#endif
        public string Content { get; set; }

#if NET48
        [JsonProperty("csharpCode")]
#else
        [JsonPropertyName("csharpCode")]
#endif
        public string CSharpCode { get; set; }

        /// <summary>
        /// Legacy Python code from v2 sessions (read-only compat).
        /// </summary>
#if NET48
        [JsonProperty("pythonCode")]
#else
        [JsonPropertyName("pythonCode")]
#endif
        public string PythonCode { get; set; }

        /// <summary>
        /// Unified code accessor: prefers CSharpCode, falls back to PythonCode.
        /// Design doc §5 — backward compatibility.
        /// </summary>
        public string Code => CSharpCode ?? PythonCode;

#if NET48
        [JsonProperty("sequenceOrder")]
#else
        [JsonPropertyName("sequenceOrder")]
#endif
        public int SequenceOrder { get; set; }

#if NET48
        [JsonProperty("inputTokens")]
#else
        [JsonPropertyName("inputTokens")]
#endif
        public int InputTokens { get; set; }

#if NET48
        [JsonProperty("outputTokens")]
#else
        [JsonPropertyName("outputTokens")]
#endif
        public int OutputTokens { get; set; }

#if NET48
        [JsonProperty("createdAt")]
#else
        [JsonPropertyName("createdAt")]
#endif
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
#if NET48
        [JsonProperty("sessions")]
#else
        [JsonPropertyName("sessions")]
#endif
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
#if NET48
        [JsonProperty("userMessage")]
#else
        [JsonPropertyName("userMessage")]
#endif
        public string UserMessage { get; set; }

#if NET48
        [JsonProperty("assistantResponse")]
#else
        [JsonPropertyName("assistantResponse")]
#endif
        public string AssistantResponse { get; set; }

#if NET48
        [JsonProperty("isError")]
#else
        [JsonPropertyName("isError")]
#endif
        public bool IsError { get; set; }

#if NET48
        [JsonProperty("timestamp")]
#else
        [JsonPropertyName("timestamp")]
#endif
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    public class WorkflowState
    {
#if NET48
        [JsonProperty("phase")]
#else
        [JsonPropertyName("phase")]
#endif
        public string Phase { get; set; }

#if NET48
        [JsonProperty("documentPath")]
#else
        [JsonPropertyName("documentPath")]
#endif
        public string DocumentPath { get; set; }

#if NET48
        [JsonProperty("pendingAction")]
#else
        [JsonPropertyName("pendingAction")]
#endif
        public string PendingAction { get; set; }

#if NET48
        [JsonProperty("metadata")]
#else
        [JsonPropertyName("metadata")]
#endif
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class RetryContext
    {
#if NET48
        [JsonProperty("originalUserMessage")]
#else
        [JsonPropertyName("originalUserMessage")]
#endif
        public string OriginalUserMessage { get; set; }

#if NET48
        [JsonProperty("conversationHistory")]
#else
        [JsonPropertyName("conversationHistory")]
#endif
        public List<ConversationTurn> ConversationHistory { get; set; } = new List<ConversationTurn>();

#if NET48
        [JsonProperty("workflowState")]
#else
        [JsonPropertyName("workflowState")]
#endif
        public WorkflowState WorkflowState { get; set; }

#if NET48
        [JsonProperty("failedAt")]
#else
        [JsonPropertyName("failedAt")]
#endif
        public DateTime FailedAt { get; set; } = DateTime.UtcNow;

#if NET48
        [JsonProperty("errorType")]
#else
        [JsonPropertyName("errorType")]
#endif
        public string ErrorType { get; set; }
    }

    public class SessionContext
    {
#if NET48
        [JsonProperty("sessionId")]
#else
        [JsonPropertyName("sessionId")]
#endif
        public string SessionId { get; set; }

#if NET48
        [JsonProperty("turns")]
#else
        [JsonPropertyName("turns")]
#endif
        public List<ConversationTurn> Turns { get; set; } = new List<ConversationTurn>();

#if NET48
        [JsonProperty("tasks")]
#else
        [JsonPropertyName("tasks")]
#endif
        public List<TaskState> Tasks { get; set; } = new List<TaskState>();

#if NET48
        [JsonProperty("activeTaskId")]
#else
        [JsonPropertyName("activeTaskId")]
#endif
        public string ActiveTaskId { get; set; }

#if NET48
        [JsonProperty("currentWorkflow")]
#else
        [JsonPropertyName("currentWorkflow")]
#endif
        public WorkflowState CurrentWorkflow { get; set; }

#if NET48
        [JsonProperty("pendingRetry")]
#else
        [JsonPropertyName("pendingRetry")]
#endif
        public RetryContext PendingRetry { get; set; }

#if NET48
        [JsonProperty("lastUpdated")]
#else
        [JsonPropertyName("lastUpdated")]
#endif
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

#if NET48
        [JsonProperty("consecutiveErrors")]
#else
        [JsonPropertyName("consecutiveErrors")]
#endif
        public int ConsecutiveErrors { get; set; }

#if NET48
        [JsonProperty("executionLog")]
#else
        [JsonPropertyName("executionLog")]
#endif
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
#if NET48
        [JsonProperty("timestamp")]
#else
        [JsonPropertyName("timestamp")]
#endif
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

#if NET48
        [JsonProperty("taskId")]
#else
        [JsonPropertyName("taskId")]
#endif
        public string TaskId { get; set; }

#if NET48
        [JsonProperty("taskTitle")]
#else
        [JsonPropertyName("taskTitle")]
#endif
        public string TaskTitle { get; set; }

#if NET48
        [JsonProperty("success")]
#else
        [JsonPropertyName("success")]
#endif
        public bool Success { get; set; }

#if NET48
        [JsonProperty("output")]
#else
        [JsonPropertyName("output")]
#endif
        public string Output { get; set; }

#if NET48
        [JsonProperty("errorMessage")]
#else
        [JsonPropertyName("errorMessage")]
#endif
        public string ErrorMessage { get; set; }

#if NET48
        [JsonProperty("isDryRun")]
#else
        [JsonPropertyName("isDryRun")]
#endif
        public bool IsDryRun { get; set; }
    }
}
