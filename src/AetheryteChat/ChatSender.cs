using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AetheryteChat;

internal enum ChatChannel
{
    Echo,
    Say,
    Party,
    FreeCompany,
}

/// <summary>
/// 게임 채팅으로 글을 보낸다. Dalamud 에는 보내기 API 가 없어, 채팅 입력창에서 Enter 를 누를 때와 같은
/// <c>UIModule.ProcessChatBoxEntry</c> 를 부른다. 사용자가 버튼을 눌렀을 때만 쓴다 (DESIGN 5절).
/// </summary>
internal static class ChatSender
{
    // 게임 채팅 입력창의 바이트 한도
    private const int MaxBytes = 500;

    // 글자·숫자·기호·CJK·자동 번역 문구 등 채팅 입력창이 허용하는 것만 남긴다
    private const AllowedEntities Allowed =
        AllowedEntities.UppercaseLetters | AllowedEntities.LowercaseLetters | AllowedEntities.Numbers |
        AllowedEntities.SpecialCharacters | AllowedEntities.CharacterList | AllowedEntities.OtherCharacters |
        AllowedEntities.Payloads | AllowedEntities.Unknown9 | AllowedEntities.CJK;

    public static string Prefix(ChatChannel channel) => channel switch
    {
        ChatChannel.Echo => "/e",
        ChatChannel.Say => "/s",
        ChatChannel.Party => "/p",
        ChatChannel.FreeCompany => "/fc",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    /// <summary>보낼 수 없는 글이면 이유를, 보낼 수 있으면 null 을 돌려준다.</summary>
    public static string? Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "빈 글은 보낼 수 없다.";
        if (text.Contains('\n') || text.Contains('\r'))
            return "줄바꿈은 보낼 수 없다.";
        if (text.TrimStart().StartsWith('/'))
            return "명령어(/…)는 보내지 않는다. 채널은 목록에서 고른다.";
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes - 4)
            return $"너무 길다 ({Encoding.UTF8.GetByteCount(text)}바이트, 한도 약 {MaxBytes}바이트).";
        return null;
    }

    /// <summary>게임 스레드에서 호출해야 한다 (<c>IFramework.RunOnFrameworkThread</c>).</summary>
    public static unsafe void Send(ChatChannel channel, string text)
    {
        if (Validate(text) is { } error)
            throw new InvalidOperationException(error);

        var line = $"{Prefix(channel)} {text.Trim()}";
        var str = Utf8String.FromString(line);
        try
        {
            str->SanitizeString(Allowed, null);
            var sanitized = str->ToString();
            if (sanitized != line)
                throw new InvalidOperationException($"게임이 허용하지 않는 글자가 들어 있다. 정리 후: {sanitized}");

            UIModule.Instance()->ProcessChatBoxEntry(str, IntPtr.Zero, false);
        }
        finally
        {
            str->Dtor(true);
        }
    }
}
