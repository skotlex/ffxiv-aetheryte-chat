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
    long? OutputTokens);

public interface ITranslator
{
    string Name { get; }

    /// <summary>로그인이 안 됐을 때 터미널에서 칠 명령 (안내용)</summary>
    string LoginCommand { get; }

    /// <summary>모델을 부르지 않고 로그인 상태만 확인한다.</summary>
    Task<bool> IsLoggedInAsync(CancellationToken ct = default);

    /// <exception cref="TranslationException">실행 실패·시간 초과·응답 파싱 실패</exception>
    Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection direction,
        CancellationToken ct = default);
}

public sealed class TranslationException(string message, Exception? inner = null)
    : Exception(message, inner);
