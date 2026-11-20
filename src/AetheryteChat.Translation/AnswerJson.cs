using System.Text.Json;

namespace AetheryteChat.Translation;

/// <summary>모델이 돌려준 {"translation": "..."} (여러 줄이면 {"translations": [...]}) 를 꺼낸다. 코드 펜스·앞뒤 군더더기를 견딘다.</summary>
internal static class AnswerJson
{
    public static string ExtractTranslation(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new TranslationException($"답에 JSON 객체가 없습니다: {answer.Trim()}");

        try
        {
            using var doc = JsonDocument.Parse(answer.AsMemory(start, end - start + 1));
            if (doc.RootElement.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString()!.Trim();
        }
        catch (JsonException e)
        {
            throw new TranslationException($"답의 JSON을 읽지 못했습니다: {answer.Trim()}", e);
        }

        throw new TranslationException($"답에 translation 필드가 없습니다: {answer.Trim()}");
    }

    /// <exception cref="TranslationException">답을 읽지 못했거나 줄 수가 <paramref name="count"/> 와 다름 (IsMalformedBatch)</exception>
    /// <remarks>
    /// 일→한 이면 히라가나가 남은 줄이 있을 때도 줄에 맞지 않는 답으로 본다 — 여러 줄을 받으면 모델이 가끔 원문을 그대로,
    /// 또는 용어집의 말만 바꿔 돌려준다 (2026-11-20 Claude haiku, 10줄 묶음 12번 중 1번). 한국어 번역에는 히라가나가 들어갈 일이
    /// 거의 없고, 가타카나는 이름·이모티콘에 쓰일 수 있어 보지 않는다
    /// </remarks>
    public static IReadOnlyList<string> ExtractTranslations(string answer, int count, TranslationDirection direction)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw Malformed($"답에 JSON 객체가 없습니다: {Trim(answer)}");

        try
        {
            using var doc = JsonDocument.Parse(answer.AsMemory(start, end - start + 1));
            if (!doc.RootElement.TryGetProperty("translations", out var array) || array.ValueKind != JsonValueKind.Array)
                throw Malformed($"답에 translations 배열이 없습니다: {Trim(answer)}");
            var texts = array.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : null)
                .ToList();
            if (texts.Count != count || texts.Any(t => t is null))
                throw Malformed($"{count}줄을 보냈는데 답이 {texts.Count}줄입니다: {Trim(answer)}");
            if (direction == TranslationDirection.JaToKo && texts.Count(t => t!.Any(IsHiragana)) is > 0 and var left)
                throw Malformed($"{count}줄 가운데 {left}줄이 번역되지 않았습니다: {Trim(answer)}");
            return [.. texts.Select(t => t!)];
        }
        catch (JsonException e)
        {
            throw new TranslationException($"답의 JSON을 읽지 못했습니다: {Trim(answer)}", e) { IsMalformedBatch = true };
        }
    }

    private static bool IsHiragana(char c) => c is >= 'ぁ' and <= 'ゟ';

    private static TranslationException Malformed(string message) => new(message) { IsMalformedBatch = true };

    private static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";
}
