using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace AetheryteChat;

internal enum TranslationState
{
    /// <summary>아직 번역하지 않음 (보내기 탭의 뜻 확인에서 쓴다)</summary>
    None,
    Pending,
    Done,
    Failed,
}

/// <summary>
/// 한 줄에 붙은 번역 하나. 결과는 백그라운드 스레드가 채우고 UI 스레드가 읽는다 —
/// 상태를 마지막에 써서 상태가 Done 이면 번역문도 보이게 한다.
/// </summary>
/// <param name="Engine">번역한 모델 이름 (ITranslator.ShortName). 같은 줄의 여러 번역을 구분한다</param>
/// <param name="IsOwnText">내가 보낸 글이 돌아온 것 — 번역이 아니라 보낼 때 쓴 한국어 원문</param>
internal sealed class LineTranslation(string engine, bool isOwnText = false)
{
    public string Engine { get; } = engine;
    public bool IsOwnText { get; } = isOwnText;

    private volatile TranslationState _state = TranslationState.Pending;
    public TranslationState State => _state;

    /// <summary>Done 이면 번역문, Failed 면 실패 이유</summary>
    public string? Result { get; private set; }

    public void Complete(TranslationState state, string result)
    {
        Result = result;
        _state = state;
    }
}

/// <summary>
/// 받은 채팅 한 줄. 번역 도구를 바꾸면 새 도구의 번역이 더해지고, 이전 번역은 그대로 남는다.
/// </summary>
internal sealed class ChatLine(DateTime time, XivChatType channel, string sender, string message)
{
    public DateTime Time { get; } = time;
    public XivChatType Channel { get; } = channel;
    public string Sender { get; } = sender;
    public string Message { get; } = message;

    private readonly Lock _lock = new();
    private LineTranslation[] _translations = []; // 더할 때마다 새 배열로 바꿔 끼워, 읽는 쪽은 잠그지 않는다

    public IReadOnlyList<LineTranslation> Translations => Volatile.Read(ref _translations);

    /// <summary>내가 보낸 글이 돌아온 줄인지 (다시 번역하지 않는다)</summary>
    public bool IsOwnText => Translations.Any(t => t.IsOwnText);

    /// <summary>이 모델로 이미 번역했거나 번역 중인지 (실패한 것은 빼고)</summary>
    public bool HasTranslationFrom(string engine) =>
        Translations.Any(t => t.Engine == engine && t.State != TranslationState.Failed);

    public LineTranslation AddTranslation(string engine, bool isOwnText = false)
    {
        var translation = new LineTranslation(engine, isOwnText);
        lock (_lock)
            Volatile.Write(ref _translations, [.. _translations, translation]);
        return translation;
    }

    /// <summary>
    /// 번역 하나를 뺀다. 로그인이 풀려 번역하지 못한 줄은 실패로 남기지 않고 빼 두었다가, 다시 로그인하면 번역한다
    /// </summary>
    public void RemoveTranslation(LineTranslation translation)
    {
        lock (_lock)
            Volatile.Write(ref _translations, [.. _translations.Where(t => t != translation)]);
    }
}

/// <summary>
/// 플레이어 대화 채널만 받아 최근 줄을 메모리에 둔다. 다른 플레이어의 대화이므로 파일에 남기지 않는다.
/// </summary>
internal sealed class ChatListener : IDisposable
{
    private const int MaxLines = 200;

    // 받는 채널은 설정의 '번역할 채널' 묶음과 같은 목록을 쓴다 — 둘이 어긋나 받기만 하고 설정에 없는 채널이 생기지 않게
    private static readonly HashSet<XivChatType> PlayerChannels = TranslateChannels.AllTypes;

    private readonly IChatGui _chatGui;
    private readonly Action<ChatLine> _onLine;
    private readonly Lock _lock = new();
    private readonly LinkedList<ChatLine> _lines = new();

    /// <param name="onLine">새 줄이 들어올 때 게임 스레드에서 불린다. 오래 걸리는 일을 하면 안 된다.</param>
    public ChatListener(IChatGui chatGui, Action<ChatLine> onLine)
    {
        _chatGui = chatGui;
        _onLine = onLine;
        _chatGui.ChatMessageUnhandled += OnChatMessage;
    }

    public void Dispose() => _chatGui.ChatMessageUnhandled -= OnChatMessage;

    /// <summary>마지막으로 귓속말을 보낸 사람 ('이름' 또는 '이름@서버' — /t 에 그대로 쓸 수 있는 형식)</summary>
    public string? LastTellSender { get; private set; }

    public IReadOnlyList<ChatLine> Snapshot()
    {
        lock (_lock)
            return [.. _lines];
    }

    public void Clear()
    {
        lock (_lock)
            _lines.Clear();
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (!PlayerChannels.Contains(message.LogKind))
            return;

        var line = new ChatLine(DateTime.Now, message.LogKind, FormatSender(message.Sender), message.Message.TextValue);
        if (message.LogKind == XivChatType.TellIncoming)
            LastTellSender = line.Sender;
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveFirst();
        }
        _onLine(line);
    }

    /// <summary>
    /// 발신자 SeString 의 TextValue 는 이름과 서버 이름이 붙어 나온다 (게임은 그 사이에 서버 아이콘을 그린다).
    /// PlayerPayload 에서 이름·서버를 꺼내, 게임이 서버를 보여 준 경우(다른 서버 플레이어)만 '이름@서버' 로 만든다.
    /// 이름 앞의 기호(파티 번호 등)는 그대로 둔다.
    /// </summary>
    private static string FormatSender(SeString sender)
    {
        var text = sender.TextValue;
        var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (player is null)
            return text;

        var name = player.PlayerName;
        var nameAt = text.IndexOf(name, StringComparison.Ordinal);
        if (nameAt < 0)
            return text;

        var prefix = text[..nameAt];
        var world = player.World.ValueNullable?.Name.ExtractText() ?? "";
        var worldShown = world.Length > 0 && text.AsSpan(nameAt + name.Length).Trim().EndsWith(world, StringComparison.Ordinal);
        return worldShown ? $"{prefix}{name}@{world}" : $"{prefix}{name}";
    }
}
