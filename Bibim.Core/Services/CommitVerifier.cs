// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bibim.Core
{
    /// <summary>Evidence gathered after a commit (see <see cref="CommitVerifier"/>).</summary>
    public sealed class CommitVerification
    {
        public List<string> Lines { get; } = new List<string>();

        /// <summary>True when the evidence contradicts a clean "done".</summary>
        public bool HasWarning { get; set; }

        public int FilesReported { get; set; }
        public int FilesFound { get; set; }

        public string ToText() => string.Join("\n", Lines);
    }

    /// <summary>
    /// Post-commit verification (UPDATE_PLAN M7): the completion message is built from
    /// MEASURED facts instead of trusting the generated code's own "done" string.
    ///   • measured element delta (added / modified / deleted) vs. the preview's prediction
    ///   • a model-changing task that changed nothing is flagged
    ///   • file paths the code reported are checked on disk
    /// Pure (file existence is injected) so it is unit-tested directly.
    /// </summary>
    public static class CommitVerifier
    {
        private static readonly Regex FilePathRegex = new Regex(
            @"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]+?\.(?:pdf|dwg|dxf|csv|xlsx|xlsm|xls|ifc|png|jpe?g|bmp|tiff?|txt|json|xml|html?|nwc|rvt|rfa|docx)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <param name="category">Planner <see cref="TaskCategories"/> value (may be null).</param>
        /// <param name="previewAffected">Preview's affected count, or -1 when unknown.</param>
        public static CommitVerification Verify(
            string category,
            int previewAffected,
            int commitAffected,
            int added, int modified, int deleted,
            string output,
            IEnumerable<string> logs,
            Func<string, bool> fileExists)
        {
            var v = new CommitVerification();
            bool en = AppLanguage.IsEnglish;

            string breakdown = (added + modified + deleted) > 0
                ? (en ? $" (added {added} · modified {modified} · deleted {deleted})"
                      : $" (추가 {added} · 수정 {modified} · 삭제 {deleted})")
                : "";
            string previewPart = previewAffected >= 0
                ? (en ? $", preview predicted {previewAffected}" : $", 미리보기 예측 {previewAffected}개")
                : "";
            v.Lines.Add(en
                ? $"Measured change: {commitAffected} element(s){breakdown}{previewPart}"
                : $"실측 변경: 요소 {commitAffected}개{breakdown}{previewPart}");

            bool expectsChange = TaskCategories.IsKnown(category) && TaskCategories.ExpectsModelChange(category);
            if (commitAffected == 0 && (expectsChange || previewAffected > 0))
            {
                v.HasWarning = true;
                v.Lines.Add(en
                    ? "⚠ No model elements changed — the requested change may not have been applied. Check the model before continuing."
                    : "⚠ 변경된 모델 요소가 없습니다 — 요청한 변경이 실제로 반영되지 않았을 수 있습니다. 모델을 확인해 주세요.");
            }
            else if (previewAffected > 0 && commitAffected > 0 && commitAffected * 2 < previewAffected)
            {
                v.HasWarning = true;
                v.Lines.Add(en
                    ? $"⚠ Fewer elements changed than the preview predicted ({commitAffected} of {previewAffected})."
                    : $"⚠ 미리보기 예측보다 적게 변경되었습니다 ({previewAffected}개 중 {commitAffected}개).");
            }

            var paths = ExtractFilePaths(output, logs);
            if (paths.Count > 0)
            {
                var missing = paths.Where(p => !SafeExists(fileExists, p)).ToList();
                v.FilesReported = paths.Count;
                v.FilesFound = paths.Count - missing.Count;
                if (missing.Count == 0)
                {
                    v.Lines.Add(en
                        ? $"Files verified on disk: {paths.Count}/{paths.Count}"
                        : $"파일 확인: {paths.Count}개 모두 디스크에 존재");
                }
                else
                {
                    v.HasWarning = true;
                    string names = string.Join(", ", missing.Take(3).Select(SafeFileName));
                    if (missing.Count > 3) names += ", …";
                    v.Lines.Add(en
                        ? $"⚠ {missing.Count} of {paths.Count} reported file(s) not found on disk: {names}"
                        : $"⚠ 보고된 파일 {paths.Count}개 중 {missing.Count}개가 디스크에 없습니다: {names}");
                }
            }
            else if (category == TaskCategories.Export)
            {
                v.Lines.Add(en
                    ? "No output file path was reported, so the files could not be verified."
                    : "출력 파일 경로가 보고되지 않아 파일 생성 여부는 확인하지 못했습니다.");
            }

            return v;
        }

        /// <summary>
        /// Absolute Windows file paths mentioned in the output / execution log. Dry-run
        /// artifacts (<c>_BIBIM_TEST</c>) are ignored. Capped at 50.
        /// </summary>
        public static List<string> ExtractFilePaths(string output, IEnumerable<string> logs)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> sources = new[] { output ?? "" }.Concat(logs ?? Enumerable.Empty<string>());
            foreach (var text in sources)
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match m in FilePathRegex.Matches(text))
                {
                    string path = m.Value.Trim();
                    if (path.IndexOf("_BIBIM_TEST", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (seen.Add(path)) found.Add(path);
                    if (found.Count >= 50) return found;
                }
            }
            return found;
        }

        private static bool SafeExists(Func<string, bool> fileExists, string path)
        {
            try { return (fileExists ?? File.Exists)(path); }
            catch { return false; }
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }
    }
}
