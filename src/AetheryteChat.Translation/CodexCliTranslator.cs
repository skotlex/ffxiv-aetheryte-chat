using System.Text.Json;

namespace AetheryteChat.Translation;

public sealed class CodexCliOptions : TranslatorOptions
{
    /// <summary>codex.exe 경로. null 이면 자동으로 찾는다.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>null 이면 Codex 기본 모델</summary>
    public string? Model { get; set; }

    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Claude 보다 느리다 (2026-11-18 실측 약 5.5초)</summary>
    public override TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 구독 계정으로 로그인한 Codex CLI 를 실행해 번역한다.
/// 시스템 프롬프트 옵션이 없어 번역 지시와 채팅 원문을 함께 stdin 으로 넘긴다.
/// </summary>
public sealed class CodexCliTranslator(CodexCliOptions options) : ITranslator
{
    // 작업 폴더의 AGENTS.md 등이 끼어들지 않게 빈 폴더에서 실행한다.
    private static readonly string WorkDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AetheryteChat", "codex-cwd")).FullName;

    private string? _resolvedPath;

    public string Name => "Codex CLI";
    public string ShortName => options.Model is { Length: > 0 } model ? $"Codex {model}" : "Codex";
    public string NotReadyHint => "로그인 안 됨";
    public bool CanLogin => true;

    private string Executable => _resolvedPath ??= CliLocation.Resolve(
        options.ExecutablePath, CodexCliLocator.Find,
        "codex.exe를 찾지 못했습니다 — Codex CLI를 설치하거나 '번역 도구' 탭에서 경로를 지정해 주세요.");

    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        // 'codex login status' 는 모델을 부르지 않는다. 로그인돼 있으면 종료 코드 0
        var result = await CliRunner.RunAsync(
            Executable, ["login", "status"], stdin: null, WorkDir, TimeSpan.FromSeconds(10), ct);
        return result.ExitCode == 0;
    }

    /// <summary>codex login — 브라우저에서 ChatGPT 계정으로 로그인한다</summary>
    public Task LoginAsync(CancellationToken ct = default) =>
        CliRunner.RunInNewWindowAsync(Executable, "login", WorkDir, ct);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "-s", "read-only",
            "--json",
            "--color", "never",
            "-c", $"model_reasoning_effort={options.ReasoningEffort}",
        };
        if (!string.IsNullOrEmpty(options.Model))
            args.AddRange(["-m", options.Model]);

        var prompt = Prompts.For(direction, options.Glossary) + "\n\n" + Prompts.WrapMessage(text);
        var run = await CliRunner.RunAsync(Executable, args, prompt, WorkDir, options.Timeout, ct);

        return ParseEvents(run.StdOut, run.StdErr, run.ExitCode, run.Elapsed);
    }

    /// <summary>
    /// --json 출력은 한 줄에 이벤트 하나. 답은 item.completed(agent_message) 의 text, 사용량은 turn.completed 의 usage.
    /// </summary>
    internal static TranslationResult ParseEvents(string stdout, string stderr, int exitCode, TimeSpan elapsed)
    {
        string? answer = null;
        string? error = null;
        long? input = null, output = null;

        foreach (var raw in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue; // 이벤트가 아닌 줄은 건너뛴다
            }

            using (doc)
            {
                var root = doc.RootElement;
                switch (JsonRead.String(root, "type"))
                {
                    case "item.completed"
                        when JsonRead.Property(root, "item") is { } item
                             && JsonRead.String(item, "type") == "agent_message"
                             && JsonRead.String(item, "text") is { } text:
                        answer = text;
                        break;

                    case "turn.completed" when JsonRead.Property(root, "usage") is { } usage:
                        // input_tokens 는 캐시된 토큰을 포함한 합계다
                        input = JsonRead.Int64(usage, "input_tokens");
                        output = JsonRead.Int64(usage, "output_tokens");
                        break;

                    case "error" when JsonRead.String(root, "message") is { } message:
                        error = message;
                        break;

                    case "turn.failed"
                        when JsonRead.Property(root, "error") is { } e && JsonRead.String(e, "message") is { } message:
                        error = message;
                        break;
                }
            }
        }

        if (answer is null)
        {
            var reason = error ?? (stderr.Trim().Length > 0 ? stderr.Trim() : $"종료 코드 {exitCode}, 답 없음");
            throw new TranslationException($"codex 오류: {(reason.Length <= 300 ? reason : reason[..300] + "…")}")
            {
                IsAuthError = AuthErrors.Matches(reason),
            };
        }

        return new TranslationResult(AnswerJson.ExtractTranslation(answer), elapsed, input, output);
    }
}
