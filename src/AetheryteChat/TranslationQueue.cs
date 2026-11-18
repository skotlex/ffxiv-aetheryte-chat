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

    // 설정 탭에서 엔진을 바꾸면 갈아 끼운다. 이미 시작한 번역은 시작할 때의 엔진으로 끝난다
    private volatile ITranslator _translator;
    private int _loginCheckId; // 엔진을 바꾼 뒤 늦게 끝난 이전 엔진의 로그인 확인 결과를 버리는 데 쓴다
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private readonly CancellationTokenSource _cts = new();

    private const int MaxRememberedSent = 20;
    private readonly OrderedDictionary<string, string> _sent = new(); // 보낸 일본어 → 한국어 원문

    // 지금 엔진의 로그인 확인 — 엔진을 바꾼 직후의 다시 번역이 확인이 끝나기를 기다린다
    private Task _loginCheck;

    public TranslationQueue(ITranslator translator)
    {
        _translator = translator;
        _loginCheck = CheckLoginAsync(translator, ++_loginCheckId);
    }

    public EngineStatus Status { get; private set; } = EngineStatus.Checking;
    public string? StatusDetail { get; private set; }
    public bool AutoTranslate { get; set; } = true;
    public string EngineName => _translator.Name;
    public string NotReadyHint => _translator.NotReadyHint;

    /// <summary>번역 엔진을 바꾸고 로그인 상태를 다시 확인한다. UI 스레드에서 부른다.</summary>
    public void SetTranslator(ITranslator translator)
    {
        _translator = translator;
        Status = EngineStatus.Checking;
        StatusDetail = null;
        _loginCheck = CheckLoginAsync(translator, ++_loginCheckId);
    }

    /// <summary>게임 스레드에서 불린다. 번역할 줄이면 대기열에 넣고 바로 돌아온다.</summary>
    public void Enqueue(ChatLine line)
    {
        // 내가 보낸 글이 돌아온 것이면 CLI 를 부르지 않고 보낼 때의 한국어 원문을 쓴다
        if (TakeSent(line.Message) is { } korean)
        {
            line.AddTranslation(OwnTextLabel, isOwnText: true).Complete(TranslationState.Done, korean);
            return;
        }

        if (!AutoTranslate || Status != EngineStatus.Ready || !ContainsJapanese(line.Message))
            return;

        Start(line, _translator);
    }

    /// <summary>내가 보낸 글의 한국어 원문에 붙이는 이름</summary>
    public const string OwnTextLabel = "내가 쓴 글";

    /// <summary>
    /// 번역 도구를 바꾼 뒤 부른다. 최근 줄 가운데 번역할 만한 것(일본어가 있고 내가 보낸 글이 아닌 것) count 개를
    /// 새 도구로 다시 번역한다. 이전 번역은 지우지 않고 그 아래에 더한다. 이미 이 도구로 번역한 줄은 건너뛴다.
    /// </summary>
    public async Task RetranslateRecentAsync(IReadOnlyList<ChatLine> lines, int count)
    {
        var translator = _translator;
        try
        {
            await _loginCheck; // 새 도구의 로그인 확인이 끝나기를 기다린다
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 그사이 다시 도구를 바꿨거나 쓸 수 없는 상태면 그만둔다
        if (_translator != translator || Status != EngineStatus.Ready)
            return;

        var targets = lines
            .Where(l => !l.IsOwnText && ContainsJapanese(l.Message))
            .TakeLast(count)
            .Where(l => !l.HasTranslationFrom(translator.ShortName));
        foreach (var line in targets)
            Start(line, translator);
    }

    /// <summary>번역을 시작할 때의 도구로 이름표를 붙이고 번역한다 — 이름표와 실제로 번역한 도구가 어긋나지 않게</summary>
    private void Start(ChatLine line, ITranslator translator) =>
        _ = TranslateAsync(line.Message, line.AddTranslation(translator.ShortName), translator, _cts.Token);

    /// <summary>
    /// 보낼 한국어를 일본어로 번역한다. 사용자가 기다리는 중이라 받은 채팅의 동시 실행 제한을 거치지 않는다.
    /// </summary>
    public Task<TranslationResult> TranslateOutgoingAsync(string korean) =>
        _translator.TranslateAsync(korean, TranslationDirection.KoToJa, _cts.Token);

    /// <summary>
    /// 보낼 일본어를 다시 한국어로 옮겨 뜻을 확인하게 한다. 일본어를 못 읽는 사용자가 보내기 전에 확인하는 용도라
    /// 한국어 원문은 넘기지 않고 일본어만 따로 번역한다 — 같은 호출에서 받으면 원문을 되풀이하기 쉬워 확인이 안 된다.
    /// </summary>
    public Task<TranslationResult> TranslateBackAsync(string japanese) =>
        _translator.TranslateAsync(japanese, TranslationDirection.JaToKo, _cts.Token);

    /// <summary>보낸 일본어와 그 한국어 원문을 기억한다. 받은 채팅에 같은 글이 오면 한 번 쓰고 지운다.</summary>
    public void RememberSent(string japanese, string korean)
    {
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

    private async Task CheckLoginAsync(ITranslator translator, int id)
    {
        EngineStatus status;
        string? detail = null;
        try
        {
            status = await translator.IsLoggedInAsync(_cts.Token) ? EngineStatus.Ready : EngineStatus.NotLoggedIn;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (TranslationException e)
        {
            status = EngineStatus.Unavailable;
            detail = e.Message;
        }

        if (id != _loginCheckId)
            return; // 그사이 엔진이 바뀌었다
        StatusDetail = detail;
        Status = status;
    }

    private async Task TranslateAsync(string message, LineTranslation target, ITranslator translator, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                var result = await translator.TranslateAsync(message, TranslationDirection.JaToKo, ct);
                target.Complete(TranslationState.Done, result.Text);
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
            target.Complete(TranslationState.Failed, e.Message);
            // 채팅 내용이 로그 파일에 남지 않게 원인 메시지는 창에만 보여 준다
            Plugin.Log.Warning("받은 채팅 번역 실패");
        }
    }

    /// <summary>가나 또는 한자가 있으면 일본어로 본다. 한국어·영어만 있는 줄은 번역하지 않아 사용량을 아낀다.</summary>
    private static bool ContainsJapanese(string text) => JapaneseChars().IsMatch(text);

    [GeneratedRegex(@"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ]")]
    private static partial Regex JapaneseChars();
}
