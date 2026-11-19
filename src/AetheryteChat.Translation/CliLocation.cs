namespace AetheryteChat.Translation;

/// <summary>CLI 실행 파일을 정하는 공통 규칙: 사용자가 지정한 경로가 있으면 그것, 없으면 자동으로 찾은 것</summary>
public static class CliLocation
{
    /// <param name="configured">설정에 적은 경로. 비었으면 자동으로 찾는다</param>
    /// <param name="find">자동 탐색 (못 찾으면 null)</param>
    /// <param name="notFoundMessage">자동으로도 못 찾았을 때의 안내</param>
    /// <exception cref="TranslationException">지정한 경로에 파일이 없거나, 자동으로도 못 찾음</exception>
    public static string Resolve(string? configured, Func<string?> find, string notFoundMessage)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Normalize(configured);
            if (!File.Exists(path))
                throw new TranslationException($"지정한 경로에 파일이 없습니다: {path}");
            if (IsScriptWrapper(path))
                throw new TranslationException(ScriptWrapperMessage);
            return path;
        }
        return find() ?? throw new TranslationException(notFoundMessage);
    }

    public const string ScriptWrapperMessage =
        "npm 의 .cmd·.ps1 래퍼는 쓸 수 없습니다 — 실제 .exe 경로를 넣거나, 칸을 비워 자동으로 찾게 해 주세요.";

    /// <summary>
    /// npm 이 PATH 에 두는 claude.cmd·codex.ps1 같은 래퍼. 셸(cmd.exe)을 거치면 여러 줄 지시문 같은 인자가 깨지므로
    /// 실제 exe 만 실행한다
    /// </summary>
    public static bool IsScriptWrapper(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat" or ".ps1";

    /// <summary>탐색기에서 '경로로 복사' 하면 붙는 따옴표와 앞뒤 공백을 떼고, %APPDATA% 같은 환경 변수를 푼다</summary>
    public static string Normalize(string path) =>
        Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());

    /// <summary>PATH 의 폴더들 (게임 프로세스에서 PATH 가 다를 수 있어 흔한 설치 위치를 먼저 본 뒤에 쓴다)</summary>
    internal static IEnumerable<string> PathDirs()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
