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

    private const int MaxRememberedSent = 20;
    private readonly OrderedDictionary<string, string> _sent = new(); // 보낸 일본어 → 한국어 원문

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
        // 내가 보낸 글이 돌아온 것이면 CLI 를 부르지 않고 보낼 때의 한국어 원문을 쓴다
        if (TakeSent(line.Message) is { } korean)
        {
            line.Complete(TranslationState.Done, korean);
            return;
        }

        if (!AutoTranslate || Status != EngineStatus.Ready || !ContainsJapanese(line.Message))
            return;

        line.MarkPending();
        _ = TranslateAsync(line, _cts.Token);
    }

    /// <summary>
    /// 보낼 한국어를 일본어로 번역한다. 사용자가 기다리는 중이라 받은 채팅의 동시 실행 제한을 거치지 않는다.
    /// </summary>
    public Task<TranslationResult> TranslateOutgoingAsync(string korean) =>
        _translator.TranslateAsync(korean, TranslationDirection.KoToJa, _cts.Token);

    /// <summary>보낸 일본어와 그 한국어 원문을 기억한다. 받은 채팅에 같은 글이 오면 한 번 쓰고 지운다.</summary>
    public void RememberSent(string japanese, string korean)
    {
        if (korean.Length == 0)
            return; // 미리보기를 직접 고치거나 써 넣은 경우 — 원문과 다르니 평소처럼 번역한다

        lock (_sent)
        {
            _sent.Remove(japanese);
            _sent.Add(japanese, korean);
            while (_sent.Count > MaxRememberedSent)
                _sent.RemoveAt(0);
        }
    }

    private string? TakeSent(string message)
    {
        lock (_sent)
        {
            if (!_sent.Remove(message, out var korean))
                return null;
            return korean;
        }
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
