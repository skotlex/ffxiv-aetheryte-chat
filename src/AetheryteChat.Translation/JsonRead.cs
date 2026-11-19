using System.Text.Json;

namespace AetheryteChat.Translation;

/// <summary>
/// 외부 응답의 JSON 값을 자료형을 확인하며 꺼낸다. 객체가 아닌 값에 TryGetProperty 를 부르거나 문자열이 아닌 값에
/// GetString 을 부르면 InvalidOperationException 이 나서 번역 실패로 처리되지 않으므로, 모양이 다르면 null 을 돌려준다.
/// </summary>
internal static class JsonRead
{
    /// <summary>객체이고 그 이름의 속성이 있으면 그 값</summary>
    public static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    /// <summary>그 이름의 속성이 문자열이면 그 문자열</summary>
    public static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>그 이름의 속성이 정수면 그 값</summary>
    public static long? Int64(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var n) ? n : null;

    /// <summary>JSON 문서로 읽는다. 문법이 틀리면 원인을 담은 TranslationException 으로 바꾼다</summary>
    public static JsonDocument Parse(string text, string what)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException e)
        {
            throw new TranslationException($"{what} 응답이 JSON이 아닙니다: {Trim(text)}", e);
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
