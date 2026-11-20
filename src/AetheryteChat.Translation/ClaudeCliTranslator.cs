using System.Text.Json;

namespace AetheryteChat.Translation;

public sealed class ClaudeCliOptions : TranslatorOptions
{
    /// <summary>claude.exe 경로. null 이면 자동으로 찾는다.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>반드시 지정한다. 비우면 기본 모델(Opus 등)로 돌아 무겁다.</summary>
    public string Model { get; set; } = "haiku";

    /// <summary>low / medium / high. null 이면 넘기지 않는다.</summary>
    public string? Effort { get; set; }

    public override TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 구독 계정으로 로그인한 Claude Code CLI 를 실행해 번역한다. API 키를 받지 않고, 구독 로그인 토큰을 빼내 API 를
/// 직접 부르지도 않는다 — Anthropic 이 허용하지 않는 방식이라 반드시 CLI 자체를 실행한다.
/// </summary>
public sealed class ClaudeCliTranslator(ClaudeCliOptions options) : ITranslator
{
    // 작업 폴더에 CLAUDE.md 같은 것이 끼어들지 않게 빈 폴더에서 실행한다.
    private static readonly string WorkDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AetheryteChat", "claude-cwd")).FullName;

    private string? _resolvedPath;

    public string Name => "Claude CLI";
    public string ShortName => $"Claude {options.Model}";
    public string NotReadyHint => "로그인 안 됨";
    public bool CanLogin => true;

    private string Executable => _resolvedPath ??= CliLocation.Resolve(
        options.ExecutablePath, ClaudeCliLocator.Find,
        "claude.exe를 찾지 못했습니다 — Claude Code를 설치하거나 '번역 도구' 탭에서 경로를 지정해 주세요.");

    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        var result = await CliRunner.RunAsync(
            Executable, ["auth", "status", "--json"], stdin: null, WorkDir, TimeSpan.FromSeconds(10), ct);
        if (result.ExitCode != 0)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            return JsonRead.Property(doc.RootElement, "loggedIn")?.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>claude auth login — 브라우저에서 구독 계정으로 로그인한다</summary>
    public Task LoginAsync(CancellationToken ct = default) =>
        CliRunner.RunInNewWindowAsync(Executable, "auth login", WorkDir, ct);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var terms = options.Glossary.Match(text, direction);
        var (answer, elapsed, input, output) =
            await AskAsync(Prompts.For(direction), Prompts.UserTurn(text, terms, direction), ct);
        return new TranslationResult(AnswerJson.ExtractTranslation(answer), elapsed, input, output);
    }

    /// <summary>여러 줄을 한 번의 호출로 번역한다 (앞뒤 줄을 문맥으로 쓴다)</summary>
    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var terms = options.Glossary.Match(Prompts.JoinForGlossary(texts), direction);
        var (answer, elapsed, input, output) =
            await AskAsync(Prompts.ForBatch(direction), Prompts.BatchUserTurn(texts, terms, direction), ct);
        return new BatchTranslationResult(AnswerJson.ExtractTranslations(answer, texts.Count, direction), elapsed, input, output);
    }

    /// <summary>지시문과 사용자 메시지로 CLI 를 한 번 실행하고 모델의 답(봉투 안의 result)을 돌려준다</summary>
    private async Task<(string Answer, TimeSpan Elapsed, long? Input, long? Output)> AskAsync(
        string systemPrompt, string userTurn, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--no-session-persistence",
            "--tools", "",
            "--strict-mcp-config",
            "--setting-sources", "",
            "--model", options.Model,
            "--system-prompt", systemPrompt,
        };
        if (!string.IsNullOrEmpty(options.Effort))
            args.AddRange(["--effort", options.Effort]);

        // 용어는 명령줄이 아니라 stdin 으로 넘긴다 — 윈도우 명령줄 길이 한도(약 32,000자)에 걸리지 않게
        var run = await CliRunner.RunAsync(Executable, args, userTurn, WorkDir, options.Timeout, ct);

        if (run.ExitCode != 0 && string.IsNullOrWhiteSpace(run.StdOut))
            throw new TranslationException($"claude 종료 코드 {run.ExitCode}: {Trim(run.StdErr)}")
            {
                IsAuthError = AuthErrors.Matches(run.StdErr),
            };

        var (answer, input, output) = ParseEnvelope(run.StdOut);
        return (answer, run.Elapsed, input, output);
    }

    /// <summary>
    /// 봉투 JSON: {"type":"result","is_error":false,"result":"...","usage":{...}}.
    /// 답은 result 문자열 안의 JSON 이다 — 그 문자열과 토큰 수를 돌려준다.
    /// </summary>
    internal static (string Answer, long? Input, long? Output) ParseEnvelope(string stdout)
    {
        JsonDocument envelope;
        try
        {
            envelope = JsonDocument.Parse(stdout);
        }
        catch (JsonException e)
        {
            throw new TranslationException($"claude 출력이 JSON이 아닙니다: {Trim(stdout)}", e);
        }

        using (envelope)
        {
            var root = envelope.RootElement;
            var resultText = JsonRead.String(root, "result") ?? "";

            if (JsonRead.Property(root, "is_error")?.ValueKind == JsonValueKind.True)
                throw new TranslationException($"claude 오류: {Trim(resultText)}")
                {
                    IsAuthError = AuthErrors.Matches(resultText) || JsonRead.Int64(root, "api_error_status") == 401,
                };

            long? input = null, output = null;
            if (JsonRead.Property(root, "usage") is { ValueKind: JsonValueKind.Object } usage)
            {
                input = SumTokens(usage, "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
                output = SumTokens(usage, "output_tokens");
            }

            return (resultText, input, output);
        }
    }

    private static long? SumTokens(JsonElement usage, params string[] names)
    {
        long? sum = null;
        foreach (var name in names)
        {
            if (JsonRead.Int64(usage, name) is { } n)
                sum = (sum ?? 0) + n;
        }
        return sum;
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
