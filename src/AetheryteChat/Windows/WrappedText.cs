using Dalamud.Bindings.ImGui;

namespace AetheryteChat.Windows;

/// <summary>
/// 일본어가 섞인 글을 창 너비에 맞춰 줄바꿈해 그린다.
/// ImGui 의 TextWrapped 는 공백에서만 줄을 바꿔, 띄어쓰기가 없는 일본어 문장을 한 단어로 보고
/// 앞줄에 자리가 남아도 통째로 다음 줄로 넘긴다. 여기서는 가나·한자·전각 기호·게임 기호 사이에서도 줄을 바꾼다.
/// 한글은 띄어쓰기에서만 바꾸고, 한 단어가 창보다 길 때만 글자 단위로 자른다.
/// </summary>
internal static class WrappedText
{
    private const int MaxCacheEntries = 1000;

    // 글 → (계산한 너비, 줄들). 창 너비가 바뀌면 다시 계산한다. 받은 채팅 200줄 × 원문·번역 정도라 크기를 묶어 둔다
    private static readonly Dictionary<string, (float Width, List<string> Lines)> Cache = new();

    /// <summary>현재 스타일 색으로 그린다. 색을 바꾸려면 호출 앞에서 ImGuiCol.Text 를 push 한다.</summary>
    public static void Draw(string text)
    {
        var width = ImGui.GetContentRegionAvail().X;
        foreach (var line in GetLines(text, width))
            ImGui.TextUnformatted(line);
    }

    public static void Draw(string text, System.Numerics.Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        Draw(text);
        ImGui.PopStyleColor();
    }

    private static List<string> GetLines(string text, float width)
    {
        if (Cache.TryGetValue(text, out var cached) && Math.Abs(cached.Width - width) < 0.5f)
            return cached.Lines;

        if (Cache.Count >= MaxCacheEntries)
            Cache.Clear();

        var lines = Wrap(text, width);
        Cache[text] = (width, lines);
        return lines;
    }

    private static List<string> Wrap(string text, float width)
    {
        var lines = new List<string>();
        if (width <= 1f)
        {
            // 창이 막 열려 너비가 아직 없을 때 — 글자마다 줄을 나누지 않는다
            lines.Add(text);
            return lines;
        }

        foreach (var paragraph in text.Split('\n'))
            WrapParagraph(paragraph, width, lines);
        return lines;
    }

    private static void WrapParagraph(string text, float width, List<string> lines)
    {
        if (text.Length == 0)
        {
            lines.Add(""); // 빈 줄도 높이를 차지하게 한다 (TextWrapped 와 같게)
            return;
        }

        var start = 0;
        while (start < text.Length)
        {
            // 남은 글이 한 줄에 들어가면 끝
            if (Measure(text, start, text.Length) <= width)
            {
                lines.Add(text[start..]);
                return;
            }

            // 들어가는 데까지 늘리면서 마지막 줄바꿈 가능 위치를 기억한다
            var end = start + 1;
            var lastBreak = CanBreakBefore(text, end) ? end : -1;
            while (end < text.Length && Measure(text, start, end + 1) <= width)
            {
                end++;
                if (CanBreakBefore(text, end))
                    lastBreak = end;
            }

            // 줄바꿈 가능 위치가 없으면 (창보다 긴 한 단어) 들어가는 데까지 자른다
            var cut = lastBreak > start ? lastBreak : end;
            if (cut < text.Length && char.IsLowSurrogate(text[cut]) && cut - 1 > start)
                cut--; // 서로게이트 쌍(이모지 등)을 반으로 자르지 않는다
            lines.Add(text[start..cut].TrimEnd());

            start = cut;
            while (start < text.Length && text[start] == ' ')
                start++; // 줄 머리의 공백은 버린다
        }
    }

    /// <summary>text[index] 앞에서 줄을 바꿀 수 있는지</summary>
    private static bool CanBreakBefore(string text, int index)
    {
        if (index <= 0 || index >= text.Length)
            return false;

        var prev = text[index - 1];
        var next = text[index];
        if (char.IsLowSurrogate(next))
            return false;
        if (prev == ' ')
            return true;
        if (IsClosingPunctuation(next))
            return false; // 。、」 등은 줄 머리에 오지 않게 한다
        return IsCjkBreakable(prev) || IsCjkBreakable(next);
    }

    private static bool IsCjkBreakable(char c) =>
        c is >= '　' and <= 'ヿ'   // CJK 기호·구두점, 히라가나, 가타카나
            or >= '㐀' and <= '䶿' // 한자 확장 A
            or >= '一' and <= '鿿' // 한자
            or >= '' and <= '' // 게임 전용 기호 (사용자 정의 영역)
            or >= '＀' and <= '￯'; // 전각 영숫자·기호, 반각 가타카나

    private static bool IsClosingPunctuation(char c) =>
        c is '。' or '、' or '」' or '』' or '）' or '！' or '？' or '〜' or '～' or 'ー' or '…' or '・' or '，' or '．';

    private static float Measure(string text, int start, int end) =>
        ImGui.CalcTextSize(text.AsSpan(start, end - start)).X;
}
