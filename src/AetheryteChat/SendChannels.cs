using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Text.ReadOnly;

namespace AetheryteChat;

/// <param name="Key">선택을 기억하는 키. 목록은 매번 새로 만들어지므로 순서가 아니라 키로 찾는다</param>
/// <param name="Label">게임 채널 목록과 같은 이름</param>
/// <param name="Prefix">채널 명령. 빈 문자열이면 게임 입력창의 현재 채널로 간다</param>
internal sealed record SendChannel(string Key, string Label, string Prefix)
{
    public const string AutoKey = "auto";
    public const string TellKey = "tell";

    public bool NeedsTellTarget => Key == TellKey;
}

/// <summary>
/// 보내기 채널 목록. 게임 채팅 입력창의 채널 목록처럼 가입한 링크셸만 이름과 함께 보여 준다.
/// 게임 메모리를 읽으므로 게임 스레드(ImGui Draw 포함)에서 부른다.
/// </summary>
internal static unsafe class SendChannels
{
    private const int LinkshellSlots = 8;

    public static List<SendChannel> Build()
    {
        var list = new List<SendChannel>
        {
            new(SendChannel.AutoKey, $"자동 (지금: {CurrentChannelLabel() ?? "알 수 없음"})", ""),
            new(SendChannel.TellKey, "귓속말", "/t"),
            new("say", "말하기", "/s"),
            new("party", "파티", "/p"),
            new("alliance", "연합 파티", "/a"),
            new("yell", "떠들기", "/y"),
            new("shout", "외치기", "/sh"),
            new("fc", "자유부대", "/fc"),
        };

        var cwls = InfoProxyCrossWorldLinkshell.Instance();
        if (cwls != null)
        {
            for (var i = 0u; i < LinkshellSlots; i++)
            {
                var name = cwls->GetCrossworldLinkshellName(i);
                if (name != null && name->ToString() is { Length: > 0 } n)
                    list.Add(new($"cwls{i + 1}", $"서버 초월 링크셸[{i + 1}]: {n}", $"/cwl{i + 1}"));
            }
        }

        var chat = InfoProxyChat.Instance();
        if (chat != null)
        {
            for (var i = 0u; i < LinkshellSlots; i++)
            {
                var name = chat->GetLinkShellName(i);
                if (name.HasValue && name.ToString() is { Length: > 0 } n)
                    list.Add(new($"ls{i + 1}", $"링크셸{i + 1}: {n}", $"/l{i + 1}"));
            }
        }

        // 게임 목록에는 없지만 시험할 때 남에게 보내지 않으려고 둔다
        list.Add(new("echo", "혼잣말 (나에게만 보임)", "/e"));
        return list;
    }

    /// <summary>게임 채팅 입력창 왼쪽에 보이는 현재 채널 이름 (예: 파티, 링크셸1: …)</summary>
    private static string? CurrentChannelLabel()
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return null;

        // 라벨은 SeString 이라 매크로 바이트를 걷고 글자만 뽑은 뒤, 앞에 붙는 게임 기호(U+E01E 등)를 걷어 낸다
        var text = new ReadOnlySeStringSpan(agent->ChannelLabel.AsSpan()).ExtractText();
        var label = new string(text.Where(c => !char.IsControl(c) && c is < '' or > '').ToArray()).Trim();
        return label.Length > 0 ? label : null;
    }
}
