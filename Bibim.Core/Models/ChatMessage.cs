// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;

namespace Bibim.Core
{
    /// <summary>
    /// Message type for chat display.
    /// v3: Added CSharpCode (replaces PythonCode from v2).
    /// </summary>
    public enum MessageType
    {
        Normal,
        Question,
        Code,
        Guide,
        Spec,
        Analysis,
        System
    }

    /// <summary>
    /// Represents a single chat message for UI display and LLM history.
    /// </summary>
    public class ChatMessage
    {
        public string Text { get; set; }
        public bool IsUser { get; set; }
        public MessageType Type { get; set; }
        public string CSharpCode { get; set; }
        public string GuideContent { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Token usage for this specific message (captured at MessageStop).
        /// </summary>
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public long ElapsedMs { get; set; }

        public ChatMessage()
        {
            Type = MessageType.Normal;
            CreatedAt = DateTime.Now;
        }

        public string Timestamp => CreatedAt.ToString("HH:mm");
    }
}
