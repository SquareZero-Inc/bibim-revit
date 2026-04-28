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
        private static int _sessionCachedInputTokens;
        private static int _sessionCacheCreationInputTokens;
        private static int _sessionCallCount;

        public static int SessionInputTokens => _sessionInputTokens;
        public static int SessionOutputTokens => _sessionOutputTokens;
        public static int SessionCachedInputTokens => _sessionCachedInputTokens;
        public static int SessionCacheCreationInputTokens => _sessionCacheCreationInputTokens;
        public static int SessionCallCount => _sessionCallCount;

        /// <summary>
        /// Cache hit ratio = cache_read / (input + cache_read). Returns 0 if no input recorded.
        /// </summary>
        public static double SessionCacheHitRatio
        {
            get
            {
                int total = _sessionInputTokens + _sessionCachedInputTokens;
                return total == 0 ? 0.0 : (double)_sessionCachedInputTokens / total;
            }
        }

        /// <summary>
        /// Record token usage and update session accumulators.
        /// </summary>
        public static void Track(string callType, string provider, string model,
            int inputTokens, int outputTokens, string requestId = null,
            int cachedInputTokens = 0, int cacheCreationInputTokens = 0)
        {
            Interlocked.Add(ref _sessionInputTokens, inputTokens);
            Interlocked.Add(ref _sessionOutputTokens, outputTokens);
            Interlocked.Add(ref _sessionCachedInputTokens, cachedInputTokens);
            Interlocked.Add(ref _sessionCacheCreationInputTokens, cacheCreationInputTokens);
            Interlocked.Increment(ref _sessionCallCount);

            Logger.Log("TokenTracker",
                $"rid={requestId} type={callType} in={inputTokens} out={outputTokens} " +
                $"cache_read={cachedInputTokens} cache_create={cacheCreationInputTokens} " +
                $"session_total_in={SessionInputTokens} session_total_out={SessionOutputTokens} " +
                $"session_cache_read={SessionCachedInputTokens} hit_ratio={SessionCacheHitRatio:P1}");
        }

        /// <summary>
        /// Reset session accumulators (call on new chat session).
        /// </summary>
        public static void ResetSession()
        {
            Interlocked.Exchange(ref _sessionInputTokens, 0);
            Interlocked.Exchange(ref _sessionOutputTokens, 0);
            Interlocked.Exchange(ref _sessionCachedInputTokens, 0);
            Interlocked.Exchange(ref _sessionCacheCreationInputTokens, 0);
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
