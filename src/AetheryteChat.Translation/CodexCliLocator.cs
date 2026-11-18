namespace AetheryteChat.Translation;

/// <summary>
/// codex.exe 를 찾는다. npm 으로 깔면 PATH 에는 codex.cmd → node 로 도는 codex.js 만 있고, 실제 실행 파일은
/// 플랫폼 패키지 안에 있다. node 를 거치지 않고 그 exe 를 셸 없이 직접 실행한다 (DESIGN 4.4).
/// </summary>
public static class CodexCliLocator
{
    private const string NpmExeRelative =
        @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

    public static string? Find()
    {
        foreach (var dir in CandidateDirs())
        {
            var exe = Path.Combine(dir, "codex.exe");
            if (File.Exists(exe))
                return exe;

            // npm 전역 설치: <dir>\codex.cmd → <dir>\node_modules\@openai\codex\...\codex.exe
            if (File.Exists(Path.Combine(dir, "codex.cmd")))
            {
                var npmExe = Path.Combine(dir, NpmExeRelative);
                if (File.Exists(npmExe))
                    return npmExe;
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        // 게임 프로세스에서는 PATH 가 다를 수 있으니 npm 전역 설치 위치를 먼저 본다.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return dir;
    }
}
