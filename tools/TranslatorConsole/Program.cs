// 게임 없이 번역 엔진을 시험하는 콘솔.
//
//   dotnet run --project tools/TranslatorConsole -- auth
//   dotnet run --project tools/TranslatorConsole -- ja2ko "零式4層、初見です。よろしくお願いします！"
//   dotnet run --project tools/TranslatorConsole -- ko2ja --effort low          (줄마다 번역, 빈 줄로 끝)
//
// 옵션: --engine <claude|codex|ollama|papago> (기본 claude), --model <name> (claude 기본 haiku, ollama 기본 gemma3:12b),
//       --effort <low|medium|high> (claude: --effort, codex: reasoning effort), --exe <실행 파일 경로>,
//       --endpoint <url> (ollama 주소, 기본 http://127.0.0.1:11434)
//       --codex-standard (Codex 경량 모드를 끄고 기존 방식으로 비교)
//       --glossary <파일> (기본 용어집 대신 이 파일을 쓴다 — 용어집을 바꾸기 전/후 비교), --no-glossary (용어집 없이)
//
//   dotnet run --project tools/TranslatorConsole -- match ja2ko "零式4層、初見です"   (번역하지 않고 맞은 용어만)

using System.Text;
using AetheryteChat.Translation;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

string engine = "claude";
string? model = null, effort = null, exe = null, endpoint = null;
bool codexLightweight = true;
string? glossaryPath = null;
bool noGlossary = false;
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
        case "--codex-standard": codexLightweight = false; break;
        case "--glossary": glossaryPath = args[++i]; break;
        case "--no-glossary": noGlossary = true; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count == 0)
{
    Console.Error.WriteLine("사용법: TranslatorConsole (auth | ja2ko | ko2ja) [--engine claude|codex|ollama|papago] [--model m] [--effort e] [--exe path] [--endpoint url] [문장]");
    Console.Error.WriteLine("        TranslatorConsole ollama (size | pull | ps | unload) [--model m] [--endpoint url]");
    Console.Error.WriteLine("        TranslatorConsole match (ja2ko | ko2ja) [--glossary 파일] [문장]   (맞은 용어만)");
    Console.Error.WriteLine("        --codex-standard: Codex 경량 모드 끄기 (비교용)");
    Console.Error.WriteLine("        --glossary <파일>: 기본 용어집 대신 이 파일, --no-glossary: 용어집 없이");
    return 2;
}

var glossary = noGlossary ? Glossary.Empty
    : glossaryPath is not null ? Glossary.Parse(File.ReadAllText(glossaryPath))
    : Glossary.Default;

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
        switch (positional.ElementAtOrDefault(1))
        {
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

ITranslator translator;
string? exePath;
switch (engine)
{
    case "claude":
        var claude = new ClaudeCliOptions { ExecutablePath = exe, Effort = effort, Glossary = glossary };
        if (model is not null)
            claude.Model = model;
        translator = new ClaudeCliTranslator(claude);
        exePath = exe ?? ClaudeCliLocator.Find();
        break;
    case "codex":
        var codex = new CodexCliOptions { ExecutablePath = exe, Model = model, Lightweight = codexLightweight, Glossary = glossary };
        if (effort is not null)
            codex.ReasoningEffort = effort;
        translator = new CodexCliTranslator(codex);
        exePath = exe ?? CodexCliLocator.Find();
        break;
    case "ollama":
        var ollama = new OllamaOptions { Glossary = glossary };
        if (model is not null)
            ollama.Model = model;
        if (endpoint is not null)
            ollama.Endpoint = endpoint;
        translator = new OllamaTranslator(ollama);
        exePath = ollama.Endpoint;
        break;
    case "papago":
        translator = new PapagoWebTranslator();
        exePath = "(실행 파일 없음 — 웹 요청)";
        break;
    default:
        Console.Error.WriteLine($"알 수 없는 엔진: {engine}");
        return 2;
}

try
{
    if (positional[0] == "auth")
    {
        Console.WriteLine($"{translator.Name}: {exePath ?? "(못 찾음)"}");
        Console.WriteLine($"로그인: {(await translator.IsLoggedInAsync() ? "됨" : "안 됨")}");
        if (translator is CodexCliTranslator codexTranslator)
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

    while (Console.ReadLine() is { Length: > 0 } line)
        await TranslateOne(line);
    return 0;

    async Task TranslateOne(string text)
    {
        text = text.TrimStart('﻿'); // PowerShell 파이프가 앞에 붙이는 BOM
        try
        {
            var r = await translator.TranslateAsync(text, direction);
            Console.WriteLine($"{text}\n→ {r.Text}");
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
