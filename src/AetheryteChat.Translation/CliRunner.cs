using System.Diagnostics;
using System.Text;

namespace AetheryteChat.Translation;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed);

/// <summary>
/// CLI 프로세스를 셸 없이 직접 실행한다. 채팅 원문은 인자가 아니라 stdin 으로 넘기고 — 따옴표·줄바꿈이 든 글을
/// 인자로 넘기면 조각날 수 있다 — stdin 은 넘길 것이 없어도 반드시 닫는다 (열어 두면 CLI 가 입력을 기다린다).
/// </summary>
internal static class CliRunner
{
    // BOM 이 stdin 앞에 붙으면 프롬프트 첫 글자가 오염된다.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<CliResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? stdin,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            throw new TranslationException($"'{executable}'를 실행하지 못했습니다: {e.Message}", e);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            if (stdin is not null)
                await process.StandardInput.WriteAsync(stdin.AsMemory(), timeoutCts.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();
            return new CliResult(process.ExitCode, stdout, stderr, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            if (ct.IsCancellationRequested)
                throw;
            throw new TranslationException($"제한 시간 {timeout.TotalSeconds:0}초를 넘겨 프로세스를 종료했습니다.");
        }
    }

    /// <summary>
    /// 로그인처럼 사용자가 직접 보고 입력해야 하는 명령을 새 콘솔 창에서 실행하고, 창이 닫힐 때까지 기다린다.
    /// 인자는 따옴표가 필요 없는 짧은 고정 명령(auth login 등)만 넘긴다
    /// </summary>
    public static async Task RunInNewWindowAsync(
        string executable, string arguments, string workingDirectory, CancellationToken ct)
    {
        // 셸 실행이라야 게임 프로세스와 따로 새 콘솔 창이 뜬다
        var psi = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        };
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception e)
        {
            throw new TranslationException($"로그인 창을 열지 못했습니다: {e.Message}", e);
        }
        if (process is null)
            throw new TranslationException("로그인 창을 열지 못했습니다.");

        using (process)
            await process.WaitForExitAsync(ct);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 이미 끝났거나 권한이 없으면 무시한다.
        }
    }
}
