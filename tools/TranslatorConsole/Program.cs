// 게임 없이 번역 엔진을 시험하는 콘솔.
//
//   dotnet run --project tools/TranslatorConsole -- auth
//   dotnet run --project tools/TranslatorConsole -- ja2ko "零式4層、初見です。よろしくお願いします！"
//   dotnet run --project tools/TranslatorConsole -- ko2ja --effort low          (줄마다 번역, 빈 줄로 끝)
//   dotnet run --project tools/TranslatorConsole -- ja2ko --batch               (빈 줄까지 받은 줄을 한 번에 — 공략 매크로처럼)
//
// 옵션: --engine <claude|codex|ollama|papago> (기본 claude), --model <name> (claude 기본 haiku, ollama 기본 gemma3:12b),
//       --effort <low|medium|high> (claude: --effort, codex: reasoning effort), --exe <실행 파일 경로>,
//       --endpoint <url> (ollama 주소, 기본 http://127.0.0.1:11434)
//       --ollama-exe <ollama.exe 경로> (기본: 플러그인이 설치한 것 → 공식 설치 → PATH)
//       이 PC 의 Ollama 가 꺼져 있으면 창 없이 켜고, 콘솔이 끝나면 같이 끈다 (게임 속 플러그인과 같은 방식).
//       여러 번 시험할 때는 다른 창에서 'TranslatorConsole -- ollama serve' 로 켜 두면 매번 켜고 모델을 올리는 시간을 아낀다
//       --codex-standard (Codex 경량 모드를 끄고 기존 방식으로 비교)
//       --engine claude,codex,ollama 처럼 쉼표로 여러 개 — 그 순서대로 쓰고, 앞의 도구가 실패하면 다음 도구로 번역한다
//       --glossary <파일> (기본 용어집 대신 이 파일을 쓴다 — 용어집을 바꾸기 전/후 비교), --no-glossary (용어집 없이)
//
//   dotnet run --project tools/TranslatorConsole -- match ja2ko "零式4層、初見です"   (번역하지 않고 맞은 용어만)
//
// 표본 기록(게임 PC 의 SampleLogDirectory)으로 용어 후보 뽑기·평가 (Samples.cs):
//   dotnet run --project tools/TranslatorConsole -- samples terms <표본 폴더>
//   dotnet run --project tools/TranslatorConsole -- samples evalset <표본 폴더>
//   dotnet run --project tools/TranslatorConsole -- samples eval <표본 폴더>/evalset.jsonl --engine codex --no-glossary

using System.Text;
using AetheryteChat.Translation;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

string engine = "claude";
string? model = null, effort = null, exe = null, endpoint = null, ollamaExe = null;
bool codexLightweight = true;
string? glossaryPath = null;
bool noGlossary = false;
bool batch = false;
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--engine": engine = args[++i]; break;
        case "--model": model = args[++i]; break;
        case "--effort": effort = args[++i]; break;
        case "--exe": exe = args[++i]; break;
        case "--endpoint": endpoint = args[++i]; break;
        case "--ollama-exe": ollamaExe = args[++i]; break;
        case "--codex-standard": codexLightweight = false; break;
        case "--glossary": glossaryPath = args[++i]; break;
        case "--no-glossary": noGlossary = true; break;
        case "--batch": batch = true; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count == 0)
{
    Console.Error.WriteLine("사용법: TranslatorConsole (auth | ja2ko | ko2ja) [--engine claude|codex|ollama|papago] [--model m] [--effort e] [--exe path] [--endpoint url] [문장]");
    Console.Error.WriteLine("        TranslatorConsole ollama (serve | size | pull | ps | unload) [--model m] [--endpoint url] [--ollama-exe path]");
    Console.Error.WriteLine("        TranslatorConsole match (ja2ko | ko2ja) [--glossary 파일] [문장]   (맞은 용어만)");
    Console.Error.WriteLine("        --codex-standard: Codex 경량 모드 끄기 (비교용)");
    Console.Error.WriteLine("        --glossary <파일>: 기본 용어집 대신 이 파일, --no-glossary: 용어집 없이");
    Console.Error.WriteLine(Samples.Usage);
    return 2;
}

var glossary = noGlossary ? Glossary.Empty
    : glossaryPath is not null ? Glossary.Parse(File.ReadAllText(glossaryPath))
    : Glossary.Default;

// 이 PC 의 Ollama 가 꺼져 있으면 창 없이 켠다. 켠 서버는 이 콘솔이 끝날 때 같이 꺼진다 (OllamaServer 가 작업 개체로 묶는다)
OllamaServerProcess? ollamaServer = null;
async Task EnsureOllamaAsync(string ollamaEndpoint, CancellationToken ct)
{
    if (!OllamaModels.IsLocal(ollamaEndpoint) || ollamaServer is { HasExited: false }
        || await OllamaServer.GetVersionAsync(ollamaEndpoint, ct) is not null)
        return;
    var path = OllamaServer.FindExecutable(ollamaExe ?? PluginOllamaExecutable())
        ?? throw new TranslationException(
            "ollama.exe를 찾지 못했습니다 — 게임 속 번역 도구 탭에서 Ollama를 설치하거나 --ollama-exe 로 경로를 지정해 주세요.");
    Console.Error.WriteLine($"Ollama 서버를 켭니다: {path} (콘솔이 끝나면 꺼집니다)");
    ollamaServer = OllamaServer.Start(path, ollamaEndpoint);
    await ollamaServer.WaitUntilReadyAsync(ollamaEndpoint, TimeSpan.FromSeconds(30), ct);
}

// 게임 속 플러그인이 설치한 ollama.exe (Dalamud 의 플러그인 설정 폴더 아래)
static string PluginOllamaExecutable() => OllamaInstaller.ExecutablePath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "AetheryteChat", "ollama"));

// 번역하지 않고 이 문장에 맞은 용어만 보여 준다 (용어 고르기 시험)
if (positional[0] == "match")
{
    var matchDirection = positional.ElementAtOrDefault(1) == "ko2ja" ? TranslationDirection.KoToJa : TranslationDirection.JaToKo;
    Console.Error.WriteLine($"[{matchDirection}] 용어 {glossary.Count}개{(glossary.InvalidLines > 0 ? $", 읽지 못한 줄 {glossary.InvalidLines}개" : "")}");
    var texts = positional.Count > 2 ? [string.Join(' ', positional.Skip(2))] : ReadLines();
    foreach (var text in texts)
    {
        var terms = glossary.Match(text, matchDirection);
        Console.WriteLine($"{text}\n  {(terms.Count == 0 ? "(없음)" : Glossary.Format(terms, matchDirection).Replace("\n", " / "))}");
    }
    return 0;

    static IEnumerable<string> ReadLines()
    {
        while (Console.ReadLine() is { Length: > 0 } line)
            yield return line.TrimStart('\uFEFF');
    }
}

// Ollama 모델 관리 (번역 도구 탭의 받기·GPU 상태·내리기를 게임 없이 시험)
if (positional[0] == "ollama")
{
    var ollamaEndpoint = endpoint ?? new OllamaOptions().Endpoint;
    var ollamaModel = model ?? new OllamaOptions().Model;
    try
    {
        var command = positional.ElementAtOrDefault(1);
        if (command is "serve" or "pull" or "ps" or "unload")
            await EnsureOllamaAsync(ollamaEndpoint, CancellationToken.None);
        switch (command)
        {
            case "serve":
                // 여러 번 시험할 때 다른 창에서 켜 두면, 번역할 때마다 서버를 켜고 모델을 올리는 시간을 아낀다
                Console.WriteLine(ollamaServer is null
                    ? $"Ollama가 이미 켜져 있습니다 ({ollamaEndpoint})."
                    : $"Ollama를 켰습니다 ({ollamaEndpoint}). Enter를 누르면 끕니다.");
                if (ollamaServer is not null)
                {
                    Console.ReadLine();
                    ollamaServer.Dispose();
                }
                return 0;
            case "size":
                var size = await OllamaModels.GetDownloadSizeAsync(ollamaModel);
                Console.WriteLine($"{ollamaModel}: {(size is { } s ? OllamaModels.FormatSize(s) : "(모름)")}");
                var free = OllamaModels.FreeDiskBytes(ollamaEndpoint);
                Console.WriteLine($"모델 폴더: {OllamaModels.ModelsDirectory}, 여유 {(free is { } f ? OllamaModels.FormatSize(f) : "(모름)")}");
                return 0;
            case "pull":
                var expected = await OllamaModels.GetDownloadSizeAsync(ollamaModel);
                var lastShown = DateTime.MinValue;
                var lastStatus = "";
                await OllamaModels.PullAsync(ollamaEndpoint, ollamaModel, expected, p =>
                {
                    // 줄이 너무 많이 나오지 않게 상태가 바뀔 때와 2초마다만 찍는다
                    if (p.Status == lastStatus && DateTime.UtcNow - lastShown < TimeSpan.FromSeconds(2))
                        return;
                    lastStatus = p.Status;
                    lastShown = DateTime.UtcNow;
                    var fraction = p.Fraction is { } fr ? $" {fr:P0} ({OllamaModels.FormatSize(p.Completed)} / {OllamaModels.FormatSize(p.Total)})" : "";
                    Console.WriteLine($"{p.Status}{fraction}");
                }, CancellationToken.None);
                Console.WriteLine("받았습니다.");
                return 0;
            case "ps":
                var loaded = await OllamaModels.GetLoadedAsync(ollamaEndpoint, ollamaModel);
                Console.WriteLine(loaded is null
                    ? $"{ollamaModel}: 올라가 있지 않음"
                    : $"{ollamaModel}: 전체 {OllamaModels.FormatSize(loaded.Size)}, GPU {OllamaModels.FormatSize(loaded.SizeVram)} ({loaded.GpuFraction:P0}), 내릴 시각 {loaded.ExpiresAt?.ToLocalTime():HH:mm:ss}{(loaded.PartlyOnCpu ? " — 일부가 CPU 에서 돈다" : "")}");
                return 0;
            case "unload":
                await OllamaModels.UnloadAsync(ollamaEndpoint, ollamaModel);
                Console.WriteLine($"{ollamaModel}: 내렸습니다.");
                return 0;
            default:
                Console.Error.WriteLine("ollama 다음에 size | pull | ps | unload 중 하나를 적어 주세요.");
                return 2;
        }
    }
    catch (TranslationException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

// --engine claude,codex 처럼 쉼표로 여러 개를 적으면 그 순서대로 쓴다 (앞의 도구가 실패하면 다음 도구로).
// 여러 개일 때 --model·--effort·--exe 는 앞에 적은 도구에만 맞춰 넣지 않고 각 도구의 기본값을 쓴다
var engineNames = engine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var single = engineNames.Length == 1;
var created = new List<(string Name, ITranslator Translator, string? ExePath)>();
foreach (var name in engineNames)
{
    switch (name)
    {
        case "claude":
            var claude = new ClaudeCliOptions { ExecutablePath = single ? exe : null, Effort = single ? effort : null, Glossary = glossary };
            if (single && model is not null)
                claude.Model = model;
            created.Add((name, new ClaudeCliTranslator(claude), claude.ExecutablePath ?? ClaudeCliLocator.Find()));
            break;
        case "codex":
            var codex = new CodexCliOptions
            {
                ExecutablePath = single ? exe : null, Model = single ? model : null, Lightweight = codexLightweight, Glossary = glossary,
            };
            if (single && effort is not null)
                codex.ReasoningEffort = effort;
            created.Add((name, new CodexCliTranslator(codex), codex.ExecutablePath ?? CodexCliLocator.Find()));
            break;
        case "ollama":
            var ollama = new OllamaOptions { Glossary = glossary };
            if (single && model is not null)
                ollama.Model = model;
            if (endpoint is not null)
                ollama.Endpoint = endpoint;
            ollama.EnsureServerAsync = ct => EnsureOllamaAsync(ollama.Endpoint, ct); // auth 로 확인할 때
            created.Add((name, new OllamaTranslator(ollama), ollama.Endpoint));
            break;
        case "papago":
            created.Add((name, new PapagoWebTranslator(), "(실행 파일 없음 — 웹 요청)"));
            break;
        default:
            Console.Error.WriteLine($"알 수 없는 엔진: {name}");
            return 2;
    }
}
ITranslator translator = single ? created[0].Translator : new FallbackTranslator(created.Select(c => (c.Name, c.Translator)));
var exePath = string.Join(", ", created.Select(c => c.ExePath ?? "(못 찾음)"));

// 번역은 상태 확인(IsLoggedInAsync)을 거치지 않고 바로 부르므로 Ollama 를 미리 켜 둔다.
// 예비 도구가 있으면 켜지 못해도 이어 간다 — 다음 도구로 넘어가는지 볼 수 있게
if (positional[0] != "auth" && engineNames.Contains("ollama"))
{
    try
    {
        await EnsureOllamaAsync(endpoint ?? new OllamaOptions().Endpoint, CancellationToken.None);
    }
    catch (TranslationException e)
    {
        Console.Error.WriteLine(e.Message);
        if (single)
            return 1;
    }
}

if (positional[0] == "samples")
    return await Samples.RunAsync(positional.Skip(1).ToList(), glossary, () => translator);

try
{
    if (positional[0] == "auth")
    {
        Console.WriteLine($"{translator.Name}: {exePath}");
        Console.WriteLine($"로그인: {(await translator.IsLoggedInAsync() ? "됨" : "안 됨")}");
        if (translator is FallbackTranslator chain)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)); // 1순위가 먼저 준비되면 나머지 확인은 뒤에서 이어진다
            foreach (var m in chain.Members)
                Console.WriteLine($"  {m.Key}: {m.Status}{(m.Detail is { } d ? $" ({d})" : "")}");
        }
        foreach (var codexTranslator in created.Select(c => c.Translator).OfType<CodexCliTranslator>())
            Console.WriteLine($"경량 번역: {(!codexLightweight ? "끔" : codexTranslator.LightweightOffReason ?? "씀")}");
        return 0;
    }

    var direction = positional[0] switch
    {
        "ja2ko" => TranslationDirection.JaToKo,
        "ko2ja" => TranslationDirection.KoToJa,
        _ => throw new ArgumentException($"알 수 없는 명령: {positional[0]}"),
    };

    Console.Error.WriteLine($"[{direction}] {translator.Name} model={model ?? "(기본)"} effort={effort ?? "(기본)"}");

    if (positional.Count > 1)
    {
        await TranslateOne(string.Join(' ', positional.Skip(1)));
        return 0;
    }

    if (batch)
    {
        var lines = new List<string>();
        while (Console.ReadLine() is { Length: > 0 } line)
            lines.Add(line.TrimStart('﻿'));
        await TranslateBatch(lines);
        return 0;
    }

    while (Console.ReadLine() is { Length: > 0 } line)
        await TranslateOne(line);
    return 0;

    async Task TranslateBatch(IReadOnlyList<string> texts)
    {
        try
        {
            var r = await translator.TranslateBatchAsync(texts, direction);
            for (var i = 0; i < texts.Count; i++)
                Console.WriteLine($"{texts[i]}\n→ {r.Texts[i]}");
            if (r.Engine is { } usedEngine)
                Console.Error.WriteLine($"   번역한 도구: {usedEngine}");
            Console.Error.WriteLine($"   {texts.Count}줄, {r.Elapsed.TotalSeconds:0.0}초, 입력 {r.InputTokens?.ToString("N0") ?? "?"} / 출력 {r.OutputTokens?.ToString("N0") ?? "?"} 토큰");
        }
        catch (TranslationException e)
        {
            Console.Error.WriteLine($"✗ {e.Message}{(e.IsAuthError ? " [로그인 필요]" : "")}{(e.IsMalformedBatch ? " [줄 수가 맞지 않음 — 플러그인은 줄마다 다시 번역]" : "")}");
        }
    }

    async Task TranslateOne(string text)
    {
        text = text.TrimStart('﻿'); // PowerShell 파이프가 앞에 붙이는 BOM
        try
        {
            var r = await translator.TranslateAsync(text, direction);
            Console.WriteLine($"{text}\n→ {r.Text}");
            if (r.Engine is { } usedEngine)
                Console.Error.WriteLine($"   번역한 도구: {usedEngine}");
            if (translator is not PapagoWebTranslator && glossary.Match(text, direction) is { Count: > 0 } terms)
                Console.Error.WriteLine($"   용어: {Glossary.Format(terms, direction).Replace("\n", " / ")}");
            Console.Error.WriteLine($"   {r.Elapsed.TotalSeconds:0.0}초, 입력 {r.InputTokens?.ToString("N0") ?? "?"} / 출력 {r.OutputTokens?.ToString("N0") ?? "?"} 토큰");
        }
        catch (TranslationException e)
        {
            // 플러그인은 로그인 문제면 실패로 남기지 않고 '로그인 안 됨' 으로 되돌린다 — 그 판정을 여기서도 보여 준다
            Console.Error.WriteLine($"{text}\n✗ {e.Message}{(e.IsAuthError ? " [로그인 필요]" : "")}");
        }
    }
}
catch (Exception e) when (e is TranslationException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
