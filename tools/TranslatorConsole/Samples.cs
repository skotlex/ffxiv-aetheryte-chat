using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AetheryteChat.Translation;

/// <summary>
/// 게임 PC 의 표본 기록(samples-*.jsonl, 플러그인의 SampleLog)으로 용어 후보를 뽑고(terms),
/// 평가 묶음을 만들고(evalset), 엔진·용어집별 용어 적중률을 잰다(eval).
/// 결과 파일은 표본 폴더에 쓴다 — 다른 플레이어의 대화가 들어 있으므로 저장소에 넣지 않는다
/// </summary>
internal static partial class Samples
{
    public const string Usage = """
        TranslatorConsole samples terms <표본 폴더> [--min-count N]      용어 후보 → terms.tsv, 엔진끼리 다른 번역 등 → disagreements.tsv
        TranslatorConsole samples evalset <표본 폴더> [--max N]          용어집 용어가 든 줄 → evalset.jsonl
        TranslatorConsole samples eval <evalset.jsonl> [--engine e …]    용어 적중률 (--glossary 파일 / --no-glossary 로 전후 비교)
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, Glossary glossary, Func<ITranslator> createTranslator)
    {
        var command = args.ElementAtOrDefault(0);
        var path = args.ElementAtOrDefault(1);
        if (command is null || path is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var options = ParseOptions(args.Skip(2));
        return command switch
        {
            "terms" => Terms(path, glossary, options.GetValueOrDefault("--min-count", 3)),
            "evalset" => EvalSet(path, glossary, options.GetValueOrDefault("--max", 200)),
            "eval" => await EvalAsync(path, createTranslator()),
            _ => Fail($"알 수 없는 samples 명령: {command}\n{Usage}"),
        };
    }

    private static Dictionary<string, int> ParseOptions(IEnumerable<string> rest)
    {
        var options = new Dictionary<string, int>();
        var list = rest.ToList();
        for (var i = 0; i + 1 < list.Count; i++)
        {
            if (list[i].StartsWith("--", StringComparison.Ordinal) && int.TryParse(list[i + 1], out var n))
                options[list[i]] = n;
        }
        return options;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    // ───────────────────────── 기록 읽기 ─────────────────────────

    private sealed record Record(string Kind, DateTime Time, string Channel, string Engine, JsonObject Fields)
    {
        public string? Get(string name) => Fields[name]?.GetValueKind() == JsonValueKind.String ? (string?)Fields[name] : null;
    }

    private static List<Record> ReadRecords(string directory)
    {
        var files = Directory.GetFiles(directory, "samples-*.jsonl").Order(StringComparer.Ordinal).ToList();
        var records = new List<Record>();
        var bad = 0;
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject o || (string?)o["kind"] is not { } kind)
                    {
                        bad++;
                        continue;
                    }
                    DateTime.TryParse((string?)o["t"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
                    records.Add(new Record(kind, time, (string?)o["ch"] ?? "", (string?)o["engine"] ?? "", o));
                }
                catch (JsonException)
                {
                    bad++;
                }
            }
        }
        Console.Error.WriteLine($"표본 파일 {files.Count}개, 기록 {records.Count}건{(bad > 0 ? $", 읽지 못한 줄 {bad}개" : "")}");
        return records;
    }

    /// <summary>
    /// 받은 채팅 기록에서 같은 줄을 다른 도구로 다시 번역한 것을 하나로 친다 — 빈도를 셀 때 한 메시지가 여러 번 세지지 않게.
    /// 같은 원문이 5분 안에 다른 도구로 다시 나오면 다시 번역으로 본다 (같은 도구면 다른 사람이 같은 말을 한 것)
    /// </summary>
    private static List<Record> UniqueIncoming(IEnumerable<Record> records)
    {
        var seen = new Dictionary<string, List<Record>>(StringComparer.Ordinal);
        var unique = new List<Record>();
        foreach (var r in records.Where(r => r.Kind == "in" && r.Get("src") is not null).OrderBy(r => r.Time))
        {
            var src = r.Get("src")!;
            if (!seen.TryGetValue(src, out var earlier))
                seen[src] = earlier = [];
            var retranslation = earlier.Any(e => e.Engine != r.Engine && (r.Time - e.Time).Duration() <= TimeSpan.FromMinutes(5));
            earlier.Add(r);
            if (!retranslation)
                unique.Add(r);
        }
        return unique;
    }

    // ───────────────────────── terms ─────────────────────────

    // 후보: 가타카나 2자 이상 (マケボ), 한자 2자 이상 (散開), 영문 대문자 약어 2~4자 (MT, LB). 비교 전에 NFKC 로 전각을 반각으로 맞춘다.
    // 한자 사이의 送り仮名 き·け·ち·え·し (受け取り·申し込み)와 끝의 り·み (頭割り·予習済み)는 한 말로 붙인다.
    // り·み 는 대개 말의 끝이라 사이에는 두지 않는다 — 頭割り外す 가 한 말로 붙지 않게.
    // 끝에 다른 가나는 붙이지 않는다 — 全滅した·散開ちゃう 의 し·ち 는 활용이라 붙이면 같은 말이 여러 후보로 갈린다
    [GeneratedRegex(@"[ァ-ヶ][ァ-ヶー]+|[一-龯々]+(?:[きけちえし][一-龯々]+)*[りみ]?|(?<![A-Za-z])[A-Z]{2,4}(?![A-Za-z])")]
    private static partial Regex CandidatePattern();

    // 너무 흔해 용어집에 넣을 까닭이 없는 말. 완전하지 않다 — 나머지는 --min-count 와 사람이 거른다
    private static readonly HashSet<string> StopWords =
    [
        "今日", "明日", "昨日", "今回", "本当", "大丈夫", "了解", "失礼", "宜", "自分", "時間", "最初", "最後", "一緒", "全然",
        "多分", "普通", "結構", "皆", "皆様", "皆さん", "何回", "一回", "今度", "大変", "勉強", "練習", "確認", "問題",
        "サーバー", "キャラ", "フレンド", "ゲーム", "ログイン", "ログアウト", "リアル", "ナイス", "ファイト", "ドンマイ",
    ];

    private sealed class TermStats
    {
        public int Count;
        public readonly HashSet<string> Texts = new(StringComparer.Ordinal);
        public readonly List<string> Translations = [];
    }

    private static int Terms(string directory, Glossary glossary, int minCount)
    {
        var records = ReadRecords(directory);
        var incoming = UniqueIncoming(records);
        var outgoing = records.Where(r => r.Kind == "out" && r.Get("ja") is not null).ToList();

        // 같은 원문의 도구별 번역 (후보의 '지금 번역' 칸)
        var translationsBySrc = records
            .Where(r => r.Kind == "in" && r.Get("state") == "done" && r.Get("src") is not null && r.Get("out") is not null)
            .GroupBy(r => r.Get("src")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => $"{r.Engine}: {r.Get("out")}").Distinct().ToList(), StringComparer.Ordinal);

        var stats = new Dictionary<string, TermStats>(StringComparer.Ordinal);
        var texts = incoming.Select(r => r.Get("src")!).Concat(outgoing.Select(r => r.Get("ja")!));
        foreach (var text in texts)
        {
            foreach (var term in Candidates(text, glossary).Distinct())
            {
                if (!stats.TryGetValue(term, out var s))
                    stats[term] = s = new TermStats();
                s.Count++;
                if (s.Texts.Add(text) && translationsBySrc.TryGetValue(text, out var tr) && s.Translations.Count < 3)
                    s.Translations.Add(string.Join(" | ", tr));
            }
        }

        var rows = stats
            .Where(kv => kv.Value.Count >= minCount)
            .OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Texts.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var termsPath = Path.Combine(directory, "terms.tsv");
        using (var w = new StreamWriter(termsPath, false, new UTF8Encoding(false)))
        {
            w.WriteLine("term\tcount\tdistinct\texample1\texample2\texample3\tcurrent_translations");
            foreach (var (term, s) in rows)
            {
                var examples = s.Texts.OrderBy(t => t.Length).Take(3).ToList();
                while (examples.Count < 3)
                    examples.Add("");
                w.WriteLine(string.Join('\t', [term, s.Count.ToString(), s.Texts.Count.ToString(), .. examples.Select(Cell),
                    Cell(string.Join(" || ", s.Translations))]));
            }
        }
        Console.Error.WriteLine($"용어 후보 {rows.Count}개 (최소 {minCount}번, 용어집에 있는 말 {glossary.Count}개는 뺌) → {termsPath}");
        foreach (var (term, s) in rows.Take(30))
            Console.WriteLine($"{s.Count,5} {s.Texts.Count,5}  {term}");

        var disagreementsPath = Path.Combine(directory, "disagreements.tsv");
        var disagreements = Disagreements(records);
        using (var w = new StreamWriter(disagreementsPath, false, new UTF8Encoding(false)))
        {
            w.WriteLine("type\tscore\tch\ttext\tdetail");
            foreach (var d in disagreements)
                w.WriteLine(string.Join('\t', d.Type, d.Score.ToString("0.00", CultureInfo.InvariantCulture), d.Channel, Cell(d.Text), Cell(d.Detail)));
        }
        Console.Error.WriteLine($"품질 신호 {disagreements.Count}건 (엔진끼리 다름 {disagreements.Count(d => d.Type == "engines")}, " +
                                $"오역 제보 {disagreements.Count(d => d.Type == "flag")}, 역번역 차이 {disagreements.Count(d => d.Type == "back")}) → {disagreementsPath}");
        return 0;
    }

    /// <summary>원문에서 용어 후보를 뽑는다. 용어집에 이미 있는 일본어 자리는 지우고 찾아, 그 일부가 후보로 나오지 않게 한다</summary>
    internal static IEnumerable<string> Candidates(string text, Glossary glossary)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        foreach (var entry in glossary.Entries.OrderByDescending(e => e.Japanese.Length))
            normalized = normalized.Replace(entry.Japanese.Normalize(NormalizationForm.FormKC), "　", StringComparison.Ordinal);
        return CandidatePattern().Matches(normalized).Select(m => m.Value)
            .Where(t => !IsKanjiStart(t) || t.Count(IsKanji) >= 2) // 한자 말은 한자가 2자 이상일 때만 (釣り·見え 등은 뺀다)
            .Where(t => !StopWords.Contains(t));

        static bool IsKanji(char c) => c is >= '一' and <= '龯' or '々';
        static bool IsKanjiStart(string t) => IsKanji(t[0]);
    }

    private sealed record Disagreement(string Type, double Score, string Channel, string Text, string Detail);

    /// <summary>
    /// 사람이 볼 만한 줄: 같은 원문을 도구마다 다르게 옮긴 것, 오역 제보, 다시 옮긴 한국어가 쓴 한국어와 많이 다른 보낸 글.
    /// score 는 비슷한 정도(0~1, 글자 두 개씩 묶어 비교). 낮을수록 위에 둔다
    /// </summary>
    private static List<Disagreement> Disagreements(List<Record> records)
    {
        var list = new List<Disagreement>();

        foreach (var g in records.Where(r => r.Kind == "in" && r.Get("state") == "done" && r.Get("src") is not null && r.Get("out") is not null)
                     .GroupBy(r => r.Get("src")!, StringComparer.Ordinal))
        {
            var byEngine = g.GroupBy(r => r.Engine).Select(e => e.Last()).ToList();
            if (byEngine.Count < 2)
                continue;
            var outs = byEngine.Select(r => r.Get("out")!).ToList();
            var score = outs.SelectMany((a, i) => outs.Skip(i + 1).Select(b => Similarity(a, b))).Min();
            if (score < 1)
                list.Add(new("engines", score, byEngine[0].Channel, g.Key, string.Join(" | ", byEngine.Select(r => $"{r.Engine}: {r.Get("out")}"))));
        }

        foreach (var r in records.Where(r => r.Kind == "flag"))
            list.Add(new("flag", 0, r.Channel, r.Get("src") ?? "", $"{r.Engine}: {r.Get("out")}"));

        foreach (var r in records.Where(r => r.Kind == "out" && r.Get("ko") is not null && r.Get("back") is not null))
        {
            var score = Similarity(r.Get("ko")!, r.Get("back")!);
            if (score < 0.5)
                list.Add(new("back", score, r.Channel, r.Get("ko")!, $"ja: {r.Get("ja")} | back: {r.Get("back")}"));
        }

        return list.OrderBy(d => d.Type == "flag" ? 0 : 1).ThenBy(d => d.Score).ToList();
    }

    /// <summary>글자 두 개씩 묶은 것의 Dice 계수. 공백·문장부호는 빼고 비교한다</summary>
    internal static double Similarity(string a, string b)
    {
        var x = Bigrams(Compact(a));
        var y = Bigrams(Compact(b));
        if (x.Count == 0 && y.Count == 0)
            return 1;
        var common = 0;
        var pool = y.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        foreach (var bg in x)
        {
            if (pool.TryGetValue(bg, out var n) && n > 0)
            {
                common++;
                pool[bg] = n - 1;
            }
        }
        return 2.0 * common / (x.Count + y.Count);

        static List<string> Bigrams(string s) =>
            s.Length < 2 ? [s] : Enumerable.Range(0, s.Length - 1).Select(i => s.Substring(i, 2)).ToList();
    }

    /// <summary>비교용: NFKC, 소문자, 글자·숫자만</summary>
    internal static string Compact(string s) =>
        new(s.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>TSV 칸: 탭·줄바꿈을 공백으로</summary>
    private static string Cell(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    // ───────────────────────── evalset ─────────────────────────

    /// <summary>
    /// 용어집 용어가 든 줄을 뽑아 평가 묶음으로 고정한다: 받은 채팅 원문(일→한)과 보낸 글의 한국어(한→일).
    /// 기대 용어는 그 줄에 맞은 용어집 항목이다. 같은 문장은 한 번만, 용어가 많이 든 줄부터 max 개
    /// </summary>
    private static int EvalSet(string directory, Glossary glossary, int max)
    {
        var records = ReadRecords(directory);
        var candidates = UniqueIncoming(records).Select(r => (Dir: TranslationDirection.JaToKo, Text: r.Get("src")!))
            .Concat(records.Where(r => r.Kind == "out" && r.Get("ko") is not null).Select(r => (Dir: TranslationDirection.KoToJa, Text: r.Get("ko")!)))
            .Distinct()
            .Select(c => (c.Dir, c.Text, Terms: glossary.Match(c.Text, c.Dir)))
            .Where(c => c.Terms.Count > 0)
            .OrderByDescending(c => c.Terms.Count).ThenBy(c => c.Text.Length)
            .Take(max)
            .ToList();

        var path = Path.Combine(directory, "evalset.jsonl");
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            foreach (var c in candidates)
            {
                var item = new JsonObject
                {
                    ["dir"] = DirectionName(c.Dir),
                    ["src"] = c.Text,
                    ["expect"] = new JsonArray(c.Terms.Select(t => (JsonNode)new JsonObject { ["ja"] = t.Japanese, ["ko"] = t.Korean }).ToArray()),
                };
                w.WriteLine(item.ToJsonString(JsonOptions));
            }
        }
        Console.Error.WriteLine($"평가 항목 {candidates.Count}개 (일→한 {candidates.Count(c => c.Dir == TranslationDirection.JaToKo)}, " +
                                $"한→일 {candidates.Count(c => c.Dir == TranslationDirection.KoToJa)}) → {path}");
        Console.Error.WriteLine("필요하면 손으로 고치거나 줄을 지워 고정해 두고, eval 로 잽니다.");
        return 0;
    }

    private static string DirectionName(TranslationDirection d) => d == TranslationDirection.KoToJa ? "ko2ja" : "ja2ko";

    // ───────────────────────── eval ─────────────────────────

    private sealed record EvalItem(TranslationDirection Direction, string Source, List<GlossaryEntry> Expect);

    /// <summary>
    /// 평가 묶음을 번역하고 용어 적중률을 잰다: 번역문에 기대 용어(일→한이면 한국어, 한→일이면 일본어)가 들어 있는 비율.
    /// 공백·문장부호 차이는 무시한다. 한 프로세스에서 같은 문장은 한 번만 번역하므로 Codex 결과 캐시가 끼어들지 않는다
    /// </summary>
    private static async Task<int> EvalAsync(string path, ITranslator translator)
    {
        var items = new List<EvalItem>();
        foreach (var line in File.ReadLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var o = JsonNode.Parse(line)!.AsObject();
            var expect = o["expect"]!.AsArray()
                .Select(e => new GlossaryEntry((string)e!["ja"]!, (string)e["ko"]!)).ToList();
            items.Add(new EvalItem((string?)o["dir"] == "ko2ja" ? TranslationDirection.KoToJa : TranslationDirection.JaToKo,
                (string)o["src"]!, expect));
        }
        items = items.DistinctBy(i => (i.Direction, i.Source)).ToList();
        Console.Error.WriteLine($"[{translator.Name}] 평가 항목 {items.Count}개");

        var terms = 0;
        var hits = 0;
        var failures = 0;
        var times = new List<double>();
        var inputTokens = new List<long>();
        var misses = new List<string>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            TranslationResult result;
            try
            {
                result = await translator.TranslateAsync(item.Source, item.Direction);
            }
            catch (TranslationException e)
            {
                failures++;
                terms += item.Expect.Count;
                misses.Add($"✗ {item.Source}\n    실패: {e.Message}");
                continue;
            }
            times.Add(result.Elapsed.TotalSeconds);
            if (result.InputTokens is { } tokens)
                inputTokens.Add(tokens);

            var output = Compact(result.Text);
            var missed = item.Expect.Where(e => !output.Contains(Compact(e.Target(item.Direction)), StringComparison.Ordinal)).ToList();
            terms += item.Expect.Count;
            hits += item.Expect.Count - missed.Count;
            if (missed.Count > 0)
                misses.Add($"✗ {item.Source}\n  → {result.Text}\n    빠진 용어: {Glossary.Format(missed, item.Direction).Replace("\n", " / ")}");
            Console.Error.Write($"\r{i + 1}/{items.Count}");
        }
        Console.Error.WriteLine();

        times.Sort();
        Console.WriteLine($"엔진: {translator.Name} ({translator.ShortName})");
        Console.WriteLine($"용어 적중률: {hits}/{terms} = {(terms == 0 ? 0 : 100.0 * hits / terms):0.0}%");
        Console.WriteLine($"실패: {failures}/{items.Count}");
        if (times.Count > 0)
            Console.WriteLine($"시간: 평균 {times.Average():0.0}초, 중앙 {times[times.Count / 2]:0.0}초, 최대 {times[^1]:0.0}초 (전체 {sw.Elapsed.TotalSeconds:0}초)");
        if (inputTokens.Count > 0)
            Console.WriteLine($"입력 토큰: 평균 {inputTokens.Average():N0}");
        if (misses.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"틀린 항목 {misses.Count}개:");
            foreach (var m in misses)
                Console.WriteLine(m);
        }
        return 0;
    }
}
