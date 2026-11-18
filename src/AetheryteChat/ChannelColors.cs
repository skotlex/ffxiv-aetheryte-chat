using System.Numerics;
using Dalamud.Game.Config;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace AetheryteChat;

/// <summary>
/// 게임 캐릭터 설정(로그 창 › 색상)의 채널별 글자색. 값은 0x??RRGGBB 이고 0 이면 설정이 없다.
/// 게임에서 색을 바꾸면 따라가도록 1초마다 다시 읽는다. 게임 스레드(ImGui Draw 포함)에서 부른다.
/// </summary>
internal sealed class ChannelColors(IGameConfig gameConfig)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<XivChatType, UiConfigOption> Options = new()
    {
        [XivChatType.Say] = UiConfigOption.ColorSay,
        [XivChatType.Shout] = UiConfigOption.ColorShout,
        [XivChatType.Yell] = UiConfigOption.ColorYell,
        [XivChatType.TellIncoming] = UiConfigOption.ColorTell,
        [XivChatType.TellOutgoing] = UiConfigOption.ColorTell,
        [XivChatType.Party] = UiConfigOption.ColorParty,
        [XivChatType.CrossParty] = UiConfigOption.ColorParty,
        [XivChatType.Alliance] = UiConfigOption.ColorAlliance,
        [XivChatType.FreeCompany] = UiConfigOption.ColorFCompany,
        [XivChatType.NoviceNetwork] = UiConfigOption.ColorBeginner,
        [XivChatType.Echo] = UiConfigOption.ColorEcho,
        [XivChatType.Ls1] = UiConfigOption.ColorLS1,
        [XivChatType.Ls2] = UiConfigOption.ColorLS2,
        [XivChatType.Ls3] = UiConfigOption.ColorLS3,
        [XivChatType.Ls4] = UiConfigOption.ColorLS4,
        [XivChatType.Ls5] = UiConfigOption.ColorLS5,
        [XivChatType.Ls6] = UiConfigOption.ColorLS6,
        [XivChatType.Ls7] = UiConfigOption.ColorLS7,
        [XivChatType.Ls8] = UiConfigOption.ColorLS8,
        [XivChatType.CrossLinkShell1] = UiConfigOption.ColorCWLS,
        [XivChatType.CrossLinkShell2] = UiConfigOption.ColorCWLS2,
        [XivChatType.CrossLinkShell3] = UiConfigOption.ColorCWLS3,
        [XivChatType.CrossLinkShell4] = UiConfigOption.ColorCWLS4,
        [XivChatType.CrossLinkShell5] = UiConfigOption.ColorCWLS5,
        [XivChatType.CrossLinkShell6] = UiConfigOption.ColorCWLS6,
        [XivChatType.CrossLinkShell7] = UiConfigOption.ColorCWLS7,
        [XivChatType.CrossLinkShell8] = UiConfigOption.ColorCWLS8,
    };

    private readonly Dictionary<XivChatType, Vector4?> _cache = new();
    private DateTime _loadedAt;

    /// <summary>채널 글자색. 설정이 없거나 모르는 채널이면 null (기본 글자색으로 그린다)</summary>
    public Vector4? Get(XivChatType channel)
    {
        var now = DateTime.UtcNow;
        if (now - _loadedAt > RefreshInterval)
        {
            _cache.Clear();
            _loadedAt = now;
        }

        if (!_cache.TryGetValue(channel, out var color))
            _cache[channel] = color = Read(channel);
        return color;
    }

    private Vector4? Read(XivChatType channel)
    {
        if (!Options.TryGetValue(channel, out var option) || !gameConfig.TryGet(option, out uint raw))
            return null;

        var rgb = raw & 0xFFFFFF;
        if (rgb == 0)
            return null;

        return new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
    }
}
