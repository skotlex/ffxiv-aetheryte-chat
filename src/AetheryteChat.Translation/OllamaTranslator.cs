using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AetheryteChat.Translation;

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";
    /// <summary>2026-11-18 비교에서 한→일 게임 용어까지 맞은 gemma3:12b 가 기본 (DESIGN 6.2)</summary>
    public string Model { get; set; } = "gemma3:12b";

    /// <summary>번역 사이에 모델을 VRAM 에 올려 두는 시간. 짧으면 매번 다시 올리느라 느려진다</summary>
    public string KeepAlive { get; set; } = "30m";

    /// <summary>처음 부를 때 모델을 VRAM 에 올리는 시간까지 포함한다</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
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
    public string NotReadyHint => $"모델 없음 — 터미널에서 'ollama pull {options.Model}' 로 받는다";

    /// <summary>로그인 대신 Ollama 가 떠 있고 모델을 받아 두었는지 본다. 모델은 부르지 않는다.</summary>
    public async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        JsonDocument tags;
        try
        {
            using var response = await Http.GetAsync($"{options.Endpoint}/api/tags", timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            tags = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutCts.Token));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new TranslationException("Ollama 가 실행 중이 아니다 — Ollama 를 켠다.", e);
        }

        bool installed;
        using (tags)
        {
            installed = tags.RootElement.GetProperty("models").EnumerateArray()
                .Any(m => m.GetProperty("name").GetString() == options.Model);
        }

        // 처음 번역할 때 모델을 VRAM 에 올리느라 7~8초 걸리므로(실측) 미리 올려 둔다. 기다리지 않는다
        if (installed)
            _ = PreloadAsync();

        return installed; // 모델이 없으면 '로그인 안 됨' 으로 보여 ollama pull 을 안내한다
    }

    /// <summary>프롬프트 없이 generate 를 부르면 모델만 올리고 끝난다</summary>
    private async Task PreloadAsync()
    {
        try
        {
            var body = new JsonObject { ["model"] = options.Model, ["keep_alive"] = options.KeepAlive };
            using var response = await Http.PostAsJsonAsync($"{options.Endpoint}/api/generate", body);
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
                new JsonObject { ["role"] = "system", ["content"] = Prompts.For(direction) },
                new JsonObject { ["role"] = "user", ["content"] = Prompts.WrapMessage(text) },
            },
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(options.Timeout);

        var started = DateTime.UtcNow;
        string responseText;
        try
        {
            using var response = await Http.PostAsJsonAsync($"{options.Endpoint}/api/chat", body, timeoutCts.Token);
            responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new TranslationException($"Ollama HTTP {(int)response.StatusCode}: {Trim(responseText)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException($"Ollama 응답이 {options.Timeout.TotalSeconds:0}초 안에 오지 않았다.");
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"Ollama 에 연결하지 못했다: {e.Message}", e);
        }
        var elapsed = DateTime.UtcNow - started;

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        var content = root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
            ? c.GetString() ?? ""
            : throw new TranslationException($"Ollama 응답에 message.content 가 없다: {Trim(responseText)}");

        long? input = root.TryGetProperty("prompt_eval_count", out var p) && p.TryGetInt64(out var pv) ? pv : null;
        long? output = root.TryGetProperty("eval_count", out var o) && o.TryGetInt64(out var ov) ? ov : null;

        return new TranslationResult(AnswerJson.ExtractTranslation(content), elapsed, input, output);
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
