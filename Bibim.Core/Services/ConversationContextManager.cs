// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;

namespace Bibim.Core
{
    /// <summary>
    /// Manages conversation context for error-resilient chat sessions.
    /// Ported from v2 with namespace change.
    /// </summary>
    public class ConversationContextManager
    {
        private SessionContext _currentSession;
        private readonly LocalSessionManager _localSessionManager;

        public ConversationContextManager(LocalSessionManager localSessionManager)
        {
            _localSessionManager = localSessionManager ?? throw new ArgumentNullException(nameof(localSessionManager));
        }

        public void StartNewSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID required.", nameof(sessionId));

            _currentSession = new SessionContext
            {
                SessionId = sessionId,
                Turns = new List<ConversationTurn>(),
                LastUpdated = DateTime.UtcNow
            };
        }

        public SessionContext RestoreSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw new ArgumentException("Session ID required.", nameof(sessionId));

            try
            {
                _currentSession = _localSessionManager.LoadSessionContext(sessionId);
                if (_currentSession == null)
                    StartNewSession(sessionId);
            }
            catch
            {
                StartNewSession(sessionId);
            }
            return _currentSession;
        }

        public void AddTurn(string userMessage, string assistantResponse = null, bool isError = false)
        {
            EnsureSession();
            _currentSession.Turns.Add(new ConversationTurn
            {
                UserMessage = userMessage,
                AssistantResponse = assistantResponse,
                IsError = isError,
                Timestamp = DateTime.UtcNow
            });
            _currentSession.LastUpdated = DateTime.UtcNow;

            if (isError)
                _currentSession.ConsecutiveErrors++;
            else if (assistantResponse != null)
                _currentSession.ConsecutiveErrors = 0;
        }

        public void UpdateWorkflowState(WorkflowState state)
        {
            EnsureSession();
            _currentSession.CurrentWorkflow = state;
            _currentSession.LastUpdated = DateTime.UtcNow;
        }

        public RetryContext CreateRetryContext(string userMessage, string errorType)
        {
            EnsureSession();
            var retry = new RetryContext
            {
                OriginalUserMessage = userMessage,
                ConversationHistory = new List<ConversationTurn>(_currentSession.Turns),
                WorkflowState = _currentSession.CurrentWorkflow,
                ErrorType = errorType
            };
            _currentSession.PendingRetry = retry;
            return retry;
        }

        public RetryContext GetPendingRetry() => _currentSession?.PendingRetry;

        public void ClearPendingRetry()
        {
            EnsureSession();
            _currentSession.PendingRetry = null;
        }

        public void SaveSession()
        {
            EnsureSession();
            _localSessionManager.SaveSessionContext(_currentSession);
        }

        public bool ShouldShowAlternativeGuidance() =>
            _currentSession != null && _currentSession.ConsecutiveErrors >= 3;

        public SessionContext GetCurrentSession()
        {
            EnsureSession();
            return _currentSession;
        }

        public int GetConsecutiveErrorCount() => _currentSession?.ConsecutiveErrors ?? 0;

        private void EnsureSession()
        {
            if (_currentSession == null)
                throw new InvalidOperationException("No active session.");
        }
    }
}
