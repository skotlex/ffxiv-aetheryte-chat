// 게임 없이 번역 엔진을 시험하는 콘솔.
//
//   dotnet run --project tools/TranslatorConsole -- auth
//   dotnet run --project tools/TranslatorConsole -- ja2ko "零式4層、初見です。よろしくお願いします！"
//   dotnet run --project tools/TranslatorConsole -- ko2ja --effort low          (줄마다 번역, 빈 줄로 끝)
//
// 옵션: --engine <claude|codex|ollama|papago> (기본 claude), --model <name> (claude 기본 haiku, ollama 기본 gemma3:12b),
//       --effort <low|medium|high> (claude: --effort, codex: reasoning effort), --exe <실행 파일 경로>,
//       --endpoint <url> (ollama 주소, 기본 http://127.0.0.1:11434)

using System.Text;
using AetheryteChat.Translation;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

string engine = "claude";
string? model = null, effort = null, exe = null, endpoint = null;
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
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count == 0)
{
    Console.Error.WriteLine("사용법: TranslatorConsole (auth | ja2ko | ko2ja) [--engine claude|codex|ollama|papago] [--model m] [--effort e] [--exe path] [--endpoint url] [문장]");
    return 2;
}

ITranslator translator;
string? exePath;
switch (engine)
{
    case "claude":
        var claude = new ClaudeCliOptions { ExecutablePath = exe, Effort = effort };
        if (model is not null)
            claude.Model = model;
        translator = new ClaudeCliTranslator(claude);
        exePath = exe ?? ClaudeCliLocator.Find();
        break;
    case "codex":
        var codex = new CodexCliOptions { ExecutablePath = exe, Model = model };
        if (effort is not null)
            codex.ReasoningEffort = effort;
        translator = new CodexCliTranslator(codex);
        exePath = exe ?? CodexCliLocator.Find();
        break;
    case "ollama":
        var ollama = new OllamaOptions();
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
