// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Local JSON storage for chat sessions.
    /// Stores at %APPDATA%/BIBIM/history/sessions_v3.json
    /// Ported from v2 with namespace change + C# code support.
    /// </summary>
    public class LocalSessionManager
    {
        private readonly string _storageFolderPath;
        private readonly string _sessionsFilePath;
        private SessionStorage _cache;
        private readonly object _lock = new object();

        public LocalSessionManager(string customStoragePath = null)
        {
            _storageFolderPath = customStoragePath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BIBIM", "history");
            _sessionsFilePath = Path.Combine(_storageFolderPath, "sessions_v3.json");
        }

        public void EnsureStorageFolder()
        {
            if (!Directory.Exists(_storageFolderPath))
                Directory.CreateDirectory(_storageFolderPath);
        }

        public ChatSession CreateSession()
        {
            var now = DateTime.UtcNow;
            return new ChatSession
            {
                SessionId = Guid.NewGuid().ToString(),
                Title = "",
                RevitVersion = ConfigService.GetEffectiveRevitVersion(),
                CreatedAt = now,
                UpdatedAt = now,
                Messages = new List<SessionMessage>()
            };
        }

        public void SaveSession(ChatSession session)
        {
            if (session == null) return;

            lock (_lock)
            {
                EnsureStorageFolder();
                var storage = LoadStorage();
                int idx = storage.Sessions.FindIndex(s => s.SessionId == session.SessionId);
                if (idx >= 0)
                    storage.Sessions[idx] = session;
                else
                    storage.Sessions.Add(session);
                WriteStorage(storage);
            }
        }

        public ChatSession LoadSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (_lock)
            {
                return LoadStorage().Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            }
        }

        public List<ChatSession> GetAllSessions()
        {
            lock (_lock)
            {
                return LoadStorage().Sessions
                    .OrderByDescending(s => s.UpdatedAt)
                    .ToList();
            }
        }

        public void AddMessage(string sessionId, string role, string contentType,
            string content, string csharpCode = null, int inputTokens = 0, int outputTokens = 0)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(content)) return;

            lock (_lock)
            {
                var storage = LoadStorage();
                var session = storage.Sessions.FirstOrDefault(s => s.SessionId == sessionId);

                if (session == null)
                {
                    session = new ChatSession
                    {
                        SessionId = sessionId,
                        Title = role == "user" ? GenerateTitle(content) : "",
                        RevitVersion = ConfigService.GetEffectiveRevitVersion(),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        Messages = new List<SessionMessage>()
                    };
                    storage.Sessions.Add(session);
                }

                session.Messages.Add(new SessionMessage
                {
                    Role = role,
                    ContentType = contentType,
                    Content = content,
                    CSharpCode = csharpCode,
                    SequenceOrder = session.Messages.Count + 1,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens
                });

                if (string.IsNullOrEmpty(session.Title) && role == "user")
                    session.Title = GenerateTitle(content);

                session.UpdatedAt = DateTime.UtcNow;
                WriteStorage(storage);
            }
        }

        public string GenerateTitle(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return "";
            var cleaned = prompt.Replace("\r\n", " ").Replace("\n", " ").Trim();
            return cleaned.Length > 50 ? cleaned.Substring(0, 47) + "..." : cleaned;
        }

        public void DeleteSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (_lock)
            {
                var storage = LoadStorage();
                storage.Sessions.RemoveAll(s => s.SessionId == sessionId);
                WriteStorage(storage);
            }
        }

        public void RenameSession(string sessionId, string newTitle)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            lock (_lock)
            {
                var storage = LoadStorage();
                var session = storage.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session == null) return;
                session.Title = newTitle ?? "";
                session.UpdatedAt = DateTime.UtcNow;
                WriteStorage(storage);
            }
        }

        public void SaveSessionContext(SessionContext context)
        {
            if (context == null) return;
            lock (_lock)
            {
                EnsureStorageFolder();
                var storage = LoadStorage();
                var session = storage.Sessions.FirstOrDefault(s => s.SessionId == context.SessionId);
                if (session != null)
                {
                    session.ContextData = JsonHelper.Serialize(context);
                    session.UpdatedAt = context.LastUpdated;
                }
                else
                {
                    storage.Sessions.Add(new ChatSession
                    {
                        SessionId = context.SessionId,
                        Title = "",
                        RevitVersion = ConfigService.GetEffectiveRevitVersion(),
                        CreatedAt = context.LastUpdated,
                        UpdatedAt = context.LastUpdated,
                        Messages = new List<SessionMessage>(),
                        ContextData = JsonHelper.Serialize(context)
                    });
                }
                WriteStorage(storage);
            }
        }

        public SessionContext LoadSessionContext(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return NewEmptyContext(sessionId);
            lock (_lock)
            {
                var session = LoadStorage().Sessions.FirstOrDefault(s => s.SessionId == sessionId);
                if (session != null && !string.IsNullOrEmpty(session.ContextData))
                {
                    try { return JsonHelper.Deserialize<SessionContext>(session.ContextData); }
                    catch { }
                }
            }
            return NewEmptyContext(sessionId);
        }

        private SessionContext NewEmptyContext(string sessionId)
        {
            return new SessionContext
            {
                SessionId = sessionId,
                Turns = new List<ConversationTurn>(),
                LastUpdated = DateTime.UtcNow
            };
        }

        private SessionStorage LoadStorage()
        {
            if (_cache != null) return _cache;
            if (!File.Exists(_sessionsFilePath))
            {
                _cache = new SessionStorage();
                return _cache;
            }
            try
            {
                _cache = JsonHelper.Deserialize<SessionStorage>(File.ReadAllText(_sessionsFilePath))
                    ?? new SessionStorage();
            }
            catch { _cache = new SessionStorage(); }
            return _cache;
        }

        private void WriteStorage(SessionStorage storage)
        {
            try
            {
                EnsureStorageFolder();
                File.WriteAllText(_sessionsFilePath, JsonHelper.Serialize(storage, indented: true));
                _cache = storage;
            }
            catch (Exception ex)
            {
                Logger.LogError("LocalSessionManager.WriteStorage", ex);
            }
        }
    }
}
