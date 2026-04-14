// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.DB;

namespace Bibim.Core
{
    internal static class DocumentChangeTracker
    {
        private static long _globalSequence;
        private static readonly ConcurrentDictionary<string, long> _documentSequences =
            new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static long RegisterChange(Document doc)
        {
            long sequence = Interlocked.Increment(ref _globalSequence);
            foreach (var key in GetDocumentKeys(doc))
            {
                _documentSequences[key] = sequence;
            }

            return sequence;
        }

        public static long GetCurrentSequence(string documentTitle, string documentPath)
        {
            long current = 0;

            if (!string.IsNullOrWhiteSpace(documentPath) &&
                _documentSequences.TryGetValue(BuildPathKey(documentPath), out var pathSequence))
            {
                current = Math.Max(current, pathSequence);
            }

            if (!string.IsNullOrWhiteSpace(documentTitle) &&
                _documentSequences.TryGetValue(BuildTitleKey(documentTitle), out var titleSequence))
            {
                current = Math.Max(current, titleSequence);
            }

            return current;
        }

        private static IEnumerable<string> GetDocumentKeys(Document doc)
        {
            if (doc == null)
                yield break;

            if (!string.IsNullOrWhiteSpace(doc.PathName))
                yield return BuildPathKey(doc.PathName);

            if (!string.IsNullOrWhiteSpace(doc.Title))
                yield return BuildTitleKey(doc.Title);
        }

        private static string BuildPathKey(string path)
        {
            return $"path:{path}";
        }

        private static string BuildTitleKey(string title)
        {
            return $"title:{title}";
        }
    }
}
