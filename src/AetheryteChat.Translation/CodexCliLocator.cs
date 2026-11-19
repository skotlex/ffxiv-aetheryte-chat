using System.Runtime.InteropServices;

namespace AetheryteChat.Translation;

/// <summary>
/// codex.exe 를 찾는다. npm 으로 깔면 PATH 에는 codex.cmd → node 로 도는 codex.js 만 있고, 실제 실행 파일은
/// 플랫폼 패키지(@openai/codex-win32-x64 또는 -arm64) 안에 있다. node·cmd 를 거치지 않고 그 exe 를 셸 없이
/// 직접 실행한다 (cmd 따옴표 규칙을 피한다).
/// </summary>
public static class CodexCliLocator
{
    public static string? Find()
    {
        foreach (var dir in CandidateDirs())
        {
            var exe = Path.Combine(dir, "codex.exe");
            if (File.Exists(exe))
                return exe;

            // npm 전역 설치: <dir>\codex.cmd → <dir>\node_modules\@openai\codex\... 의 플랫폼 패키지 속 codex.exe
            if (File.Exists(Path.Combine(dir, "codex.cmd")) && FindInNpmPackage(Path.Combine(dir, "node_modules")) is { } npmExe)
                return npmExe;
        }
        return null;
    }

    /// <summary>
    /// 플랫폼 패키지는 codex 패키지 안(node_modules\@openai\codex\node_modules\@openai\codex-win32-*)에 들어가거나
    /// npm 이 한 단계 위(node_modules\@openai\codex-win32-*)로 끌어올린다. 둘 다 보고, 이 PC 의 CPU 에 맞는 것을 먼저 고른다
    /// </summary>
    private static string? FindInNpmPackage(string nodeModules)
    {
        string[] scopes =
        [
            Path.Combine(nodeModules, "@openai", "codex", "node_modules", "@openai"),
            Path.Combine(nodeModules, "@openai"),
        ];
        var preferred = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "codex-win32-arm64" : "codex-win32-x64";

        foreach (var scope in scopes.Where(Directory.Exists))
        {
            var packages = Directory.EnumerateDirectories(scope, "codex-win32-*")
                .OrderBy(p => Path.GetFileName(p) == preferred ? 0 : 1);
            foreach (var package in packages)
            {
                // vendor\<target triple>\bin\codex.exe — target triple(x86_64-pc-windows-msvc 등)은 패키지마다 다르다
                var vendor = Path.Combine(package, "vendor");
                if (!Directory.Exists(vendor))
                    continue;
                foreach (var target in Directory.EnumerateDirectories(vendor))
                {
                    var exe = Path.Combine(target, "bin", "codex.exe");
                    if (File.Exists(exe))
                        return exe;
                }
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
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links"); // winget
        yield return Path.Combine(home, "scoop", "shims"); // scoop

        foreach (var dir in CliLocation.PathDirs())
            yield return dir;
    }
}
