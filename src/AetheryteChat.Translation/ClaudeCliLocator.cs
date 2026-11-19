namespace AetheryteChat.Translation;

/// <summary>
/// claude.exe 를 찾는다. npm 으로 깔면 PATH 에는 claude.cmd 래퍼만 있으므로
/// 래퍼가 가리키는 실제 exe 를 찾아 셸 없이 실행한다 (DESIGN 4.4).
/// </summary>
public static class ClaudeCliLocator
{
    private const string NpmExeRelative = @"node_modules\@anthropic-ai\claude-code\bin\claude.exe";

    public static string? Find()
    {
        foreach (var dir in CandidateDirs())
        {
            var exe = Path.Combine(dir, "claude.exe");
            if (File.Exists(exe))
                return exe;

            // npm 전역 설치: <dir>\claude.cmd → <dir>\node_modules\@anthropic-ai\claude-code\bin\claude.exe
            if (File.Exists(Path.Combine(dir, "claude.cmd")))
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
        // 게임 프로세스에서는 PATH 가 다를 수 있으니 흔한 설치 위치를 먼저 본다.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(appData, "npm");
        yield return Path.Combine(home, ".local", "bin"); // 네이티브 설치 프로그램 위치
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links"); // winget
        yield return Path.Combine(home, "scoop", "shims"); // scoop

        foreach (var dir in CliLocation.PathDirs())
            yield return dir;
    }
}
