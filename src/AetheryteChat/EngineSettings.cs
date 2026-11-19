using AetheryteChat.Translation;

namespace AetheryteChat;

// 엔진별 설정. 설정 파일에는 이 클래스들을 저장하고, 번역할 때 번역 라이브러리의 옵션으로 옮긴다.
// 라이브러리의 옵션 클래스를 그대로 저장하지 않는 까닭: Dalamud 는 설정을 형식 이름($type)과 함께 저장하는데,
// 다른 어셈블리(AetheryteChat.Translation)의 형식은 읽을 때 찾지 못할 수 있다.
// 값이 비었거나 범위를 벗어나면 옵션으로 옮길 때 기본값으로 되돌린다 — 설정 파일을 손으로 고쳐도 번역이 멈추지 않게.

[Serializable]
public sealed class ClaudeSettings
{
    private static readonly ClaudeCliOptions Defaults = new();

    /// <summary>claude.exe 경로. 비우면 자동으로 찾는다</summary>
    public string? ExecutablePath { get; set; }

    public string Model { get; set; } = Defaults.Model;

    /// <summary>low / medium / high. 비우면 넘기지 않는다 (Claude 기본)</summary>
    public string? Effort { get; set; }

    public int TimeoutSeconds { get; set; } = (int)Defaults.Timeout.TotalSeconds;

    internal ClaudeCliOptions ToOptions(string glossary) => new()
    {
        ExecutablePath = EngineSettings.OrNull(ExecutablePath),
        Model = EngineSettings.OrNull(Model) ?? Defaults.Model,
        Effort = EngineSettings.OrNull(Effort),
        Timeout = EngineSettings.Timeout(TimeoutSeconds),
        Glossary = glossary,
    };
}

[Serializable]
public sealed class CodexSettings
{
    private static readonly CodexCliOptions Defaults = new();

    /// <summary>codex.exe 경로. 비우면 자동으로 찾는다</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>비우면 Codex 기본 모델</summary>
    public string? Model { get; set; }

    public string ReasoningEffort { get; set; } = Defaults.ReasoningEffort;

    public int TimeoutSeconds { get; set; } = (int)Defaults.Timeout.TotalSeconds;

    internal CodexCliOptions ToOptions(string glossary) => new()
    {
        ExecutablePath = EngineSettings.OrNull(ExecutablePath),
        Model = EngineSettings.OrNull(Model),
        ReasoningEffort = EngineSettings.OrNull(ReasoningEffort) ?? Defaults.ReasoningEffort,
        Timeout = EngineSettings.Timeout(TimeoutSeconds),
        Glossary = glossary,
    };
}

[Serializable]
public sealed class OllamaSettings
{
    private static readonly OllamaOptions Defaults = new();

    public string Endpoint { get; set; } = Defaults.Endpoint;

    public string Model { get; set; } = Defaults.Model;

    /// <summary>번역 사이에 모델을 VRAM 에 올려 두는 시간 (Ollama 형식: 30m, 1h, -1 = 계속)</summary>
    public string KeepAlive { get; set; } = Defaults.KeepAlive;

    /// <summary>상태를 확인할 때 모델을 미리 GPU 에 올린다. 끄면 첫 번역 때 올린다</summary>
    public bool PreloadOnCheck { get; set; } = Defaults.Preload;

    public int TimeoutSeconds { get; set; } = (int)Defaults.Timeout.TotalSeconds;

    internal OllamaOptions ToOptions(string glossary) => new()
    {
        Endpoint = EngineSettings.OrNull(Endpoint) ?? Defaults.Endpoint,
        Model = EngineSettings.OrNull(Model) ?? Defaults.Model,
        KeepAlive = EngineSettings.OrNull(KeepAlive) ?? Defaults.KeepAlive,
        Preload = PreloadOnCheck,
        Timeout = EngineSettings.Timeout(TimeoutSeconds),
        Glossary = glossary,
    };
}

internal static class EngineSettings
{
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 300;

    public static string? OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static TimeSpan Timeout(int seconds) =>
        TimeSpan.FromSeconds(Math.Clamp(seconds, MinTimeoutSeconds, MaxTimeoutSeconds));
}
