using System.Text.RegularExpressions;

namespace AetheryteChat.Translation;

/// <summary>
/// CLI 가 돌려준 오류가 '로그인이 안 됨·풀림' 인지 본다. 쓰는 도중 로그인이 풀리면(토큰 만료·로그아웃)
/// 번역마다 실패를 쌓지 않고 상태를 '로그인 안 됨' 으로 되돌리는 데 쓴다.
/// </summary>
internal static partial class AuthErrors
{
    // 2026-11-19 빈 설정 폴더(CLAUDE_CONFIG_DIR·CODEX_HOME)로 로그인 안 된 상태를 만들어 확인한 문구:
    //   claude 2.1.238: result "Not logged in · Please run /login" (is_error)
    //   codex 0.154.0:  turn.failed "unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, ..."
    // 토큰 만료 등은 직접 확인하지 못해 흔한 문구를 함께 본다
    [GeneratedRegex(
        @"not logged in|/login|please log ?in|log ?in again|\b401\b|unauthorized|invalid api key|oauth token|token (has )?expired|refresh token|authentication_error",
        RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool Matches(string? message) => message is { Length: > 0 } && Pattern().IsMatch(message);
}
