// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Text.RegularExpressions;

namespace Bibim.Core
{
    /// <summary>
    /// Maps raw LLM/provider failures to short, actionable, localized messages.
    ///
    /// Providers throw strings like
    ///   "Anthropic API 401: {\"type\":\"error\",...}"  (see AnthropicProvider)
    /// and before this presenter those raw HTTP-status + JSON bodies reached the
    /// chat panel verbatim — the exact "Error: Anthropic API 401: {json}" a field
    /// user screenshotted. Raw detail still goes to the debug log at every catch
    /// site (Logger.LogError); the USER gets a sentence they can act on.
    /// </summary>
    public static class LlmErrorPresenter
    {
        // "Anthropic API 401:", "OpenAI API 429:", "Local LLM (http://…) 503:"
        private static readonly Regex ProviderStatus = new Regex(
            // Lazy any-char gap: Local LLM messages embed the server URL, whose
            // port digits ("http://localhost:11434") a [^0-9] gap could never cross.
            // The trailing ":" anchors the real status, so URL digits can't match.
            @"(?<provider>Anthropic|OpenAI|Local LLM).{0,100}?(?<status>[1-5]\d\d):",
            RegexOptions.Compiled);

        /// <summary>
        /// Shown when the model or a provider safety classifier declines the request
        /// (stop_reason "refusal") and no server-side fallback could serve it. Benign
        /// BIM work rarely triggers this; rephrasing or switching models usually helps.
        /// </summary>
        public static string RefusalMessage()
        {
            return AppLanguage.IsEnglish
                ? "The AI model declined this request (safety policy). Try rephrasing it more specifically, or pick a different model in Settings."
                : "AI 모델이 안전 정책에 따라 이 요청을 거절했습니다. 요청을 더 구체적으로 바꿔 보시거나, 설정에서 다른 모델을 선택해 주세요.";
        }

        public static string ToUserMessage(Exception ex) => ToUserMessage(ex?.Message);

        public static string ToUserMessage(string raw)
        {
            bool en = AppLanguage.IsEnglish;
            if (string.IsNullOrWhiteSpace(raw))
                return en ? "An unknown error occurred while processing the request."
                          : "요청 처리 중 알 수 없는 오류가 발생했습니다.";

            var m = ProviderStatus.Match(raw);
            if (m.Success)
            {
                string provider = m.Groups["provider"].Value;
                int status = int.Parse(m.Groups["status"].Value);

                if (status == 401 || status == 403)
                    return en
                        ? $"AI authentication failed ({provider}, {status}). Please check that the API key is valid."
                        : $"AI 인증에 실패했습니다 ({provider}, {status}). API 키가 유효한지 확인해 주세요.";
                if (status == 429)
                    return en
                        ? $"The AI service is rate-limiting requests ({provider}, 429). Please wait a moment and try again."
                        : $"AI 요청이 일시적으로 제한되었습니다 ({provider}, 429). 잠시 후 다시 시도해 주세요.";
                if (status == 408 || status == 504)
                    return en
                        ? $"The AI service timed out ({provider}, {status}). Please try again."
                        : $"AI 응답이 시간 내에 도착하지 않았습니다 ({provider}, {status}). 다시 시도해 주세요.";
                if (status >= 500)
                    return en
                        ? $"The AI service is temporarily unstable ({provider}, {status}). Please try again shortly."
                        : $"AI 서버가 일시적으로 불안정합니다 ({provider}, {status}). 잠시 후 다시 시도해 주세요.";
                if (status == 400)
                    return en
                        ? $"The AI service rejected the request ({provider}, 400). If this keeps happening, try a new session."
                        : $"AI 서비스가 요청을 거부했습니다 ({provider}, 400). 반복되면 새 세션에서 다시 시도해 주세요.";

                return en
                    ? $"The AI request failed ({provider}, {status}). Please try again."
                    : $"AI 요청이 실패했습니다 ({provider}, {status}). 다시 시도해 주세요.";
            }

            // Non-HTTP failures: network / DNS / TLS / cancellation.
            if (ContainsAny(raw, "No such host", "actively refused", "Connection refused",
                    "network", "socket", "SSL", "TLS", "name or service not known"))
                return en
                    ? "Could not reach the AI service. Please check your internet connection and try again."
                    : "AI 서비스에 연결할 수 없습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.";
            // User-initiated cancellation never reaches this presenter (the
            // orchestrator rethrows OCE when ct is cancelled), so cancel-shaped
            // text here means an internal abort — usually an HttpClient timeout
            // (net48 surfaces those as TaskCanceledException "A task was canceled").
            if (ContainsAny(raw, "timed out", "timeout", "task was canceled",
                    "operation was canceled", "canceled", "cancelled"))
                return en
                    ? "The request was interrupted or timed out. Please try again."
                    : "요청이 중단되었거나 시간을 초과했습니다. 다시 시도해 주세요.";

            // Unknown: localized prefix + one short raw line for diagnosability
            // (full detail is already in the debug log).
            string firstLine = raw;
            int nl = firstLine.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) firstLine = firstLine.Substring(0, nl);
            if (firstLine.Length > 140) firstLine = firstLine.Substring(0, 140) + "…";
            return en
                ? $"A problem occurred while processing the request: {firstLine}"
                : $"요청 처리 중 문제가 발생했습니다: {firstLine}";
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (var n in needles)
                if (text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
