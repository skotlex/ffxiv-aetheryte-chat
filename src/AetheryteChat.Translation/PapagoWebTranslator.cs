using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AetheryteChat.Translation;

/// <summary>
/// papago.naver.com 웹 화면이 쓰는 번역 주소를 직접 부른다 (TataruHelper 가 쓰는 방식을 참고해 새로 작성).
/// 공개 API 가 아니라서 네이버가 바꾸거나 막으면 동작하지 않는다. 로그인·키는 필요 없다.
/// 프롬프트·용어집을 넣을 수 없어 게임 용어 번역은 LLM 보다 거칠다.
/// </summary>
public sealed class PapagoWebTranslator : ITranslator
{
    private const string TranslateUrl = "https://papago.naver.com/api/text/translation";

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    // 429(요청이 너무 많음)를 받으면 이만큼 쉰다 — 그사이 요청은 보내지 않고 바로 실패시킨다
    private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new();

    private DateTime _cooldownUntil = DateTime.MinValue;

    public string Name => "Papago (웹)";
    public string ShortName => "Papago";
    public string NotReadyHint => "";

    /// <summary>로그인이 필요 없다.</summary>
    public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(true);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _cooldownUntil)
            throw new TranslationException($"Papago가 요청을 제한해 쉬는 중입니다 ({(_cooldownUntil - DateTime.UtcNow).TotalSeconds:0}초 남음).");

        var (source, target) = direction switch
        {
            TranslationDirection.JaToKo => ("ja", "ko"),
            TranslationDirection.KoToJa => ("ko", "ja"),
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TranslateUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["source"] = source,
                ["target"] = target,
                ["text"] = text,
                ["dict"] = "false",
                ["useGlossary"] = "false",
                ["honorific"] = "false",
            }),
        };
        // 웹 화면에서 보낸 요청과 같은 머리글이어야 받아 준다
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Add("device-type", "pc");
        request.Headers.Add("Origin", "https://papago.naver.com");
        request.Headers.Add("Referer", "https://papago.naver.com/");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Dest", "empty");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        var sw = Stopwatch.StartNew();
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await Http.SendAsync(request, timeoutCts.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranslationException($"Papago 응답이 {Timeout.TotalSeconds:0}초 안에 오지 않았습니다.");
        }
        catch (HttpRequestException e)
        {
            throw new TranslationException($"Papago에 연결하지 못했습니다: {e.Message}", e);
        }
        sw.Stop();

        if (status == HttpStatusCode.TooManyRequests)
        {
            _cooldownUntil = DateTime.UtcNow + RateLimitCooldown;
            throw new TranslationException($"Papago가 요청을 제한했습니다(429). {RateLimitCooldown.TotalSeconds:0}초 쉽니다.");
        }
        if (!((int)status is >= 200 and < 300))
            throw new TranslationException($"Papago HTTP {(int)status}: {Trim(body)}");

        using (var doc = JsonRead.Parse(body, "Papago"))
        {
            if (JsonRead.String(doc.RootElement, "translatedText") is { Length: > 0 } translated)
                return new TranslationResult(translated.Trim(), sw.Elapsed, null, null);
        }

        throw new TranslationException($"Papago 응답에 translatedText가 없습니다: {Trim(body)}");
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
