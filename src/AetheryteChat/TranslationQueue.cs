using System.Text.RegularExpressions;
using AetheryteChat.Translation;
using Dalamud.Game.Text;

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
/// 아직 한 줄씩 번역한다 — 묶음 번역·같은 문장 캐시·실패했을 때의 예비 엔진은 붙이지 않았다.
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

    // 보낸 글은 보통 1초 안에 받은 채팅으로 돌아온다. 돌아오지 않은 기록(서버가 받지 않음 등)이 한참 뒤
    // 남이 쓴 같은 문장에 붙지 않게, 이 시간이 지나면 버린다
    private static readonly TimeSpan SentLifetime = TimeSpan.FromSeconds(60);
    private const int MaxRememberedSent = 20;
    private readonly List<SentText> _sent = []; // 보낸 순서. 같은 문장을 두 번 보내면 두 개가 따로 남는다

    // 지금 엔진의 로그인 확인 — 엔진을 바꾼 직후의 다시 번역이 확인이 끝나기를 기다린다
    private Task _loginCheck;

    // 설정의 '번역할 채널' — 끈 채널의 대화는 번역하지 않는다 (채팅 기록에는 그대로 나온다)
    private readonly Func<XivChatType, bool> _isChannelEnabled;

    // 지금 설정으로 엔진을 만든다 — 설정을 바꾸거나 '다시 확인' 할 때 새로 만든다
    private readonly Func<ITranslator> _createTranslator;

    // 준비가 안 됐을 때(로그인 전·로그인이 풀림·확인 중·Ollama 꺼짐) 들어와 번역하지 못한 최근 줄.
    // 준비되면 이어서 번역한다 — 로그인한 뒤 그 전에 받은 대화가 번역 없이 남지 않게
    private const int MaxBacklog = 10;
    private readonly List<ChatLine> _backlog = [];

    public TranslationQueue(Func<ITranslator> createTranslator, Func<XivChatType, bool> isChannelEnabled)
    {
        _createTranslator = createTranslator;
        _translator = createTranslator();
        _isChannelEnabled = isChannelEnabled;
        _loginCheck = CheckLoginAsync(_translator, ++_loginCheckId);
    }

    public EngineStatus Status { get; private set; } = EngineStatus.Checking;
    public string? StatusDetail { get; private set; }
    public bool AutoTranslate { get; set; } = true;
    public string EngineName => _translator.Name;
    public string NotReadyHint => _translator.NotReadyHint;

    /// <summary>지금 도구가 플러그인에서 로그인 창을 띄울 수 있는지 (Claude·Codex CLI)</summary>
    public bool CanLogin => _translator.CanLogin;

    /// <summary>로그인 창이 떠 있다 — 창이 닫히면 다시 확인한다</summary>
    public bool LoggingIn { get; private set; }

    /// <summary>새 콘솔 창에서 로그인 명령을 띄운다(브라우저가 열린다). 창이 닫히면 상태를 다시 확인한다. UI 스레드에서 부른다</summary>
    public void StartLogin()
    {
        var translator = _translator;
        if (LoggingIn || !translator.CanLogin)
            return;
        LoggingIn = true;
        _ = LoginAsync(translator);
    }

    private async Task LoginAsync(ITranslator translator)
    {
        string? error = null;
        try
        {
            await translator.LoginAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // 플러그인을 내릴 때. 로그인 창은 그대로 둔다
        }
        catch (TranslationException e)
        {
            error = e.Message;
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            LoggingIn = false;
            if (_translator != translator)
                return; // 그사이 도구를 바꿨다
            if (error is null)
            {
                Reload();
                return;
            }
            Status = EngineStatus.Unavailable;
            StatusDetail = error;
        });
    }

    /// <summary>
    /// 지금 설정으로 엔진을 다시 만들고 상태를 다시 확인한다. 엔진·엔진 설정을 바꿨을 때,
    /// 그리고 터미널에서 로그인하거나 Ollama 를 켠 뒤 '다시 확인' 할 때 부른다 (실행 파일도 다시 찾는다). UI 스레드에서 부른다.
    /// </summary>
    public void Reload()
    {
        var translator = _createTranslator();
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

        if (!AutoTranslate || !_isChannelEnabled(line.Channel) || !ContainsJapanese(line.Message))
            return;

        if (Status != EngineStatus.Ready)
        {
            AddBacklog(line); // 준비되면 이어서 번역한다
            return;
        }
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
            .Where(l => !l.IsOwnText && _isChannelEnabled(l.Channel) && ContainsJapanese(l.Message))
            .TakeLast(count)
            .Where(l => !l.HasTranslationFrom(translator.ShortName));
        foreach (var line in targets)
            Start(line, translator);
    }

    /// <summary>번역을 시작할 때의 도구로 이름표를 붙이고 번역한다 — 이름표와 실제로 번역한 도구가 어긋나지 않게</summary>
    private void Start(ChatLine line, ITranslator translator) =>
        _ = TranslateAsync(line, line.AddTranslation(translator.ShortName), translator, _cts.Token);

    private void AddBacklog(ChatLine line)
    {
        lock (_backlog)
        {
            _backlog.Remove(line);
            _backlog.Add(line);
            if (_backlog.Count > MaxBacklog)
                _backlog.RemoveAt(0);
        }
    }

    /// <summary>
    /// 준비된 뒤 밀린 줄을 번역한다. 자동 번역을 꺼 두었으면 그대로 둔다.
    /// 기다리는 사이 번역을 끈 채널의 줄은 번역하지 않고 버린다 — 끈 채널의 대화가 번역 도구로 나가지 않게
    /// </summary>
    private void FlushBacklog(ITranslator translator)
    {
        if (!AutoTranslate)
            return;
        List<ChatLine> lines;
        lock (_backlog)
        {
            lines = [.. _backlog];
            _backlog.Clear();
        }
        foreach (var line in lines.Where(l => _isChannelEnabled(l.Channel) && !l.HasTranslationFrom(translator.ShortName)))
            Start(line, translator);
    }

    /// <summary>번역하지 못한 줄을 실패로 남기지 않고 빼 두었다가, 준비되면 다시 번역한다</summary>
    private void Defer(ChatLine line, LineTranslation target)
    {
        line.RemoveTranslation(target);
        AddBacklog(line);
    }

    /// <summary>
    /// 쓰는 도중 로그인이 풀렸다(토큰 만료·로그아웃). 상태를 '로그인 안 됨' 으로 되돌려 번역을 멈추고 로그인 버튼을 보이게 한다
    /// </summary>
    private void MarkLoggedOut(ITranslator translator)
    {
        if (translator != _translator)
            return; // 이미 다른 도구로 바꿨다
        StatusDetail = null;
        Status = EngineStatus.NotLoggedIn;
    }

    /// <summary>
    /// 보낼 한국어를 일본어로 번역한다. 사용자가 기다리는 중이라 받은 채팅의 동시 실행 제한을 거치지 않는다.
    /// </summary>
    public Task<TranslationResult> TranslateOutgoingAsync(string korean) =>
        WatchAuthAsync(_translator, t => t.TranslateAsync(korean, TranslationDirection.KoToJa, _cts.Token));

    /// <summary>
    /// 보낼 일본어를 다시 한국어로 옮겨 뜻을 확인하게 한다. 일본어를 못 읽는 사용자가 보내기 전에 확인하는 용도라
    /// 한국어 원문은 넘기지 않고 일본어만 따로 번역한다 — 같은 호출에서 받으면 원문을 되풀이하기 쉬워 확인이 안 된다.
    /// </summary>
    public Task<TranslationResult> TranslateBackAsync(string japanese) =>
        WatchAuthAsync(_translator, t => t.TranslateAsync(japanese, TranslationDirection.JaToKo, _cts.Token));

    /// <summary>보내기 쪽 번역이 로그인 문제로 실패하면 상태를 되돌리고, 무엇을 하면 되는지 알려 준다</summary>
    private async Task<TranslationResult> WatchAuthAsync(
        ITranslator translator, Func<ITranslator, Task<TranslationResult>> translate)
    {
        try
        {
            return await translate(translator);
        }
        catch (TranslationException e) when (e.IsAuthError)
        {
            MarkLoggedOut(translator);
            throw new TranslationException("로그인이 풀렸습니다 — 상태 줄의 '로그인'을 눌러 다시 로그인해 주세요.", e)
            {
                IsAuthError = true,
            };
        }
    }

    /// <summary>보낸 글 하나의 기록</summary>
    public sealed record SentText(string Japanese, string Korean, DateTime Expires);

    /// <summary>
    /// 보낸 일본어와 그 한국어 원문을 기억한다. 받은 채팅에 같은 글이 오면 한 번 쓰고 지운다.
    /// 게임에 넘기기 전에 부른다 — 돌아오는 글이 기록보다 먼저 오지 않게. 보내지 못했으면 돌려받은 값으로 <see cref="ForgetSent"/> 한다
    /// </summary>
    public SentText RememberSent(string japanese, string korean)
    {
        var sent = new SentText(japanese, korean, DateTime.UtcNow + SentLifetime);
        lock (_sent)
        {
            _sent.Add(sent);
            if (_sent.Count > MaxRememberedSent)
                _sent.RemoveAt(0);
        }
        return sent;
    }

    /// <summary>보내지 못한 글의 기록을 지운다 — 나중에 남이 쓴 같은 문장을 내 글로 보지 않게</summary>
    public void ForgetSent(SentText sent)
    {
        lock (_sent)
            _sent.Remove(sent);
    }

    private string? TakeSent(string message)
    {
        var now = DateTime.UtcNow;
        lock (_sent)
        {
            _sent.RemoveAll(s => s.Expires < now);
            var index = _sent.FindIndex(s => s.Japanese == message);
            if (index < 0)
                return null;
            var korean = _sent[index].Korean;
            _sent.RemoveAt(index);
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

        // 이 확인을 기다리는 '도구를 바꾼 뒤 다시 번역' 보다 먼저 밀린 줄을 시작한다 — 같은 줄을 두 번 번역하지 않게
        if (status == EngineStatus.Ready)
            FlushBacklog(translator);
    }

    private async Task TranslateAsync(ChatLine line, LineTranslation target, ITranslator translator, CancellationToken ct)
    {
        try
        {
            await _slots.WaitAsync(ct);
            try
            {
                // 기다리는 사이 로그인이 풀린 것을 알았으면 CLI 를 부르지 않고 미뤄 둔다 (Codex 는 실패하는 데만 25초쯤 걸린다)
                if (translator == _translator && Status != EngineStatus.Ready)
                {
                    Defer(line, target);
                    return;
                }
                // 기다리는 사이 이 채널의 번역을 껐으면 부르지 않는다. 이미 도구에 넘긴 줄은 그대로 끝낸다
                if (!_isChannelEnabled(line.Channel))
                {
                    line.RemoveTranslation(target);
                    return;
                }
                var result = await translator.TranslateAsync(line.Message, TranslationDirection.JaToKo, ct);
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
        catch (TranslationException e) when (e.IsAuthError)
        {
            Defer(line, target);
            MarkLoggedOut(translator);
            Plugin.Log.Warning("받은 채팅 번역 실패: 로그인이 안 됐거나 풀림");
        }
        catch (TranslationException e)
        {
            target.Complete(TranslationState.Failed, e.Message);
            // 채팅 내용이 로그 파일에 남지 않게 원인 메시지는 창에만 보여 준다
            Plugin.Log.Warning("받은 채팅 번역 실패");
        }
        catch (Exception e)
        {
            // 예상하지 못한 실패도 '번역 중' 으로 남기지 않는다. 예외 메시지에 응답 내용이 섞일 수 있어 로그에는 종류만 남긴다
            target.Complete(TranslationState.Failed, $"알 수 없는 오류 ({e.GetType().Name})");
            Plugin.Log.Warning($"받은 채팅 번역 실패: 예상하지 못한 오류 {e.GetType().Name}");
        }
    }

    /// <summary>가나 또는 한자가 있으면 일본어로 본다. 한국어·영어만 있는 줄은 번역하지 않아 사용량을 아낀다.</summary>
    private static bool ContainsJapanese(string text) => JapaneseChars().IsMatch(text);

    [GeneratedRegex(@"[぀-ヿ㐀-䶿一-鿿ｦ-ﾟ]")]
    private static partial Regex JapaneseChars();
}
