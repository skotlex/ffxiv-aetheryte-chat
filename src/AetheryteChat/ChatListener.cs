using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace AetheryteChat;

internal sealed record ReceivedChat(DateTime Time, XivChatType Channel, string Sender, string Message);

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
    private readonly Lock _lock = new();
    private readonly LinkedList<ReceivedChat> _lines = new();

    public ChatListener(IChatGui chatGui)
    {
        _chatGui = chatGui;
        _chatGui.ChatMessageUnhandled += OnChatMessage;
    }

    public void Dispose() => _chatGui.ChatMessageUnhandled -= OnChatMessage;

    public IReadOnlyList<ReceivedChat> Snapshot()
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

        var line = new ReceivedChat(DateTime.Now, message.LogKind, message.Sender.TextValue, message.Message.TextValue);
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines)
                _lines.RemoveFirst();
        }
    }
}
