using System.Diagnostics;
using System.Text;

namespace AetheryteChat.Translation;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed);

/// <summary>
/// CLI 프로세스를 셸 없이 직접 실행한다. 입력은 stdin 으로 넘기고(DESIGN 4.4) 반드시 닫는다.
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
