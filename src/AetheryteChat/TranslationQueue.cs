using System.Text.RegularExpressions;
using AetheryteChat.Translation;

namespace AetheryteChat;

internal enum EngineStatus
{
    Checking,
    Ready,
    NotLoggedIn,
    Unavailable,
}

/// <summary>
/// 받은 채팅을 백그라운드에서 번역한다. 게임 스레드는 기다리지 않는다.
/// 묶음 번역·캐시·예비 엔진은 다음 단계에서 붙인다 (DESIGN 4.5, 9절 6번).
/// </summary>
internal sealed partial class TranslationQueue : IDisposable
{
    // 붐비는 채널에서 CLI 프로세스가 한꺼번에 뜨지 않게 동시 실행 수를 묶는다
    private const int MaxConcurrent = 2;

    private readonly ITranslator _translator;
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();

    public TranslationQueue(ITranslator translator)
    {
        _translator = translator;
        _ = CheckLoginAsync();
    }

    public EngineStatus Status { get; private set; } = EngineStatus.Checking;
    public string? StatusDetail { get; private set; }
    public bool AutoTranslate { get; set; } = true;
    public string EngineName => _translator.Name;

    /// <summary>게임 스레드에서 불린다. 번역할 줄이면 대기열에 넣고 바로 돌아온다.</summary>
    public void Enqueue(ChatLine line)
    {
        if (!AutoTranslate || Status != EngineStatus.Ready || !ContainsJapanese(line.Message))
            return;

        line.MarkPending();
        _ = TranslateAsync(line, _cts.Token);
    }

    public void Dispose()
    {
        // 진행 중인 CLI 프로세스는 CliRunner 가 취소를 받아 종료한다.
        // 아직 끝나지 않은 작업이 토큰을 쓰므로 CTS 는 Dispose 하지 않고 취소만 한다.
        _cts.Cancel();
    }

    private async Task CheckLoginAsync()
    {
        try
        {
            Status = await _translator.IsLoggedInAsync(_cts.Token) ? EngineStatus.Ready : EngineStatus.NotLoggedIn;
        }
        catch (OperationCanceledException)
        {
        }
        catch (TranslationException e)
        {
            Status = EngineStatus.Unavailable;
            StatusDetail = e.Message;
        }
    }

    private async Task TranslateAsync(ChatLine line, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                var result = await _translator.TranslateAsync(line.Message, TranslationDirection.JaToKo, ct);
                line.Complete(TranslationState.Done, result.Text);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // 플러그인을 내릴 때
        }
        catch (TranslationException e)
        {
            line.Complete(TranslationState.Failed, e.Message);
            // 채팅 내용이 로그 파일에 남지 않게 원인 메시지는 창에만 보여 준다
            Plugin.Log.Warning("받은 채팅 번역 실패");
        }
    }

    /// <summary>가나 또는 한자가 있으면 일본어로 본다. 한국어·영어만 있는 줄은 번역하지 않아 사용량을 아낀다.</summary>
    private static bool ContainsJapanese(string text) => JapaneseChars().IsMatch(text);

    [GeneratedRegex(@"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ]")]
    private static partial Regex JapaneseChars();
}
