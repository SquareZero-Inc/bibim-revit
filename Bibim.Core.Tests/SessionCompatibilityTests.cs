// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Tests for v2 → v3 session data compatibility.
    /// Design doc §5 — User safe transition.
    /// Ensures existing user data (sessions, messages) is preserved.
    /// </summary>
    public class SessionCompatibilityTests
    {
        [Fact]
        public void ChatMessage_DefaultType_IsNormal()
        {
            var msg = new ChatMessage();
            Assert.Equal(MessageType.Normal, msg.Type);
        }

        [Fact]
        public void ChatMessage_CSharpCode_IsOptional()
        {
            var msg = new ChatMessage { Text = "Hello", IsUser = true };
            Assert.Null(msg.CSharpCode);
        }

        [Fact]
        public void ChatMessage_TokenFields_DefaultToZero()
        {
            var msg = new ChatMessage();
            Assert.Equal(0, msg.InputTokens);
            Assert.Equal(0, msg.OutputTokens);
            Assert.Equal(0, msg.ElapsedMs);
        }

        [Fact]
        public void ChatMessage_Timestamp_FormatsCorrectly()
        {
            var msg = new ChatMessage();
            Assert.NotNull(msg.Timestamp);
            Assert.Contains(":", msg.Timestamp); // HH:mm format
        }

        [Fact]
        public void SessionMessage_V3_HasCSharpCodeField()
        {
            var msg = new SessionMessage
            {
                Content = "test",
                Role = "assistant",
                CSharpCode = "var x = 1;"
            };
            Assert.Equal("var x = 1;", msg.CSharpCode);
        }

        [Fact]
        public void SessionMessage_V2_PythonCode_StillReadable()
        {
            // v2 messages had PythonCode — v3 should still read them
            var msg = new SessionMessage
            {
                Content = "test",
                Role = "assistant",
                PythonCode = "import clr\nclr.AddReference('RevitAPI')"
            };
            Assert.NotNull(msg.PythonCode);
            // Code property should prefer CSharpCode, fallback to PythonCode
            Assert.Equal(msg.PythonCode, msg.Code);
        }

        [Fact]
        public void SessionMessage_V3_CodePrefersCSharp()
        {
            var msg = new SessionMessage
            {
                Content = "test",
                Role = "assistant",
                CSharpCode = "var x = 1;",
                PythonCode = "x = 1"
            };
            // CSharpCode takes priority
            Assert.Equal("var x = 1;", msg.Code);
        }

        [Fact]
        public void ChatSession_CanBeCreated()
        {
            var session = new ChatSession
            {
                SessionId = Guid.NewGuid().ToString(),
                Title = "Test Session",
                CreatedAt = DateTime.Now,
                Messages = new List<SessionMessage>()
            };

            Assert.NotNull(session.SessionId);
            Assert.Equal("Test Session", session.Title);
            Assert.Empty(session.Messages);
        }

    }
}
