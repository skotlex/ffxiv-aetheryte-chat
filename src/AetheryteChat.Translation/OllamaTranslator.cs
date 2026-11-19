using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetheryteChat.Translation;

public sealed class OllamaOptions : TranslatorOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    /// <summary>2026-11-18 비교에서 한→일 게임 용어까지 맞은 gemma3:12b 가 기본 (DESIGN 6.2)</summary>
    public string Model { get; set; } = "gemma3:12b";

    /// <summary>번역 사이에 모델을 VRAM 에 올려 두는 시간. 짧으면 매번 다시 올리느라 느려진다</summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>처음 부를 때 모델을 VRAM 에 올리는 시간까지 포함한다</summary>
    public override TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// 내 PC 에서 도는 Ollama 로 번역한다. 무료·오프라인이고, LLM 이라 지시문·용어집을 넣을 수 있다 (DESIGN 6.1).
/// 번역할 때만 GPU 를 쓰고, 그동안은 게임과 GPU 를 나눠 쓴다.
/// </summary>
public sealed class OllamaTranslator(OllamaOptions options) : ITranslator
{
    private static readonly HttpClient Http = new();

    // 답을 {"translation": "..."} 꼴로 강제한다 (Ollama 의 구조화 출력)
    private static readonly JsonNode AnswerSchema = JsonNode.Parse("""
        {"type":"object","properties":{"translation":{"type":"string"}},"required":["translation"]}
        """)!;

    public string Name => $"Ollama ({options.Model})";
    public string ShortName => options.Model;
    public string NotReadyHint => $"모델 없음 — 터미널에서 'ollama pull {options.Model}'로 받아 주세요";

    /// <summary>로그인 대신 Ollama 가 떠 있고 모델을 받아 두었는지 본다. 모델은 부르지 않는다.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        var models = await ListModelsAsync(options.Endpoint, ct);
        var installed = models.Any(m => SameModel(m, options.Model));

        // 처음 번역할 때 모델을 VRAM 에 올리느라 7~8초 걸리므로(실측) 미리 올려 둔다. 기다리지 않는다
        if (installed)
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

    /// <summary>
    /// 모델을 받는다 (터미널의 ollama pull 과 같다). 수 GB 라 오래 걸리므로 진행 상황을 onProgress 로 알린다
    /// </summary>
    /// <exception cref="TranslationException">연결 실패 또는 Ollama 가 거절 (없는 모델 이름 등)</exception>
    public static async Task PullAsync(string endpoint, string model, Action<string> onProgress, CancellationToken ct)
    {
        var url = $"{NormalizeEndpoint(endpoint)}/api/pull";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new JsonObject { ["model"] = model, ["stream"] = true }),
        };
        try
        {
            // 응답 머리글만 받고 돌아와, 한 줄에 하나씩 오는 진행 상황을 읽는다
            using var response = await PullHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"모델을 받지 못했습니다 (HTTP {(int)response.StatusCode}): {Trim(await response.Content.ReadAsStringAsync(ct))}");

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new TranslationException($"모델을 받지 못했습니다: {error.GetString()}");

                if (root.TryGetProperty("total", out var t) && t.TryGetInt64(out var total) && total > 0
                    && root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var completed))
                    onProgress($"{completed * 100 / total}% ({completed / 1e9:0.0} / {total / 1e9:0.0} GB)");
                else if (root.TryGetProperty("status", out var status))
                    onProgress(status.GetString() ?? "");
            }
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"Ollama에 연결하지 못했습니다 ({url}) — Ollama를 켜 주세요.", e);
        }
        catch (JsonException e)
        {
            throw new TranslationException($"{url} 의 응답이 Ollama 형식이 아닙니다.", e);
        }
    }

    // 모델 받기는 수 분 걸릴 수 있어 HttpClient 기본 제한 시간(100초)을 쓰지 않는다. 멈추는 것은 취소로 한다
    private static readonly HttpClient PullHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

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
            var body = new JsonObject { ["model"] = options.Model, ["keep_alive"] = options.KeepAlive };
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
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["stream"] = false,
            ["think"] = false, // 생각하는 모델(qwen3 등)도 바로 답하게 한다 — 채팅 번역에는 생각이 필요 없고 느려진다
            ["keep_alive"] = options.KeepAlive,
            ["format"] = AnswerSchema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0.2 },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = Prompts.For(direction, options.Glossary) },
                new JsonObject { ["role"] = "user", ["content"] = Prompts.WrapMessage(text) },
            },
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);

        var started = DateTime.UtcNow;
        string responseText;
        try
        {
            using var response = await Http.PostAsJsonAsync($"{BaseUrl}/api/chat", body, timeoutCts.Token);
            responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"Ollama HTTP {(int)response.StatusCode}: {Trim(responseText)}");
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

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        var content = root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
            ? c.GetString() ?? ""
            : throw new TranslationException($"Ollama 응답에 message.content가 없습니다: {Trim(responseText)}");

        long? input = root.TryGetProperty("prompt_eval_count", out var p) && p.TryGetInt64(out var pv) ? pv : null;
        long? output = root.TryGetProperty("eval_count", out var o) && o.TryGetInt64(out var ov) ? ov : null;

        return new TranslationResult(AnswerJson.ExtractTranslation(content), elapsed, input, output);
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
