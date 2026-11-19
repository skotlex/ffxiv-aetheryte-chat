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
    public string NotReadyHint => "로그인 안 됨 — 터미널에서 'claude'로 로그인해 주세요";

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
            return doc.RootElement.TryGetProperty("loggedIn", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
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
            "--system-prompt", Prompts.For(direction, options.Glossary),
        };
        if (!string.IsNullOrEmpty(options.Effort))
            args.AddRange(["--effort", options.Effort]);

        var run = await CliRunner.RunAsync(
            Executable, args, Prompts.WrapMessage(text), WorkDir, options.Timeout, ct);

        if (run.ExitCode != 0 && string.IsNullOrWhiteSpace(run.StdOut))
            throw new TranslationException($"claude 종료 코드 {run.ExitCode}: {Trim(run.StdErr)}");

        return ParseEnvelope(run.StdOut, run.Elapsed);
    }

    /// <summary>
    /// 봉투 JSON: {"type":"result","is_error":false,"result":"...","usage":{...}}.
    /// 답은 result 문자열 안의 JSON 이다.
    /// </summary>
    internal static TranslationResult ParseEnvelope(string stdout, TimeSpan elapsed)
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
            var resultText = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";

            if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                throw new TranslationException($"claude 오류: {Trim(resultText)}");

            long? input = null, output = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = SumTokens(usage, "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
                output = SumTokens(usage, "output_tokens");
            }

            return new TranslationResult(AnswerJson.ExtractTranslation(resultText), elapsed, input, output);
        }
    }

    private static long? SumTokens(JsonElement usage, params string[] names)
    {
        long? sum = null;
        foreach (var name in names)
        {
            if (usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n))
                sum = (sum ?? 0) + n;
        }
        return sum;
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
