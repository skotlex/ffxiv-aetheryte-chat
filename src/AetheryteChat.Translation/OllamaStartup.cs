using System.ComponentModel;
using System.Diagnostics;

namespace AetheryteChat.Translation;

/// <summary>
/// 공식 설치 프로그램으로 깐 Ollama 앱이 Windows 로그인 때마다 켜지는 것(시작프로그램의 Ollama.lnk)을 끄고 켠다.
/// 끌 때는 바로가기를 지우지 않고 백업 폴더로 옮겨 두었다가, 켤 때 되돌린다
/// </summary>
public static class OllamaStartup
{
    private const string ShortcutName = "Ollama.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    /// <summary>Windows 로그인 때 Ollama 앱이 켜지는지</summary>
    public static bool IsEnabled => File.Exists(ShortcutPath);

    /// <summary>끈 적이 있어 되돌릴 수 있는지</summary>
    public static bool CanEnable(string backupDir) => File.Exists(Path.Combine(backupDir, ShortcutName));

    /// <exception cref="TranslationException">옮기지 못함</exception>
    public static void Disable(string backupDir)
    {
        if (!IsEnabled)
            return;
        Move(ShortcutPath, Path.Combine(Directory.CreateDirectory(backupDir).FullName, ShortcutName));
    }

    /// <exception cref="TranslationException">옮기지 못함</exception>
    public static void Enable(string backupDir)
    {
        if (IsEnabled || !CanEnable(backupDir))
            return;
        Move(Path.Combine(backupDir, ShortcutName), ShortcutPath);
    }

    /// <summary>지금 켜져 있는 Ollama 앱(작업 표시줄 아이콘)과 그 앱이 띄운 서버를 닫는다. 닫은 앱 수</summary>
    public static int CloseApp()
    {
        var closed = 0;
        foreach (var app in Process.GetProcessesByName("ollama app"))
        {
            using (app)
            {
                try
                {
                    app.Kill(entireProcessTree: true);
                    app.WaitForExit(5000);
                    closed++;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // 이미 끝났거나 권한이 없다
                }
            }
        }
        return closed;
    }

    private static void Move(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new TranslationException($"시작프로그램 설정을 바꾸지 못했습니다: {e.Message}", e);
        }
    }
}
