using AetheryteChat.Translation;

namespace AetheryteChat;

/// <summary>번역 도구를 아직 고르지 않았을 때의 자리. 상태 줄에 '골라 주세요' 를 띄우고 번역은 하지 않는다</summary>
internal sealed class NoEngineTranslator : ITranslator
{
    public string Name => "";
    public string ShortName => "";
    public string NotReadyHint => "번역 도구를 골라 주세요 — '번역 도구' 탭";

    public Task<bool> IsLoggedInAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<TranslationResult> TranslateAsync(string text, TranslationDirection direction, CancellationToken ct = default) =>
        Task.FromException<TranslationResult>(
            new TranslationException("번역 도구를 고르지 않았습니다 — '번역 도구' 탭에서 골라 주세요."));
}
