namespace AetheryteChat.Translation;

public enum TranslationDirection
{
    /// <summary>받은 채팅: 일본어 → 한국어</summary>
    JaToKo,

    /// <summary>보낼 채팅: 한국어 → 일본어</summary>
    KoToJa,
}

/// <param name="Text">번역문</param>
/// <param name="Elapsed">프로세스를 띄워서 답을 받기까지 걸린 시간</param>
/// <param name="InputTokens">캐시를 포함한 입력 토큰 합계. 엔진이 알려 주지 않으면 null</param>
/// <param name="OutputTokens">출력 토큰. 엔진이 알려 주지 않으면 null</param>
public sealed record TranslationResult(
    string Text,
    TimeSpan Elapsed,
    long? InputTokens,
    long? OutputTokens)
{
    /// <summary>
    /// 실제로 번역한 도구의 ShortName. 여러 도구를 차례로 쓰는 <see cref="FallbackTranslator"/> 가 적는다.
    /// null 이면 부른 번역기 자신이다
    /// </summary>
    public string? Engine { get; init; }
}

/// <param name="Texts">원문과 같은 순서, 같은 개수의 번역문</param>
/// <param name="Elapsed">모든 줄을 번역하기까지 걸린 시간</param>
/// <param name="InputTokens">입력 토큰 합계. 엔진이 알려 주지 않으면 null</param>
/// <param name="OutputTokens">출력 토큰 합계. 엔진이 알려 주지 않으면 null</param>
public sealed record BatchTranslationResult(
    IReadOnlyList<string> Texts,
    TimeSpan Elapsed,
    long? InputTokens,
    long? OutputTokens)
{
    /// <summary>실제로 번역한 도구의 ShortName (<see cref="TranslationResult.Engine"/> 과 같다). null 이면 부른 번역기 자신이다</summary>
    public string? Engine { get; init; }
}

public interface ITranslator
{
    string Name { get; }

    /// <summary>IsLoggedInAsync 가 false 일 때 보여 줄 안내 (예: 터미널에서 'claude' 로 로그인)</summary>
    string NotReadyHint { get; }

    /// <summary>번역 결과에 붙일 짧은 모델 이름 (같은 줄의 여러 번역을 구분한다)</summary>
    string ShortName { get; }

    /// <summary>모델을 부르지 않고 로그인 상태만 확인한다.</summary>
    Task<bool> IsLoggedInAsync(CancellationToken ct = default);

    /// <summary>플러그인에서 로그인 창을 띄울 수 있는 도구인지 (Claude·Codex CLI)</summary>
    bool CanLogin => false;

    /// <summary>
    /// 새 콘솔 창에서 CLI 의 로그인 명령을 실행하고(브라우저가 열린다) 그 창이 닫힐 때까지 기다린다.
    /// 로그인했는지는 끝난 뒤 IsLoggedInAsync 로 다시 확인한다
    /// </summary>
    /// <exception cref="TranslationException">실행 파일을 못 찾았거나 창을 열지 못함</exception>
    Task LoginAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <exception cref="TranslationException">실행 실패·시간 초과·응답 파싱 실패</exception>
    Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default);

    /// <summary>
    /// 한 사람이 잇달아 보낸 여러 줄(레이드 공략 매크로 등)을 한 번에 번역한다. 앞뒤 줄을 문맥으로 쓰고 도구를 한 번만 부른다.
    /// 기본 구현은 한 줄씩 차례로 번역한다 — 지시문을 넣을 수 없는 도구(Papago)는 줄을 합쳐 보내면 줄이 어긋날 수 있어서
    /// </summary>
    /// <exception cref="TranslationException">
    /// 실행 실패·시간 초과 등. 답의 줄 수가 맞지 않거나 읽지 못했으면 <see cref="TranslationException.IsMalformedBatch"/>
    /// </exception>
    async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        TranslationDirection direction,
        CancellationToken ct = default)
    {
        var translated = new List<string>(texts.Count);
        var elapsed = TimeSpan.Zero;
        long? input = 0, output = 0;
        foreach (var text in texts)
        {
            var r = await TranslateAsync(text, direction, ct);
            translated.Add(r.Text);
            elapsed += r.Elapsed;
            input = input is { } i && r.InputTokens is { } ri ? i + ri : null;
            output = output is { } o && r.OutputTokens is { } ro ? o + ro : null;
        }
        return new BatchTranslationResult(translated, elapsed, input, output);
    }
}

public sealed class TranslationException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>로그인이 안 됐거나 풀려서 실패했다 — 다시 로그인하면 될 일이다</summary>
    public bool IsAuthError { get; init; }

    /// <summary>
    /// 여러 줄을 한 번에 번역했는데 답의 줄 수가 맞지 않거나 형식을 읽지 못했다 — 도구는 멀쩡하니 줄마다 다시 번역하면 된다
    /// </summary>
    public bool IsMalformedBatch { get; init; }
}
