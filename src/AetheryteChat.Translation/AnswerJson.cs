using System.Text.Json;

namespace AetheryteChat.Translation;

/// <summary>모델이 돌려준 {"translation": "..."} 를 꺼낸다. 코드 펜스·앞뒤 군더더기를 견딘다.</summary>
internal static class AnswerJson
{
    public static string ExtractTranslation(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new TranslationException($"답에 JSON 객체가 없다: {answer.Trim()}");

        try
        {
            using var doc = JsonDocument.Parse(answer.AsMemory(start, end - start + 1));
            if (doc.RootElement.TryGetProperty("translation", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString()!.Trim();
        }
        catch (JsonException e)
        {
            throw new TranslationException($"답의 JSON 을 읽지 못했다: {answer.Trim()}", e);
        }

        throw new TranslationException($"답에 translation 필드가 없다: {answer.Trim()}");
    }
}
