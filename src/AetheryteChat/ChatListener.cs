using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace AetheryteChat;

internal enum TranslationState
{
    /// <summary>번역하지 않는 줄 (일본어가 없음, 자동 번역 꺼짐 등)</summary>
    None,
    Pending,
    Done,
    Failed,
}

/// <summary>
/// 받은 채팅 한 줄. 번역 결과는 백그라운드 스레드가 채우고 UI 스레드가 읽는다 —
/// 상태를 마지막에 써서 상태가 Done 이면 번역문도 보이게 한다.
/// </summary>
internal sealed class ChatLine(DateTime time, XivChatType channel, string sender, string message)
{
    public DateTime Time { get; } = time;
    public XivChatType Channel { get; } = channel;
    public string Sender { get; } = sender;
    public string Message { get; } = message;

    private volatile TranslationState _state;
    public TranslationState State => _state;

    /// <summary>Done 이면 번역문, Failed 면 실패 이유</summary>
    public string? Result { get; private set; }

    public void MarkPending() => _state = TranslationState.Pending;

    public void Complete(TranslationState state, string result)
    {
        Result = result;
        _state = state;
    }
}

/// <summary>
/// 플레이어 대화 채널만 받아 최근 줄을 메모리에 둔다. 다른 플레이어의 대화이므로 파일에 남기지 않는다 (DESIGN 7절).
/// </summary>
internal sealed class ChatListener : IDisposable
{
    private const int MaxLines = 200;

    private static readonly HashSet<XivChatType> PlayerChannels =
    [
        XivChatType.Say, XivChatType.Shout, XivChatType.Yell,
        XivChatType.Party, XivChatType.CrossParty, XivChatType.Alliance,
        XivChatType.FreeCompany, XivChatType.NoviceNetwork,
        XivChatType.TellIncoming, XivChatType.TellOutgoing,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
        XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
        // 보내기 시험을 /e 로 하므로 Echo 도 받는다
        XivChatType.Echo,
    ];

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
