// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System.Threading;

namespace Bibim.Core
{
    /// <summary>
    /// Tracks LLM token usage per session for local diagnostics.
    /// </summary>
    public static class TokenTracker
    {
        private static int _sessionInputTokens;
        private static int _sessionOutputTokens;
        private static int _sessionCallCount;

        public static int SessionInputTokens => _sessionInputTokens;
        public static int SessionOutputTokens => _sessionOutputTokens;
        public static int SessionCallCount => _sessionCallCount;

        /// <summary>
        /// Record token usage and update session accumulators.
        /// </summary>
        public static void Track(string callType, string provider, string model,
            int inputTokens, int outputTokens, string requestId = null)
        {
            Interlocked.Add(ref _sessionInputTokens, inputTokens);
            Interlocked.Add(ref _sessionOutputTokens, outputTokens);
            Interlocked.Increment(ref _sessionCallCount);

            Logger.Log("TokenTracker",
                $"rid={requestId} type={callType} in={inputTokens} out={outputTokens} " +
                $"session_total_in={SessionInputTokens} session_total_out={SessionOutputTokens}");
        }

        /// <summary>
        /// Reset session accumulators (call on new chat session).
        /// </summary>
        public static void ResetSession()
        {
            Interlocked.Exchange(ref _sessionInputTokens, 0);
            Interlocked.Exchange(ref _sessionOutputTokens, 0);
            Interlocked.Exchange(ref _sessionCallCount, 0);
        }

        /// <summary>
        /// Restore session totals from stored messages (used when replaying a loaded session).
        /// </summary>
        public static void RestoreSessionUsage(int inputTokens, int outputTokens, bool incrementCallCount = false)
        {
            Interlocked.Add(ref _sessionInputTokens, inputTokens);
            Interlocked.Add(ref _sessionOutputTokens, outputTokens);
            if (incrementCallCount) Interlocked.Increment(ref _sessionCallCount);
        }
    }
}
