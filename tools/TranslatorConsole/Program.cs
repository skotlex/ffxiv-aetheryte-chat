// 게임 없이 번역 엔진을 시험하는 콘솔.
//
//   dotnet run --project tools/TranslatorConsole -- auth
//   dotnet run --project tools/TranslatorConsole -- ja2ko "零式4層、初見です。よろしくお願いします！"
//   dotnet run --project tools/TranslatorConsole -- ko2ja --effort low          (줄마다 번역, 빈 줄로 끝)
//
// 옵션: --model <name> (기본 haiku), --effort <low|medium|high>, --exe <claude.exe 경로>

using System.Text;
using AetheryteChat.Translation;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var options = new ClaudeCliOptions();
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model": options.Model = args[++i]; break;
        case "--effort": options.Effort = args[++i]; break;
        case "--exe": options.ExecutablePath = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count == 0)
{
    Console.Error.WriteLine("사용법: TranslatorConsole (auth | ja2ko | ko2ja) [--model m] [--effort e] [--exe path] [문장]");
    return 2;
}

var translator = new ClaudeCliTranslator(options);

try
{
    if (positional[0] == "auth")
    {
        Console.WriteLine($"claude.exe: {options.ExecutablePath ?? ClaudeCliLocator.Find() ?? "(못 찾음)"}");
        Console.WriteLine($"로그인: {(await translator.IsLoggedInAsync() ? "됨" : "안 됨")}");
        return 0;
    }

    var direction = positional[0] switch
    {
        "ja2ko" => TranslationDirection.JaToKo,
        "ko2ja" => TranslationDirection.KoToJa,
        _ => throw new ArgumentException($"알 수 없는 명령: {positional[0]}"),
    };

    Console.Error.WriteLine($"[{direction}] model={options.Model} effort={options.Effort ?? "(기본)"}");

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
        try
        {
            var r = await translator.TranslateAsync(text, direction);
            Console.WriteLine($"{text}\n→ {r.Text}");
            Console.Error.WriteLine($"   {r.Elapsed.TotalSeconds:0.0}초, 입력 {r.InputTokens?.ToString("N0") ?? "?"} / 출력 {r.OutputTokens?.ToString("N0") ?? "?"} 토큰");
        }
        catch (TranslationException e)
        {
            Console.Error.WriteLine($"{text}\n✗ {e.Message}");
        }
    }
}
catch (Exception e) when (e is TranslationException or ArgumentException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
