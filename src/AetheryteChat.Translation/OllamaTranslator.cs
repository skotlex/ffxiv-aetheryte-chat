using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetheryteChat.Translation;

public sealed class OllamaOptions : TranslatorOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    /// <summary>2026-11-18 qwen3:14b·translategemma:12b 와 비교해 한→일 게임 용어까지 맞은 gemma3:12b 가 기본</summary>
    public string Model { get; set; } = "gemma3:12b";

    /// <summary>번역 사이에 모델을 VRAM 에 올려 두는 시간. 짧으면 매번 다시 올리느라 느려진다</summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>
    /// 상태를 확인할 때(플러그인을 켤 때·도구를 바꿀 때) 모델을 미리 GPU 에 올린다. 끄면 첫 번역 때 올리느라 몇 초 늦지만,
    /// 번역하기 전까지 GPU 메모리를 차지하지 않는다
    /// </summary>
    public bool Preload { get; set; } = true;

    /// <summary>
    /// 상태를 확인하기 전에 부른다 — 이 PC 의 Ollama 가 꺼져 있으면 플러그인이 창 없이 켠다. null 이면 켜져 있다고 본다
    /// </summary>
    public Func<CancellationToken, Task>? EnsureServerAsync { get; set; }

    /// <summary>
    /// 받은 채팅을 동시에 보낼 수. 서버의 동시 처리 수(OLLAMA_NUM_PARALLEL)와 맞춘다 — 더 보내면 서버에서 기다리기만 한다
    /// </summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>처음 부를 때 모델을 VRAM 에 올리는 시간까지 포함한다</summary>
    public override TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 내 PC 에서 도는 Ollama 로 번역한다. 무료·오프라인이고, LLM 이라 지시문·용어집을 넣을 수 있다.
/// 번역할 때만 GPU 를 쓰고, 그동안은 게임과 GPU 를 나눠 쓴다.
/// </summary>
public sealed class OllamaTranslator(OllamaOptions options) : ITranslator
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// 모델에 잡게 할 문맥 길이(토큰). 넘기지 않으면 Ollama 가 GPU 메모리에 맞춰 길게 잡아 그만큼 메모리를 더 쓴다 —
    /// 2026-11-19 RTX 4090 에서 qwen3:8b 10.0GB → 5.3GB, aya-expanse:8b 6.4GB → 5.4GB 로 줄었고 번역은 같았다
    /// (gemma3 는 거의 그대로). 채팅 한 줄 번역은 지시문·용어를 합쳐 입력 300토큰, 출력 50토큰 안팎이라 넉넉하다.
    /// 미리 올리기와 번역에 같은 값을 넘겨야 한다 — 다르면 Ollama 가 모델을 다시 올린다
    /// </summary>
    public const int ContextLength = 2048;

    // 답을 {"translation": "..."} 꼴로 강제한다 (Ollama 의 구조화 출력)
    private static readonly JsonNode AnswerSchema = JsonNode.Parse("""
        {"type":"object","properties":{"translation":{"type":"string"}},"required":["translation"]}
        """)!;

    public string Name => $"Ollama ({options.Model})";
    public string ShortName => options.Model;
    public string NotReadyHint => "모델 없음 — '번역 도구' 탭에서 받아 주세요";
    public int MaxConcurrency => options.MaxConcurrency;

    /// <summary>로그인 대신 Ollama 가 떠 있고 모델을 받아 두었는지 본다. 모델은 부르지 않는다.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        if (options.EnsureServerAsync is { } ensureServer)
            await ensureServer(ct);

        var models = await ListModelsAsync(options.Endpoint, ct);
        var installed = models.Any(m => SameModel(m, options.Model));

        // 처음 번역할 때 모델을 VRAM 에 올리느라 7~8초 걸리므로(실측) 미리 올려 둔다. 기다리지 않는다
        if (installed && options.Preload)
            _ = PreloadAsync();

        return installed; // 모델이 없으면 '로그인 안 됨' 으로 보여 ollama pull 을 안내한다
    }

    /// <summary>Ollama 에 받아 둔 모델 이름들 (/api/tags). 설정 화면의 모델 목록에도 쓴다</summary>
    /// <exception cref="TranslationException">Ollama 에 연결하지 못함</exception>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string endpoint, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        var url = $"{NormalizeEndpoint(endpoint)}/api/tags";
        try
        {
            using var response = await Http.GetAsync(url, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            using var tags = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutCts.Token));
            return tags.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString())
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new TranslationException($"Ollama에 연결하지 못했습니다 ({url}) — Ollama를 켜 주세요.", e);
        }
        catch (Exception e) when (e is InvalidOperationException or UriFormatException or NotSupportedException)
        {
            throw new TranslationException($"Ollama 주소가 올바르지 않습니다: {endpoint}", e);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            throw new TranslationException($"{url} 의 응답이 Ollama 형식이 아닙니다 — 주소를 확인해 주세요.", e);
        }
    }

    /// <summary>'http://' 를 빠뜨렸거나 끝에 '/' 를 붙여도 받아 준다</summary>
    public static string NormalizeEndpoint(string endpoint)
    {
        var e = endpoint.Trim().TrimEnd('/');
        return e.Contains("://", StringComparison.Ordinal) ? e : "http://" + e;
    }

    /// <summary>태그를 빼면 ':latest' 로 본다 — 'gemma3' 와 'gemma3:latest' 는 같은 모델</summary>
    public static bool SameModel(string a, string b) =>
        string.Equals(WithTag(a), WithTag(b), StringComparison.OrdinalIgnoreCase);

    private static string WithTag(string model)
    {
        var m = model.Trim();
        return m.Contains(':') ? m : m + ":latest";
    }

    private string BaseUrl => NormalizeEndpoint(options.Endpoint);

    /// <summary>프롬프트 없이 generate 를 부르면 모델만 올리고 끝난다</summary>
    private async Task PreloadAsync()
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = options.Model,
                ["keep_alive"] = options.KeepAlive,
                ["options"] = new JsonObject { ["num_ctx"] = ContextLength },
            };
            using var response = await Http.PostAsJsonAsync($"{BaseUrl}/api/generate", body);
        }
        catch (HttpRequestException)
        {
            // 미리 올리기는 실패해도 첫 번역 때 다시 올린다
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var userTurn = Prompts.UserTurn(text, options.Glossary.Match(text, direction), direction);
        var (content, elapsed, input, output) =
            await ChatAsync(Prompts.For(direction), userTurn, AnswerSchema.DeepClone(), ct);
        return new TranslationResult(AnswerJson.ExtractTranslation(content), elapsed, input, output);
    }

    /// <summary>
    /// 여러 줄을 한 번의 요청으로 번역한다 (앞뒤 줄을 문맥으로 쓴다). 구조화 출력으로 배열 길이를 줄 수에 맞춘다.
    /// 문맥 길이(<see cref="ContextLength"/>)에 들어가게 묶는 줄의 양은 부르는 쪽이 묶는다
    /// </summary>
    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var terms = options.Glossary.Match(Prompts.JoinForGlossary(texts), direction);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["translations"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["minItems"] = texts.Count,
                    ["maxItems"] = texts.Count,
                },
            },
            ["required"] = new JsonArray("translations"),
        };
        var (content, elapsed, input, output) =
            await ChatAsync(Prompts.ForBatch(direction), Prompts.BatchUserTurn(texts, terms, direction), schema, ct);
        return new BatchTranslationResult(AnswerJson.ExtractTranslations(content, texts.Count, direction), elapsed, input, output);
    }

    /// <summary>지시문과 사용자 메시지로 /api/chat 을 한 번 부르고 답(message.content)을 돌려준다</summary>
    private async Task<(string Content, TimeSpan Elapsed, long? Input, long? Output)> ChatAsync(
        string systemPrompt, string userTurn, JsonNode format, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = false,
            ["think"] = false, // 생각하는 모델(qwen3 등)도 바로 답하게 한다 — 채팅 번역에는 생각이 필요 없고 느려진다
            ["keep_alive"] = options.KeepAlive,
            ["format"] = format,
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = ContextLength },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userTurn },
            },
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);

        var started = DateTime.UtcNow;
        string responseText;
        try
        {
            // Ollama 의 실행기가 가끔 서버와의 연결을 끊고 500 을 돌려준다 (2026-11-19 CPU 만 쓰는 노트북에서 8번 중 1번,
            // 실행기 쪽은 답을 다 만든 뒤였다). 같은 요청을 한 번만 다시 보낸다 — 제한 시간은 두 번을 합쳐 센다
            for (var attempt = 1; ; attempt++)
            {
                using var response = await Http.PostAsJsonAsync($"{BaseUrl}/api/chat", body, timeoutCts.Token);
                responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                    break;
                if ((int)response.StatusCode >= 500 && attempt == 1)
                    continue;
                throw new TranslationException($"Ollama HTTP {(int)response.StatusCode}: {Trim(responseText)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException($"Ollama 응답이 {options.Timeout.TotalSeconds:0}초 안에 오지 않았습니다.");
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"Ollama에 연결하지 못했습니다: {e.Message}", e);
        }
        var elapsed = DateTime.UtcNow - started;

        using var doc = JsonRead.Parse(responseText, "Ollama");
        var root = doc.RootElement;
        var content = (JsonRead.Property(root, "message") is { } message ? JsonRead.String(message, "content") : null)
            ?? throw new TranslationException($"Ollama 응답에 message.content가 없습니다: {Trim(responseText)}");

        var input = JsonRead.Int64(root, "prompt_eval_count");
        var output = JsonRead.Int64(root, "eval_count");

        return (content, elapsed, input, output);
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
