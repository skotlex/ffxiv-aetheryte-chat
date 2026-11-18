using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AetheryteChat;

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

    /// <summary>채널 명령(<c>/p</c>, <c>/t 이름@서버</c> …)과 본문을 이어 실제로 입력창에 들어갈 한 줄을 만든다.</summary>
    /// <param name="prefix">빈 문자열이면 게임 입력창의 현재 채널로 간다 (자동)</param>
    public static string BuildLine(string prefix, string text) =>
        prefix.Length == 0 ? text.Trim() : $"{prefix} {text.Trim()}";

    /// <summary>보낼 수 없는 글이면 이유를, 보낼 수 있으면 null 을 돌려준다.</summary>
    public static string? Validate(string prefix, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "빈 글은 보낼 수 없습니다.";
        if (text.Contains('\n') || text.Contains('\r'))
            return "줄바꿈은 보낼 수 없습니다.";
        if (text.TrimStart().StartsWith('/'))
            return "명령어(/…)는 보낼 수 없습니다. 채널은 목록에서 골라 주세요.";

        var bytes = Encoding.UTF8.GetByteCount(BuildLine(prefix, text));
        if (bytes > MaxBytes)
            return $"너무 깁니다 (채널 명령 포함 {bytes}바이트, 한도 {MaxBytes}바이트).";
        return null;
    }

    /// <summary>게임 스레드에서 호출해야 한다 (<c>IFramework.RunOnFrameworkThread</c>).</summary>
    public static unsafe void Send(string prefix, string text)
    {
        if (Validate(prefix, text) is { } error)
            throw new InvalidOperationException(error);

        var line = BuildLine(prefix, text);
        var str = Utf8String.FromString(line);
        try
        {
            str->SanitizeString(Allowed, null);
            var sanitized = str->ToString();
            if (sanitized != line)
                throw new InvalidOperationException($"게임이 허용하지 않는 글자가 들어 있습니다. 정리 후: {sanitized}");

            UIModule.Instance()->ProcessChatBoxEntry(str, IntPtr.Zero, false);
        }
        finally
        {
            str->Dtor(true);
        }
    }
}
