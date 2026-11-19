using Dalamud.Game.Text;

namespace AetheryteChat;

/// <summary>설정의 '번역할 채널' 묶음. 설정 파일에는 이름으로 저장된다</summary>
public enum TranslateChannelGroup
{
    Say,
    Yell,
    Shout,
    Party,
    Alliance,
    FreeCompany,
    Linkshell,
    CrossLinkshell,
    NoviceNetwork,
    Tell,
    Echo,
}

/// <summary>
/// 받은 채팅 가운데 어떤 채널을 번역할지. 끈 채널의 대화도 채팅 기록에는 그대로 나오고 번역만 하지 않는다.
/// </summary>
internal static class TranslateChannels
{
    public static readonly (TranslateChannelGroup Group, string Label, XivChatType[] Types)[] Groups =
    [
        (TranslateChannelGroup.Say, "말하기", [XivChatType.Say]),
        (TranslateChannelGroup.Yell, "떠들기", [XivChatType.Yell]),
        (TranslateChannelGroup.Shout, "외치기", [XivChatType.Shout]),
        (TranslateChannelGroup.Party, "파티", [XivChatType.Party, XivChatType.CrossParty]),
        (TranslateChannelGroup.Alliance, "연합 파티", [XivChatType.Alliance]),
        (TranslateChannelGroup.FreeCompany, "자유부대", [XivChatType.FreeCompany]),
        (TranslateChannelGroup.Linkshell, "링크셸",
        [
            XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
            XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        ]),
        (TranslateChannelGroup.CrossLinkshell, "서버 초월 링크셸",
        [
            XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
            XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
        ]),
        (TranslateChannelGroup.NoviceNetwork, "초보자 채널", [XivChatType.NoviceNetwork]),
        (TranslateChannelGroup.Tell, "귓속말", [XivChatType.TellIncoming, XivChatType.TellOutgoing]),
        (TranslateChannelGroup.Echo, "혼잣말", [XivChatType.Echo]),
    ];

    /// <summary>묶음에 든 모든 채널 = 받은 채팅이 받아들이는 플레이어 대화 채널 (Echo 는 보내기 시험용)</summary>
    public static readonly HashSet<XivChatType> AllTypes = Groups.SelectMany(g => g.Types).ToHashSet();

    private static readonly Dictionary<XivChatType, TranslateChannelGroup> GroupOf =
        Groups.SelectMany(g => g.Types.Select(t => (t, g.Group))).ToDictionary(x => x.t, x => x.Group);

    /// <summary>
    /// 주변 채널 — 근처·서버의 모르는 사람이 쓰는 채널. 번역이 밀리면 이 채널의 줄부터 건너뛰고,
    /// 파티·귓속말처럼 실제로 주고받는 대화 채널을 먼저 번역한다
    /// </summary>
    public static bool IsAmbient(XivChatType channel) =>
        channel is XivChatType.Say or XivChatType.Yell or XivChatType.Shout or XivChatType.NoviceNetwork;

    /// <summary>채널이 속한 묶음. 묶음에 없는 채널이면 null</summary>
    public static TranslateChannelGroup? GroupFor(XivChatType channel) =>
        GroupOf.TryGetValue(channel, out var group) ? group : null;

    /// <summary>이 채널의 대화를 번역할지. 묶음에 없는 채널은 번역하지 않는다</summary>
    public static bool IsEnabled(Configuration config, XivChatType channel) =>
        GroupOf.TryGetValue(channel, out var group) && !config.DisabledTranslateChannels.Contains(group);
}
