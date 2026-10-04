// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
namespace Bibim.Core
{
    /// <summary>
    /// Where the chat-history window starts. A plain "last N messages" window shifts by one
    /// message every turn, so the first message of every request differs from the previous
    /// request's and the whole cached conversation prefix is re-billed. Moving the start in
    /// steps keeps the prefix byte-identical for <c>step</c> turns: the window grows from
    /// <c>minKeep</c> to <c>minKeep + step - 1</c> messages, then jumps back to <c>minKeep</c>.
    /// </summary>
    public static class HistoryWindowPolicy
    {
        public static int StartIndex(int count, int minKeep, int step)
        {
            if (count <= minKeep || minKeep < 0) return 0;
            if (step < 1) step = 1;
            return ((count - minKeep) / step) * step;
        }
    }
}
