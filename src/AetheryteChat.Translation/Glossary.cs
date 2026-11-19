using System.Text;

namespace AetheryteChat.Translation;

/// <summary>용어 하나: 일본어 게임 용어와 한국 서버 유저들이 쓰는 말</summary>
public sealed record GlossaryEntry(string Japanese, string Korean)
{
    /// <summary>번역 방향의 원문 쪽 말</summary>
    public string Source(TranslationDirection direction) =>
        direction == TranslationDirection.KoToJa ? Korean : Japanese;

    /// <summary>번역 방향의 번역문 쪽 말</summary>
    public string Target(TranslationDirection direction) =>
        direction == TranslationDirection.KoToJa ? Japanese : Korean;
}

/// <summary>
/// 용어집. 텍스트 형식은 한 줄에 '일본어 → 한국어' 하나 ('->' 도 받는다). 빈 줄과 '#' 로 시작하는 줄은 건너뛴다.
/// 한국어 쪽에 '|' 로 다른 표현(별칭)을 더할 수 있다: '初見 → 초행 | 처음인데'. 첫 말은 일→한 번역에 쓰고,
/// 별칭은 한→일 때 원문에서 찾는 데만 쓴다 — 한국 유저가 같은 뜻을 여러 말로 쓰는데 일본어는 하나인 용어를 위해.
/// 번역할 때는 그 줄에 나온 용어만 골라(<see cref="Match"/>) 보낸다 — 용어집이 커져도 번역마다 쓰는 토큰이 늘지 않게
/// </summary>
public sealed class Glossary
{
    private const string DefaultResourceName = "AetheryteChat.Translation.default-glossary.txt";

    public static Glossary Empty { get; } = new([], 0);

    /// <summary>플러그인에 들어 있는 기본 용어집 (default-glossary.txt). 모든 사용자에게 같이 배포된다</summary>
    public static Glossary Default { get; } = Parse(ReadDefaultText());

    private readonly Entry[] _entries;

    private Glossary(Entry[] entries, int invalidLines)
    {
        _entries = entries;
        InvalidLines = invalidLines;
    }

    public IReadOnlyList<GlossaryEntry> Entries => _entries.Select(e => e.Value).ToList();

    public int Count => _entries.Length;

    /// <summary>'→' 가 없거나 한쪽이 빈 줄의 수. 이런 줄은 쓰지 않는다</summary>
    public int InvalidLines { get; }

    /// <summary>
    /// 텍스트를 읽는다. 같은 일본어가 여러 번 나오면 뒤의 것을 쓴다 (나중에 덧붙인 줄이 앞의 것을 고친다)
    /// </summary>
    public static Glossary Parse(string? text)
    {
        var entries = new List<(GlossaryEntry Value, string[] Aliases)>();
        var invalid = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (ParseFull(line) is { } entry)
                entries.Add(entry);
            else
                invalid++;
        }
        return new Glossary(Dedupe(entries), invalid);
    }

    /// <summary>'일본어 → 한국어' 한 줄 (별칭은 뺀다). 형식이 맞지 않으면 null</summary>
    public static GlossaryEntry? ParseLine(string line) => ParseFull(line)?.Value;

    private static (GlossaryEntry Value, string[] Aliases)? ParseFull(string line)
    {
        var at = line.IndexOf('→');
        var width = 1;
        if (at < 0)
        {
            at = line.IndexOf("->", StringComparison.Ordinal);
            width = 2;
        }
        if (at < 0)
            return null;
        var ja = line[..at].Trim();
        var korean = line[(at + width)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ja.Length > 0 && korean.Length > 0 ? (new GlossaryEntry(ja, korean[0]), korean[1..]) : null;
    }

    /// <summary>기본 용어와 내 용어를 합친다. 같은 일본어(공백·전각/반각·대소문자 차이 무시)가 있으면 내 용어가 우선한다</summary>
    public static Glossary Merge(Glossary defaults, Glossary personal) =>
        new(Dedupe([.. defaults._entries.Select(e => (e.Value, e.Aliases)), .. personal._entries.Select(e => (e.Value, e.Aliases))]),
            defaults.InvalidLines + personal.InvalidLines);

    /// <summary>
    /// 이 원문에 나온 용어만 돌려준다 (용어집 순서). 한→일이면 한국어 쪽, 일→한이면 일본어 쪽을 원문에서 찾는다.
    /// 부분 문자열로 찾는다 — 한국어는 조사가 붙고(영식은·영식에서) 일본어는 띄어 쓰지 않아서.
    /// 비교할 때 공백을 빼고 전각/반각(ＭＴ·MT)과 대소문자를 맞춘다.
    /// 짧은 용어가 나온 자리가 모두 더 긴 용어 안이면(予習 ⊂ 予習済み) 짧은 것은 뺀다.
    /// 한→일에서 별칭으로 찾았으면 돌려주는 항목의 한국어는 그 별칭이다 (프롬프트에 원문에 나온 말 그대로 넣게)
    /// </summary>
    public IReadOnlyList<GlossaryEntry> Match(string text, TranslationDirection direction)
    {
        if (_entries.Length == 0 || string.IsNullOrWhiteSpace(text))
            return [];

        var source = Normalize(text);
        var found = new List<(GlossaryEntry Value, List<int> At, int Length)>();
        foreach (var entry in _entries)
        {
            // 한→일은 한국어와 별칭을 차례로 보고 처음 나온 말로 찾는다
            var terms = direction == TranslationDirection.KoToJa
                ? entry.KoreanKeys.Select((key, i) => (Key: key, Value: i == 0 ? entry.Value : entry.Value with { Korean = entry.Aliases[i - 1] }))
                : [(Key: entry.JapaneseKey, entry.Value)];
            foreach (var (term, value) in terms)
            {
                if (term.Length == 0)
                    continue;
                var at = new List<int>();
                for (var i = source.IndexOf(term, StringComparison.Ordinal); i >= 0;
                     i = source.IndexOf(term, i + 1, StringComparison.Ordinal))
                    at.Add(i);
                if (at.Count == 0)
                    continue;
                found.Add((value, at, term.Length));
                break;
            }
        }

        return found
            .Where(f => !f.At.All(start => found.Any(o =>
                o.Length > f.Length && o.At.Any(os => os <= start && start + f.Length <= os + o.Length))))
            .Select(f => f.Value)
            .ToList();
    }

    /// <summary>프롬프트에 넣을 '원문 → 번역문' 줄들. 한→일이면 한국어 → 일본어로 뒤집어 적는다</summary>
    public static string Format(IEnumerable<GlossaryEntry> entries, TranslationDirection direction) =>
        string.Join('\n', entries.Select(e => $"{e.Source(direction)} → {e.Target(direction)}"));

    /// <summary>이 용어집을 텍스트 형식으로 (한 줄에 '일본어 → 한국어 | 별칭…')</summary>
    public string ToText() => string.Join('\n', _entries.Select(e =>
        $"{e.Value.Japanese} → {string.Join(" | ", [e.Value.Korean, .. e.Aliases])}"));

    /// <summary>비교용 형태: NFKC(전각 영숫자 → 반각, 반각 가타카나 → 전각), 공백 제거, 소문자</summary>
    internal static string Normalize(string s)
    {
        var nfkc = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static Entry[] Dedupe(IEnumerable<(GlossaryEntry Value, string[] Aliases)> entries)
    {
        // 같은 일본어면 뒤의 것이 이기되(별칭도 통째로), 자리는 처음 나온 곳을 지킨다
        var order = new List<string>();
        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (e, aliases) in entries)
        {
            var entry = new Entry(e, aliases, Normalize(e.Japanese), [Normalize(e.Korean), .. aliases.Select(Normalize)]);
            if (!byKey.ContainsKey(entry.JapaneseKey))
                order.Add(entry.JapaneseKey);
            byKey[entry.JapaneseKey] = entry;
        }
        return order.Select(k => byKey[k]).ToArray();
    }

    private static string ReadDefaultText()
    {
        using var stream = typeof(Glossary).Assembly.GetManifestResourceStream(DefaultResourceName)
            ?? throw new InvalidOperationException($"기본 용어집 리소스가 없습니다: {DefaultResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <param name="KoreanKeys">비교용 한국어: [0] 은 한국어, 그 뒤는 별칭 (Aliases 와 같은 순서)</param>
    private sealed record Entry(GlossaryEntry Value, string[] Aliases, string JapaneseKey, string[] KoreanKeys);
}
