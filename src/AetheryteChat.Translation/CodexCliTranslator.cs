using System.Text.Json;

namespace AetheryteChat.Translation;

public sealed class CodexCliOptions : TranslatorOptions
{
    /// <summary>codex.exe 경로. null 이면 자동으로 찾는다.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>null 이면 Codex 기본 모델</summary>
    public string? Model { get; set; }

    public string ReasoningEffort { get; set; } = "low";

    /// <summary>
    /// 번역 전용 지침으로 실행한다. CLI 가 <see cref="CodexCliTranslator.LightweightMinVersion"/> 보다 낮으면
    /// 켜 두어도 기존 방식으로 번역한다
    /// </summary>
    public bool Lightweight { get; set; } = true;

    /// <summary>프로세스 시작과 서버 응답을 포함한 요청 제한 시간</summary>
    public override TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 구독 계정으로 로그인한 Codex CLI 를 실행해 번역한다.
/// 경량 모드에서는 기본 코딩 지침을 번역 지침으로 바꾸고 원문만 stdin 으로 넘긴다.
/// 기존 방식(이전 CLI·경량 끔)은 시스템 프롬프트 옵션이 없어 번역 지시와 채팅 원문을 함께 stdin 으로 넘긴다.
/// </summary>
public sealed class CodexCliTranslator(CodexCliOptions options) : ITranslator
{
    // 작업 폴더의 AGENTS.md 등이 끼어들지 않게 빈 폴더에서 실행한다.
    private static readonly string WorkDir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AetheryteChat", "codex-cwd")).FullName;

    /// <summary>
    /// 경량 번역에 필요한 최소 CLI 버전. 이전 CLI 는 --ignore-user-config 를 모르는 옵션으로 보고 실패하고,
    /// 모르는 -c 설정 키는 조용히 무시해 번역 지침이 빠질 수 있다
    /// </summary>
    public static readonly Version LightweightMinVersion = new(0, 154, 0);

    private string? _resolvedPath;
    private readonly object _versionLock = new();
    private Task<Version?>? _version;
    private volatile string? _lightweightOffReason;

    /// <summary>경량 번역을 켜 두었지만 쓰지 못할 때 그 이유 (CLI 버전 확인 뒤에 정해진다). 쓸 수 있거나 꺼 두었으면 null</summary>
    public string? LightweightOffReason => _lightweightOffReason;

    private const int CacheCapacity = 256;
    private readonly object _cacheLock = new();
    private readonly Dictionary<CacheKey, string> _cache = new();
    private readonly Queue<CacheKey> _cacheOrder = new();
    /// <param name="Terms">이 줄에 맞은 용어 (프롬프트에 넣는 그대로). 용어집 전체가 아니라 이것만 같으면 같은 번역이다</param>
    private sealed record CacheKey(string Text, TranslationDirection Direction, string Terms,
        string? Model, string Effort, bool Lightweight);

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
        // 상태를 확인할 때 버전도 미리 읽어, 번역 도구 탭에 경량 번역을 쓰지 못하는 이유를 바로 보여 준다
        await UseLightweightAsync();
        return result.ExitCode == 0;
    }

    /// <summary>이번 번역을 경량 모드로 할지. 설정이 켜져 있고 CLI 가 최소 버전 이상일 때만</summary>
    private async Task<bool> UseLightweightAsync()
    {
        if (!options.Lightweight)
            return false;
        var version = await GetVersionAsync();
        if (version is not null && version >= LightweightMinVersion)
        {
            _lightweightOffReason = null;
            return true;
        }
        _lightweightOffReason = version is null
            ? "Codex CLI 버전을 확인하지 못해 경량 번역 대신 기존 방식으로 번역합니다."
            : $"Codex CLI {version.ToString(3)}은(는) 경량 번역을 지원하지 않아 기존 방식으로 번역합니다 " +
              $"({LightweightMinVersion.ToString(3)} 이상 필요 — npm install -g @openai/codex 로 업데이트).";
        return false;
    }

    /// <summary>설치된 CLI 버전. 'codex --version' 은 모델을 부르지 않는다. 번역 도구를 만들 때마다 한 번만 읽는다</summary>
    private Task<Version?> GetVersionAsync()
    {
        lock (_versionLock)
            return _version ??= ReadVersionAsync();
    }

    private async Task<Version?> ReadVersionAsync()
    {
        try
        {
            // 여러 번역이 같이 기다리는 작업이라 한 요청의 취소에 묶지 않는다
            var run = await CliRunner.RunAsync(
                Executable, ["--version"], stdin: null, WorkDir, TimeSpan.FromSeconds(10), CancellationToken.None);
            return run.ExitCode == 0 ? ParseVersion(run.StdOut) : null;
        }
        catch (TranslationException)
        {
            return null; // 실행 파일이 없으면 번역할 때 그 오류를 그대로 보여 준다
        }
    }

    /// <summary>'codex-cli 0.154.0' 같은 출력에서 버전을 꺼낸다. 뒤에 붙은 '-alpha.1' 등은 버린다</summary>
    internal static Version? ParseVersion(string output) =>
        System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\.(\d+)\.(\d+)") is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;

    /// <summary>codex login — 브라우저에서 ChatGPT 계정으로 로그인한다</summary>
    public Task LoginAsync(CancellationToken ct = default) =>
        CliRunner.RunInNewWindowAsync(Executable, "login", WorkDir, ct);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lightweight = await UseLightweightAsync();
        var terms = options.Glossary.Match(text, direction);
        var userTurn = Prompts.UserTurn(text, terms, direction);
        var cacheKey = new CacheKey(text, direction, Glossary.Format(terms, direction), options.Model,
            options.ReasoningEffort, lightweight);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
                return new TranslationResult(cached, TimeSpan.Zero, 0, 0);
        }

        var (answer, elapsed, input, output) = await AskAsync(Prompts.For(direction), userTurn, lightweight, ct);
        var result = new TranslationResult(AnswerJson.ExtractTranslation(answer), elapsed, input, output);
        // 성공한 번역만 메모리에 보관한다. 방향·맞은 용어·모델·추론 설정이 다르면 새로 번역한다.
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            lock (_cacheLock)
            {
                if (_cache.TryAdd(cacheKey, result.Text))
                {
                    _cacheOrder.Enqueue(cacheKey);
                    if (_cache.Count > CacheCapacity)
                        _cache.Remove(_cacheOrder.Dequeue());
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 여러 줄을 한 번의 호출로 번역한다 (앞뒤 줄을 문맥으로 쓴다). 묶음은 그때그때 달라 위의 캐시에 넣지 않는다 —
    /// 플러그인이 줄마다 기억한다
    /// </summary>
    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lightweight = await UseLightweightAsync();
        var terms = options.Glossary.Match(Prompts.JoinForGlossary(texts), direction);
        var (answer, elapsed, input, output) =
            await AskAsync(Prompts.ForBatch(direction), Prompts.BatchUserTurn(texts, terms, direction), lightweight, ct);
        return new BatchTranslationResult(AnswerJson.ExtractTranslations(answer, texts.Count, direction), elapsed, input, output);
    }

    /// <summary>지시문과 사용자 메시지로 codex exec 를 한 번 실행하고 모델의 답(agent_message)을 돌려준다</summary>
    private async Task<(string Answer, TimeSpan Elapsed, long? Input, long? Output)> AskAsync(
        string instructions, string userTurn, bool lightweight, CancellationToken ct)
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

        string? instructionsFile = null;
        try
        {
            var prompt = instructions + "\n\n" + userTurn;
            if (lightweight)
            {
                // 요청마다 별도 파일을 써서 동시에 도는 송신/수신 번역이 서로의 파일을 지우지 않는다.
                instructionsFile = Path.Combine(WorkDir, $"translation-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(instructionsFile, instructions, ct);
                args.AddRange([
                    "--ignore-user-config",
                    "-c", $"model_instructions_file={JsonSerializer.Serialize(instructionsFile)}",
                    "-c", "project_doc_max_bytes=0",
                    "-c", "skills.include_instructions=false",
                    "-c", "include_apps_instructions=false",
                    "-c", "include_collaboration_mode_instructions=false",
                    "-c", "include_environment_context=false",
                    "-c", "features.shell_tool=false",
                    "-c", "features.apply_patch_freeform=false",
                    "-c", "features.apps=false",
                    "-c", "features.plugins=false",
                    "-c", "features.memories=false",
                    "-c", "features.multi_agent=false",
                    "-c", "features.multi_agent_v2=false",
                    "-c", "agents.enabled=false",
                    "-c", "tools.update_plan.enabled=false",
                    "-c", "web_search=\"disabled\"",
                ]);
                prompt = userTurn;
            }
            var run = await CliRunner.RunAsync(Executable, args, prompt, WorkDir, options.Timeout, ct);
            var (answer, input, output) = ParseEvents(run.StdOut, run.StdErr, run.ExitCode);
            return (answer, run.Elapsed, input, output);
        }
        finally
        {
            if (instructionsFile is not null)
            {
                try { File.Delete(instructionsFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// --json 출력은 한 줄에 이벤트 하나. 답은 item.completed(agent_message) 의 text, 사용량은 turn.completed 의 usage.
    /// </summary>
    internal static (string Answer, long? Input, long? Output) ParseEvents(string stdout, string stderr, int exitCode)
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

        return (answer, input, output);
    }
}
