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
}

public sealed class TranslationException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>로그인이 안 됐거나 풀려서 실패했다 — 다시 로그인하면 될 일이다</summary>
    public bool IsAuthError { get; init; }
}
