using System.Numerics;
using AetheryteChat.Translation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using static AetheryteChat.Windows.UiCommon;

namespace AetheryteChat.Windows;

/// <summary>
/// '번역 도구' 탭: 어느 도구로 번역할지, 그 도구를 준비하는 방법·설정·상태, 용어집.
/// 처음 쓰는 사람이 터미널을 몰라도 여기서 무엇을 해야 하는지 알 수 있게 한다
/// </summary>
internal sealed class EngineTab(Plugin plugin) : IDisposable
{
    /// <summary>번역 도구를 바꿀 때 새 도구로 다시 번역할 최근 대화 수 — 대기열 길이에 맞춰, 넣자마자 건너뛰는 줄이 없게 한다</summary>
    private const int RetranslateCount = TranslationQueue.MaxWaiting;

    private const string NotChosen = "골라 주세요";

    // 목록 상자에는 이름만 보이고(길면 잘린다), 설명은 펼친 목록과 아직 고르지 않았을 때의 안내에 나온다
    private static readonly (TranslationEngine Engine, string Name, string Description)[] Engines =
    [
        (TranslationEngine.Claude, "Claude CLI", "Claude 구독 필요 · 약 2초 · 게임 용어에 강합니다"),
        (TranslationEngine.Codex, "Codex CLI", "ChatGPT 구독 필요 · 약 4~5초, 가끔 15초 넘게 걸립니다"),
        (TranslationEngine.Ollama, "Ollama (로컬)", "무료 · 이 PC에서 돌림 · GPU가 있으면 약 1초, 없으면 약 3초"),
        (TranslationEngine.Papago, "Papago (웹)", "무료 · 준비할 것 없음 · 막힐 수 있고 게임 용어에 약합니다"),
    ];

    private const string ClaudeSetupUrl = "https://code.claude.com/docs/en/setup";
    private const string CodexSetupUrl = "https://developers.openai.com/codex/cli";
    private const string OllamaDownloadUrl = "https://ollama.com/download";

    /// <summary>모델 목록에 늘 보이는 권장 모델 (GPU 가 넉넉할 때 / GPU 가 없거나 적을 때)</summary>
    private static readonly string[] RecommendedOllamaModels = [OllamaModels.LargeModel, OllamaModels.MidModel, OllamaModels.SmallModel];

    /// <summary>이 PC 의 그래픽카드로 고른 권장 모델. 그래픽카드는 플러그인을 켜 있는 동안 바뀌지 않으므로 한 번만 정한다</summary>
    private static readonly OllamaRecommendation ThisPc = OllamaModels.RecommendForThisPc();

    private static float InputWidth => 320 * ImGuiHelpers.GlobalScale;
    private static float TimeoutSliderWidth => 160 * ImGuiHelpers.GlobalScale;

    // 자동으로 찾은 실행 파일. 파일 시스템을 뒤지므로 매 프레임 찾지 않고, 처음 그릴 때와 '다시 확인' 때 찾는다
    private Found _claudeFound;
    private Found _codexFound;

    // Ollama 에 받아 둔 모델 목록. 주소를 바꾸거나 '다시 확인' 하면 다시 가져온다
    private Task<IReadOnlyList<string>>? _ollamaModels;

    // 받기 전에 보여 줄 모델 크기 (ollama.com 레지스트리). 모델 이름마다 한 번 가져온다
    private readonly Dictionary<string, Task<long?>> _downloadSizes = new(StringComparer.OrdinalIgnoreCase);

    // Ollama 모델 받기 (ollama pull 대신)
    private Task? _pull;
    private CancellationTokenSource? _pullCts;
    private volatile OllamaPullProgress? _pullProgress;

    // GPU 에 올라간 상태 (/api/ps). 탭을 보는 동안 몇 초마다 다시 가져온다. 새로 가져오는 동안에는 지난 값을 보여 준다
    private const double LoadedRefreshSeconds = 3;
    private Task<OllamaLoadedModel?>? _loaded;
    private DateTime _loadedAt;
    private (bool Known, OllamaLoadedModel? Model) _lastLoaded;
    private Task? _unload;

    private Configuration Config => plugin.Configuration;
    private TranslationQueue Queue => plugin.TranslationQueue;

    public void Draw()
    {
        var config = Config;

        ImGui.TextUnformatted("번역 도구");
        var current = Engines.FirstOrDefault(e => e.Engine == config.Engine);
        ImGui.SetNextItemWidth(Layout.ComboWidth(Engines.Select(e => e.Name).Append(NotChosen)));
        using (var combo = ImRaii.Combo("##engine", current.Name ?? NotChosen))
        {
            if (combo.Success)
            {
                foreach (var (engine, name, description) in Engines)
                {
                    if (ImGui.Selectable($"{name} — {description}", engine == config.Engine) && engine != config.Engine)
                    {
                        config.Engine = engine;
                        config.Save();
                        Queue.Reload();
                        // 새 도구로 최근 대화를 다시 번역한다. 이전 번역은 남겨 두고 모델 이름으로 구분한다
                        _ = Queue.RetranslateRecentAsync(plugin.ChatListener.Snapshot(), RetranslateCount);
                    }
                }
            }
        }
        DrawEngineStatus(Queue);
        if (config.Engine is not null && Queue.Status != EngineStatus.Checking)
        {
            Layout.SameLineIfFits(Layout.ButtonWidth("다시 확인"));
            if (ImGui.Button("다시 확인"))
                Recheck();
        }

        ImGuiHelpers.ScaledDummy(8);

        switch (config.Engine)
        {
            case TranslationEngine.Claude:
                DrawClaude(config.Claude);
                break;
            case TranslationEngine.Codex:
                DrawCodex(config.Codex);
                break;
            case TranslationEngine.Ollama:
                DrawOllama(config.Ollama);
                break;
            case TranslationEngine.Papago:
                DrawPapago();
                break;
            default:
                DrawChooseGuide();
                return;
        }

        if (config.Engine != TranslationEngine.Papago)
        {
            ImGuiHelpers.ScaledDummy(8);
            DrawGlossary(config);
        }
    }

    public void Dispose()
    {
        _pullCts?.Cancel();
    }

    /// <summary>아직 고르지 않았을 때: 무엇이 필요하고 어떤 차이가 있는지</summary>
    private static void DrawChooseGuide()
    {
        HelpText("받은 일본어 채팅을 한국어로, 쓴 한국어를 일본어로 옮길 도구를 위에서 골라 주세요. " +
                 "가지고 있는 구독이나 PC 사양에 맞는 것을 고르면 됩니다.");
        ImGuiHelpers.ScaledDummy(4);
        foreach (var (_, name, description) in Engines)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            WrappedText.Draw($"{name} — {description}");
        }
        ImGuiHelpers.ScaledDummy(4);
        HelpText("나중에 언제든 바꿀 수 있습니다. 바꾸면 최근 대화 몇 줄을 새 도구로 다시 번역해 비교할 수 있습니다.");
    }

    private void DrawClaude(ClaudeSettings s)
    {
        Heading("준비");
        HelpText("Claude Pro·Max처럼 Claude Code를 쓸 수 있는 Claude 구독이 필요합니다. " +
                 "Claude Code를 설치한 뒤 위의 '로그인'을 누르면 새 창과 브라우저가 열려 구독 계정으로 로그인합니다 " +
                 "(터미널의 claude auth login 과 같습니다). 창이 닫히면 자동으로 다시 확인합니다.");
        LinkButton("Claude Code 설치 안내 열기", ClaudeSetupUrl);
        HelpText("받은 채팅과 보낼 글(보낸 사람 이름은 빼고)이 Anthropic으로 전송되고, Claude 구독 사용량을 씁니다. " +
                 "사용량이 걱정되면 설정 탭의 '번역할 채널'을 줄여 주세요.");

        ImGuiHelpers.ScaledDummy(6);
        DrawExecutable("claude", "claude.exe", s.ExecutablePath, v => s.ExecutablePath = v, ref _claudeFound, ClaudeCliLocator.Find);
        DrawTextSetting("모델", "claudeModel", "haiku", s.Model, v => s.Model = v ?? "", ["haiku", "sonnet", "opus"]);
        HelpText("haiku를 권장합니다 — 빠르고 사용량이 적습니다. sonnet·opus는 더 정확할 수 있지만 느리고 사용량을 더 씁니다.");
        DrawChoice("생각 정도 (effort)", "claudeEffort", s.Effort, v => s.Effort = v,
            [(null, "기본"), ("low", "low"), ("medium", "medium"), ("high", "high")]);
        DrawTimeout("claudeTimeout", s.TimeoutSeconds, v => s.TimeoutSeconds = v);
    }

    private void DrawCodex(CodexSettings s)
    {
        Heading("준비");
        HelpText("Codex를 쓸 수 있는 ChatGPT 구독(Plus·Pro 등)이 필요합니다. " +
                 "Codex CLI를 설치한 뒤 위의 '로그인'을 누르면 새 창과 브라우저가 열려 ChatGPT 계정으로 로그인합니다 " +
                 "(터미널의 codex login 과 같습니다). 창이 닫히면 자동으로 다시 확인합니다.");
        LinkButton("Codex CLI 설치 안내 열기", CodexSetupUrl);
        HelpText("받은 채팅과 보낼 글(보낸 사람 이름은 빼고)이 OpenAI로 전송되고, ChatGPT 구독 사용량을 씁니다. " +
                 "경량 모드는 코딩용 지침과 도구를 줄여 입력 토큰을 절약합니다. 같은 문구는 최근 256개까지 메모리에 보관해 재요청 없이 번역합니다.");

        ImGuiHelpers.ScaledDummy(6);
        DrawExecutable("codex", "codex.exe", s.ExecutablePath, v => s.ExecutablePath = v, ref _codexFound, CodexCliLocator.Find);
        var lightweight = s.Lightweight;
        if (ImGui.Checkbox("경량 번역 (권장)", ref lightweight))
        {
            s.Lightweight = lightweight;
            Apply();
        }
        HelpText($"Codex CLI {CodexCliTranslator.LightweightMinVersion.ToString(3)} 이상에서 쓰고, 이전 CLI면 자동으로 기존 방식으로 번역합니다. " +
                 "개인 Codex 설정(config.toml)을 읽지 않으며 로그인은 그대로 사용합니다. " +
                 "사용할 모델은 아래에 지정하세요. 개인 설정이 필요하면 경량 번역을 끄세요.");
        if (s.Lightweight && Queue.Translator is CodexCliTranslator { LightweightOffReason: { } reason })
            WrappedText.Draw(reason, ErrorColor);
        DrawTextSetting("모델", "codexModel", "비우면 Codex 기본 모델", s.Model, v => s.Model = v, []);
        DrawChoice("생각 정도 (reasoning effort)", "codexEffort", s.ReasoningEffort, v => s.ReasoningEffort = v ?? "low",
            [("low", "low (권장)"), ("medium", "medium"), ("high", "high")]);
        DrawTimeout("codexTimeout", s.TimeoutSeconds, v => s.TimeoutSeconds = v);
    }

    private void DrawOllama(OllamaSettings s)
    {
        DrawOllamaSetup(s);

        ImGuiHelpers.ScaledDummy(6);
        // 연결하지 못했던 목록은 상태 줄을 눌러 다시 확인해 준비되면 다시 가져온다
        if (_ollamaModels is { IsFaulted: true } && Queue.Status == EngineStatus.Ready)
            _ollamaModels = null;
        _ollamaModels ??= OllamaTranslator.ListModelsAsync(s.Endpoint);

        if (DrawTextSetting("주소", "ollamaEndpoint", new OllamaOptions().Endpoint, s.Endpoint, v => s.Endpoint = v ?? ""))
            _ollamaModels = null; // 다른 Ollama 의 모델 목록을 다시 가져온다

        var installed = _ollamaModels is { IsCompletedSuccessfully: true } t ? t.Result : [];
        // 목록에는 받아 둔 모델과 권장 모델을 함께 둔다 — 아직 아무것도 받지 않았어도 고를 수 있게
        var choices = installed.Concat(RecommendedOllamaModels.Where(r => !installed.Any(m => OllamaTranslator.SameModel(m, r)))).ToList();
        // 이 PC 의 Ollama 일 때만 이 PC 의 그래픽카드로 권한다 (다른 PC 의 Ollama 면 그 PC 의 카드가 중요하다)
        var local = OllamaModels.IsLocal(s.Endpoint);
        var recommended = local ? ThisPc.Model : null;
        if (DrawTextSetting("모델", "ollamaModel", recommended ?? OllamaModels.LargeModel, s.Model, v => s.Model = v ?? "", choices,
                m => OllamaTranslator.SameModel(m, recommended ?? "") ? $"{m}  (이 PC 권장)" : m))
            ResetLoaded();
        var isInstalled = DrawOllamaModelState(s, installed, recommended);
        if (local)
        {
            HelpText(ThisPc.Gpu is { } gpu
                ? $"이 PC의 그래픽카드: {gpu} → {ThisPc.Model}를 권합니다."
                : $"이 PC에서 Ollama가 쓸 수 있는 그래픽카드(NVIDIA·AMD 외장)를 찾지 못해 CPU로 돌립니다 → {ThisPc.Model}를 권합니다." +
                  (GpuInfo.Adapters.Count > 0 ? $" (찾은 그래픽: {string.Join(", ", GpuInfo.Adapters)})" : ""));
        }
        HelpText($"{OllamaModels.LargeModel}(받을 때 약 8.2GB)는 한↔일 게임 용어까지 맞게 옮깁니다. 그래픽카드 메모리가 12GB 이상일 때 권합니다 " +
                 "(게임도 그래픽카드 메모리를 쓰므로). " +
                 $"{OllamaModels.MidModel}(약 5.1GB)는 그 사이로, 게임 용어는 12b와 비슷하게 맞고 메모리는 약 5.4GB를 씁니다. " +
                 "8GB 이상일 때 권합니다. 비상업 라이선스(CC-BY-NC 4.0)라 개인 용도로만 쓸 수 있습니다. " +
                 $"{OllamaModels.SmallModel}(약 3.4GB)는 GPU 없이 CPU만으로도 한 줄에 약 3초, 처음 올릴 때 약 17초였습니다 " +
                 "(내장 그래픽 노트북 실측). gemma3:1b 처럼 더 작은 모델은 번역을 제대로 하지 못했습니다.");

        if (isInstalled)
        {
            ImGuiHelpers.ScaledDummy(4);
            DrawOllamaGpuState(s);
        }

        ImGuiHelpers.ScaledDummy(4);
        var preload = s.PreloadOnCheck;
        if (ImGui.Checkbox("미리 GPU에 올려 두기", ref preload))
        {
            s.PreloadOnCheck = preload;
            Apply();
        }
        HelpText("켜 두면 플러그인을 켤 때·도구를 바꿀 때 모델을 GPU에 올려 첫 번역이 빠릅니다. " +
                 "끄면 첫 번역 때 올리므로 그 전까지 GPU 메모리를 쓰지 않습니다(대신 첫 번역이 몇 초 늦어집니다).");

        DrawTextSetting("모델 유지 시간 (keep_alive)", "ollamaKeepAlive", new OllamaOptions().KeepAlive, s.KeepAlive,
            v => s.KeepAlive = v ?? "", ["5m", "30m", "1h", "-1"]);
        HelpText("번역 사이에 모델을 GPU 메모리에 올려 두는 시간입니다. 짧으면 GPU 메모리를 빨리 비우지만 다음 번역이 몇 초 늦어집니다. -1은 계속 올려 둡니다.");
        DrawTimeout("ollamaTimeout", s.TimeoutSeconds, v => s.TimeoutSeconds = v);
    }

    /// <summary>
    /// 모델 목록을 가져오는 중인지, 고른 모델을 받아 두었는지. 받아 두지 않았으면 크기·디스크 여유를 보여 주고 여기서 받을 수 있다.
    /// 받아 둔 모델이면 true
    /// </summary>
    private bool DrawOllamaModelState(OllamaSettings s, IReadOnlyList<string> installed, string? recommended)
    {
        using var indent = ImRaii.PushIndent();

        if (_pull is { IsCompleted: false })
        {
            DrawPullProgress(s);
            return false;
        }
        if (_pull is { IsFaulted: true } failed)
            WrappedText.Draw(failed.Exception?.InnerException?.Message ?? "모델을 받지 못했습니다.", ErrorColor);

        switch (_ollamaModels)
        {
            case null or { IsCompleted: false }:
                HelpText("받아 둔 모델 확인 중…");
                return false;
            case { IsFaulted: true } faulted:
                WrappedText.Draw(faulted.Exception?.InnerException?.Message ?? "Ollama에 연결하지 못했습니다.", ErrorColor);
                return false;
        }

        if (installed.Any(m => OllamaTranslator.SameModel(m, s.Model)))
        {
            HelpText("받아 둔 모델입니다.");
            return true;
        }

        WrappedText.Draw($"{s.Model} 모델을 아직 받지 않았습니다.", ErrorColor);
        // 큰 모델을 이 PC 에서 돌리면 CPU 로 돌아 한 줄에 수십 초 걸릴 수 있다 — 받기 전에 알린다
        if (recommended is not null && OllamaModels.Tier(s.Model) > OllamaModels.Tier(recommended))
            WrappedText.Draw($"이 PC의 그래픽카드로는 {s.Model}가 매우 느릴 수 있습니다 — {recommended}를 권합니다.", ErrorColor);

        // 받기 전에 크기와 디스크 여유를 알린다 — 수 GB 라 모르고 받으면 디스크가 찰 수 있다
        if (!_downloadSizes.TryGetValue(s.Model, out var sizeTask))
            _downloadSizes[s.Model] = sizeTask = OllamaModels.GetDownloadSizeAsync(s.Model);
        if (sizeTask.IsFaulted)
        {
            // 레지스트리에 없는 이름 — 받아도 실패하므로 버튼을 두지 않는다
            WrappedText.Draw(sizeTask.Exception?.InnerException?.Message ?? "모델을 찾지 못했습니다.", ErrorColor);
            return false;
        }

        var size = sizeTask.IsCompletedSuccessfully ? sizeTask.Result : null;
        var free = OllamaModels.FreeDiskBytes(s.Endpoint);
        var info = sizeTask.IsCompleted
            ? size is { } sz ? $"내려받을 크기 약 {OllamaModels.FormatSize(sz)}" : "내려받을 크기를 알 수 없습니다 (수 GB일 수 있습니다)"
            : "내려받을 크기 확인 중…";
        if (free is { } f)
            info += $" · 디스크 여유 {OllamaModels.FormatSize(f)}";
        HelpText(info);

        // 받는 동안 임시 파일도 생기므로 1GB 여유를 더 둔다
        var lowDisk = size is { } need && free is { } have && have < need + 1_000_000_000;
        if (lowDisk)
            WrappedText.Draw($"디스크 여유가 모자랍니다 — 모델 폴더({OllamaModels.ModelsDirectory})가 있는 드라이브의 공간을 비워 주세요.", ErrorColor);

        using (ImRaii.Disabled(lowDisk))
        {
            if (ImGui.Button("지금 받기##pull"))
                StartPull(s, size);
        }
        return false;
    }

    /// <summary>받는 중: 전체 진행 막대와 취소</summary>
    private void DrawPullProgress(OllamaSettings s)
    {
        var p = _pullProgress;
        var label = p switch
        {
            null or { Status: "pulling manifest" } => "목록 받는 중…",
            { Fraction: { } fraction } when p.Status.StartsWith("pulling", StringComparison.Ordinal) =>
                $"{fraction:P0}  ({OllamaModels.FormatSize(p.Completed)} / {OllamaModels.FormatSize(p.Total)})",
            { Status: "verifying sha256 digest" } => "받은 파일 확인 중…",
            { Status: "writing manifest" or "removing any unused layers" } => "마무리 중…",
            { Status: "success" } => "다 받았습니다",
            _ => p.Status,
        };
        HelpText($"{s.Model} 받는 중");
        ImGui.ProgressBar((float)(p?.Fraction ?? 0), new Vector2(Layout.FitWidth(InputWidth), 0), label);
        if (ImGui.Button("취소##pull"))
            _pullCts?.Cancel();
        Layout.SameLineIfFits(Layout.TextWidth("취소해도 받은 부분은 남아, 다시 받으면 이어서 받습니다"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(DimColor, "취소해도 받은 부분은 남아, 다시 받으면 이어서 받습니다");
    }

    private void StartPull(OllamaSettings s, long? expectedSize)
    {
        _pullCts?.Cancel();
        _pullCts = new CancellationTokenSource();
        _pullProgress = null;
        var endpoint = s.Endpoint;
        var model = s.Model;
        var ct = _pullCts.Token;
        _pull = Task.Run(async () =>
        {
            await OllamaModels.PullAsync(endpoint, model, expectedSize, p => _pullProgress = p, ct);
            // 받았으면 목록과 상태를 다시 확인한다 — 다음 프레임의 그리기에서 목록을 다시 가져온다
            _ollamaModels = null;
            await Plugin.Framework.RunOnFrameworkThread(Queue.Reload);
        }, ct);
    }

    /// <summary>
    /// GPU 에 올라가 있는지, 다 올라갔는지(일부가 CPU 에서 돌면 몇 배 느리다), 언제 내리는지. 바로 내릴 수도 있다
    /// </summary>
    private void DrawOllamaGpuState(OllamaSettings s)
    {
        Heading("GPU 상태");

        if (_loaded is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _lastLoaded = (true, done.Result);
            if ((DateTime.UtcNow - _loadedAt).TotalSeconds >= LoadedRefreshSeconds)
                _loaded = null;
        }
        if (_loaded is null)
        {
            _loaded = OllamaModels.GetLoadedAsync(s.Endpoint, s.Model);
            _loadedAt = DateTime.UtcNow;
        }

        using var indent = ImRaii.PushIndent();
        var (known, loaded) = _lastLoaded;
        if (!known)
        {
            HelpText("확인 중…");
            return;
        }
        if (loaded is null)
        {
            HelpText("올라가 있지 않습니다 — 다음 번역 때 올립니다 (처음 몇 초 더 걸립니다).");
            return;
        }

        var until = loaded.ExpiresAt is { } at && at - DateTimeOffset.Now is var left && left < TimeSpan.FromDays(1)
            ? $" · {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분 뒤 내림"
            : " · 계속 올려 둠";
        if (loaded.PartlyOnCpu)
        {
            WrappedText.Draw(loaded.SizeVram > 0
                ? $"GPU에 {loaded.GpuFraction:P0}만 올라가고 나머지는 CPU에서 돕니다 ({OllamaModels.FormatSize(loaded.Size)}){until}. " +
                  "번역이 몇 배 느려질 수 있습니다 — GPU 메모리가 모자라면 더 작은 모델을 권합니다."
                : $"GPU를 쓰지 못하고 CPU에서만 돕니다 ({OllamaModels.FormatSize(loaded.Size)}){until}. " +
                  "GPU가 없거나 Ollama가 지원하지 않는 GPU(내장 그래픽 등)입니다 — 느리다면 gemma3:4b를 권합니다.", ErrorColor);
        }
        else
        {
            HelpText($"GPU에 모두 올라가 있습니다 ({OllamaModels.FormatSize(loaded.Size)}){until}.");
        }

        using (ImRaii.Disabled(_unload is { IsCompleted: false }))
        {
            if (ImGui.Button("지금 내리기##unload"))
            {
                var endpoint = s.Endpoint;
                var model = s.Model;
                _unload = Task.Run(async () =>
                {
                    await OllamaModels.UnloadAsync(endpoint, model);
                    _loaded = null; // 바로 다시 확인한다
                });
            }
        }
        Layout.SameLineIfFits(Layout.TextWidth("GPU 메모리를 바로 비웁니다. 다음 번역 때 다시 올립니다"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(DimColor, "GPU 메모리를 바로 비웁니다. 다음 번역 때 다시 올립니다");
    }

    // ── Ollama 설치·실행 ─────────────────────────────────────────

    // 최신 릴리스 (GitHub). 설치하지 않았거나 '업데이트 확인' 을 눌렀을 때만 가져온다
    private Task<OllamaRelease>? _latestRelease;
    private bool _updateCheckRequested;
    private bool? _includeRocm;

    // 서버 버전 (/api/version) — 켜져 있는지. 탭을 보는 동안 몇 초마다 다시 본다
    private Task<string?>? _serverVersion;
    private DateTime _serverVersionAt;
    private (bool Known, string? Version) _lastServerVersion;

    private Task? _ollamaHousekeeping; // 지우기·앱 닫기 (뒤에서)
    private string? _ollamaSetupError;

    /// <summary>
    /// Ollama 가 설치·실행 중인지와 그에 맞는 할 일: 설치(플러그인이 공식 zip 으로), 업데이트·지우기, 공식 앱의 시작프로그램 끄기
    /// </summary>
    private void DrawOllamaSetup(OllamaSettings s)
    {
        Heading("준비");
        HelpText("무료이고, 이 PC(또는 아래 주소의 PC)에서 번역하므로 채팅이 인터넷으로 나가지 않습니다. 번역하는 동안 게임과 GPU를 나눠 씁니다.");

        if (!OllamaModels.IsLocal(s.Endpoint))
        {
            HelpText("다른 PC의 Ollama를 씁니다 — 그 PC에서 Ollama를 켜 두세요.");
            return;
        }

        var host = plugin.OllamaHost;
        using var indent = ImRaii.PushIndent();

        if (host.IsInstalling)
        {
            DrawOllamaInstallProgress(host);
            return;
        }
        if (host.InstallError is { } installError)
            WrappedText.Draw(installError, ErrorColor);
        if (_ollamaSetupError is { } setupError)
            WrappedText.Draw(setupError, ErrorColor);

        PollServerVersion(s.Endpoint);
        var (known, version) = _lastServerVersion;
        if (known && version is not null)
        {
            HelpText(host.OwnsRunningServer
                ? $"Ollama {version} 실행 중 — 플러그인이 창 없이 켰고, 게임을 끄면 같이 꺼집니다."
                : $"Ollama {version} 실행 중 — 다른 프로그램(Ollama 앱 등)이 켠 것을 그대로 씁니다.");
        }
        else if (host.IsAnyInstalled)
        {
            HelpText("Ollama가 꺼져 있습니다 — 번역할 때 플러그인이 창 없이 켭니다.");
        }
        else if (known)
        {
            WrappedText.Draw("Ollama가 설치되어 있지 않습니다.", ErrorColor);
            DrawOllamaInstallOffer(host, currentVersion: null);
            ImGuiHelpers.ScaledDummy(2);
            HelpText("공식 설치 프로그램으로 설치해도 됩니다. 그때는 설치 직후 Ollama 앱의 환영 화면이 뜨는데, 닫아도 번역에는 상관없습니다.");
            LinkButton("Ollama 공식 사이트 열기", OllamaDownloadUrl);
            return;
        }

        if (host.IsManagedInstalled)
            DrawManagedOllama(host);
        if (OllamaServer.IsOfficialInstalled)
            DrawOfficialOllamaStartup(host);
    }

    private void PollServerVersion(string endpoint)
    {
        if (_serverVersion is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
                _lastServerVersion = (true, done.Result);
            if ((DateTime.UtcNow - _serverVersionAt).TotalSeconds >= LoadedRefreshSeconds)
                _serverVersion = null;
        }
        if (_serverVersion is null)
        {
            _serverVersion = OllamaServer.GetVersionAsync(endpoint);
            _serverVersionAt = DateTime.UtcNow;
        }
    }

    /// <summary>설치 또는 업데이트 제안: 받을 크기·디스크 여유, AMD 구성 요소, 버튼</summary>
    private void DrawOllamaInstallOffer(OllamaHost host, string? currentVersion)
    {
        _latestRelease ??= OllamaInstaller.GetLatestAsync();
        if (!_latestRelease.IsCompleted)
        {
            HelpText("최신 버전 확인 중…");
            return;
        }
        if (_latestRelease.IsFaulted)
        {
            WrappedText.Draw(_latestRelease.Exception?.InnerException?.Message ?? "최신 버전을 확인하지 못했습니다.", ErrorColor);
            if (ImGui.Button("다시 시도##release"))
                _latestRelease = null;
            return;
        }

        var release = _latestRelease.Result;
        if (currentVersion is not null && release.Tag == currentVersion)
        {
            HelpText("최신 버전입니다.");
            return;
        }
        if (release.Find(OllamaInstaller.MainAssetName) is not { } main)
        {
            WrappedText.Draw($"{release.Tag} 에 이 PC용 파일이 없습니다.", ErrorColor);
            return;
        }

        var rocm = release.Find(OllamaInstaller.RocmAssetName);
        _includeRocm ??= rocm is not null && OllamaInstaller.HasAmdGpu();
        var includeRocm = _includeRocm.Value && rocm is not null;
        var size = main.Size + (includeRocm ? rocm!.Size : 0);
        // 받은 zip 과 푼 파일이 잠깐 같이 있다 (풀면 zip 의 2배 남짓)
        var need = size * 3;
        var free = FreeBytes(host.InstallRoot);
        HelpText($"{release.Tag} · 받을 크기 약 {OllamaModels.FormatSize(size)}" +
                 (free is { } f ? $" · 디스크 여유 {OllamaModels.FormatSize(f)}" : ""));

        if (rocm is not null)
        {
            var rocmChecked = _includeRocm.Value;
            if (ImGui.Checkbox($"AMD 그래픽카드용 구성 요소도 받기 (+{OllamaModels.FormatSize(rocm.Size)})", ref rocmChecked))
                _includeRocm = rocmChecked;
        }

        var lowDisk = free is { } have && have < need;
        if (lowDisk)
            WrappedText.Draw($"디스크 여유가 모자랍니다 — 약 {OllamaModels.FormatSize(need)}가 필요합니다.", ErrorColor);

        using (ImRaii.Disabled(lowDisk))
        {
            if (ImGui.Button(currentVersion is null ? "Ollama 설치##install" : $"{release.Tag}(으)로 업데이트##install"))
            {
                _ollamaSetupError = null;
                host.StartInstall(release, includeRocm, onDone: Recheck);
            }
        }
        HelpText("Ollama 공식 GitHub에서 받아 검증(SHA-256)한 뒤 플러그인 폴더에 둡니다. 설치 프로그램·환영 화면·작업 표시줄 아이콘 없이, " +
                 "번역할 때만 창 없이 켜고 게임을 끄면 같이 끕니다.");
    }

    private static void DrawOllamaInstallProgress(OllamaHost host)
    {
        var p = host.InstallProgress;
        HelpText("Ollama 설치 중");
        var label = p is null ? "준비 중…" : p.Fraction is { } fraction ? $"{p.Stage}  {fraction:P0}" : p.Stage;
        ImGui.ProgressBar((float)(p?.Fraction ?? 0), new Vector2(Layout.FitWidth(InputWidth), 0), label);
        if (ImGui.Button("취소##install"))
            host.CancelInstall();
        Layout.SameLineIfFits(Layout.TextWidth("취소해도 받은 부분은 남아, 다시 설치하면 이어서 받습니다"));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(DimColor, "취소해도 받은 부분은 남아, 다시 설치하면 이어서 받습니다");
    }

    /// <summary>플러그인이 설치한 Ollama: 버전, 업데이트 확인, 지우기</summary>
    private void DrawManagedOllama(OllamaHost host)
    {
        var version = host.ManagedVersion ?? "(버전 모름)";
        HelpText($"플러그인이 설치한 Ollama {version}");

        if (_updateCheckRequested)
        {
            DrawOllamaInstallOffer(host, version);
        }
        else if (ImGui.Button("업데이트 확인##ollama"))
        {
            _updateCheckRequested = true;
            _latestRelease = null; // 새로 가져온다
        }

        Layout.SameLineIfFits(Layout.ButtonWidth("지우기##ollama"));
        using (ImRaii.Disabled(_ollamaHousekeeping is { IsCompleted: false }))
        {
            if (ImGui.Button("지우기##ollama"))
            {
                _ollamaSetupError = null;
                _ollamaHousekeeping = Task.Run(async () =>
                {
                    try
                    {
                        await host.UninstallAsync();
                    }
                    catch (TranslationException e)
                    {
                        _ollamaSetupError = e.Message;
                    }
                    await Plugin.Framework.RunOnFrameworkThread(Recheck);
                });
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("플러그인이 설치한 Ollama를 지웁니다. 받아 둔 모델은 남습니다");
    }

    /// <summary>공식 설치 프로그램으로 깐 Ollama 앱: Windows 를 켤 때 앱도 켤지 (끄면 지금 켜진 앱도 닫는다)</summary>
    private void DrawOfficialOllamaStartup(OllamaHost host)
    {
        var enabled = OllamaStartup.IsEnabled;
        if (!enabled && !OllamaStartup.CanEnable(host.StartupBackupDir))
            return; // 원래 시작프로그램에 없었다 — 바꿀 것이 없다

        using (ImRaii.Disabled(_ollamaHousekeeping is { IsCompleted: false }))
        {
            if (ImGui.Checkbox("Windows를 켤 때 Ollama 앱도 켜기", ref enabled))
            {
                _ollamaSetupError = null;
                var turnOn = enabled;
                _ollamaHousekeeping = Task.Run(async () =>
                {
                    try
                    {
                        if (turnOn)
                        {
                            OllamaStartup.Enable(host.StartupBackupDir);
                            return;
                        }
                        OllamaStartup.Disable(host.StartupBackupDir);
                        OllamaStartup.CloseApp(); // 지금 켜진 앱(작업 표시줄 아이콘)도 닫는다 — 이제 플러그인이 창 없이 켠다
                    }
                    catch (TranslationException e)
                    {
                        _ollamaSetupError = e.Message;
                    }
                    await Plugin.Framework.RunOnFrameworkThread(Recheck);
                });
            }
        }
        HelpText("Ollama 공식 설치 프로그램이 켜 둔 설정입니다. 끄면 Windows를 켤 때 Ollama 앱(작업 표시줄 아이콘)이 뜨지 않고, " +
                 "지금 켜져 있는 앱도 닫습니다. 그 뒤에는 번역할 때 플러그인이 창 없이 켜고, 게임을 끄면 같이 끕니다.");
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private void ResetLoaded()
    {
        _loaded = null;
        _lastLoaded = default;
    }

    private static void DrawPapago()
    {
        Heading("준비");
        HelpText("준비할 것이 없습니다. 바로 쓸 수 있습니다.");
        ImGuiHelpers.ScaledDummy(4);
        WrappedText.Draw("알아 두실 점", ErrorColor);
        HelpText("· 받은 채팅과 보낼 글(보낸 사람 이름은 빼고)이 Naver로 전송됩니다.");
        HelpText("· 공식 API가 아니라 papago.naver.com 웹 화면이 쓰는 주소를 부릅니다. Naver가 바꾸거나 막으면 동작하지 않고, " +
                 "많이 쓰면 잠시 제한됩니다.");
        HelpText("· 용어집을 쓸 수 없어 게임 용어가 틀리게 옮겨질 수 있습니다. 특히 보내기(한→일)에서는 '다시 한국어로' 확인에도 " +
                 "드러나지 않는 오역이 생길 수 있어, 보내기에는 LLM 도구(Claude·Codex·Ollama)를 권합니다.");
    }

    private void DrawGlossary(Configuration config)
    {
        Heading("용어집");
        HelpText("게임 용어를 한국 서버 유저들이 쓰는 말로 옮기게 하는 표입니다. 번역할 때 그 줄에 나온 용어만 함께 보내므로, " +
                 "용어가 많아도 번역마다 쓰는 토큰은 거의 늘지 않습니다.");
        var size = new Vector2(Layout.FitWidth(InputWidth), ImGui.GetTextLineHeight() * 8 + ImGui.GetStyle().FramePadding.Y * 2);

        var useDefault = config.UseDefaultGlossary;
        if (ImGui.Checkbox($"기본 용어 쓰기 ({Glossary.Default.Count}개)", ref useDefault))
        {
            config.UseDefaultGlossary = useDefault;
            Apply();
        }
        using (ImRaii.PushIndent())
        {
            HelpText("플러그인과 함께 배포되며, 업데이트하면 함께 늘어납니다.");
            using var node = ImRaii.TreeNode("기본 용어 보기");
            if (node.Success)
            {
                // 고칠 수 없게 하되 글을 골라 복사할 수 있게 입력칸으로 보여 준다. 길이 한도는 UTF-8 바이트 수다
                var defaults = DefaultGlossaryText;
                ImGui.InputTextMultiline("##defaultGlossary", ref defaults, System.Text.Encoding.UTF8.GetByteCount(defaults) + 1,
                    size, ImGuiInputTextFlags.ReadOnly);
            }
        }

        ImGuiHelpers.ScaledDummy(4);
        ImGui.TextUnformatted("내 용어");
        HelpText("한 줄에 '일본어 → 한국어' 하나. 기본 용어와 일본어가 같으면 내 용어가 우선합니다. " +
                 "보낼 때(한→일)는 한국어 쪽으로 찾아 거꾸로 씁니다.");
        var text = config.PersonalGlossary;
        if (ImGui.InputTextMultiline("##personalGlossary", ref text, 8000, size))
            config.PersonalGlossary = text;
        if (ImGui.IsItemDeactivatedAfterEdit())
            Apply();

        if (_parsedPersonalText != config.PersonalGlossary)
        {
            _parsedPersonalText = config.PersonalGlossary;
            _parsedPersonal = Glossary.Parse(config.PersonalGlossary);
        }
        if (_parsedPersonal.InvalidLines > 0)
            WrappedText.Draw($"'→' 가 없거나 한쪽이 빈 줄 {_parsedPersonal.InvalidLines}개는 쓰지 않습니다.", ErrorColor);
    }

    /// <summary>기본 용어 보기 칸에 보여 줄 텍스트 (플러그인을 켜 있는 동안 바뀌지 않는다)</summary>
    private static readonly string DefaultGlossaryText = Glossary.Default.ToText();

    // 내 용어를 읽은 결과. 글이 바뀔 때만 다시 읽는다
    private string? _parsedPersonalText;
    private Glossary _parsedPersonal = Glossary.Empty;

    /// <summary>
    /// 실행 파일 경로. 비우면 자동으로 찾고 찾은 경로를 보여 준다. 게임이 PATH 를 다르게 보거나 특이한 곳에 설치했을 때 직접 넣는다
    /// </summary>
    private void DrawExecutable(
        string id, string exeName, string? configured, Action<string?> set, ref Found found, Func<string?> find)
    {
        DrawTextSetting("실행 파일", $"{id}Path", "비우면 자동으로 찾습니다", configured, set);

        using var indent = ImRaii.PushIndent();
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!found.Searched)
                found = new Found(true, find());
            if (found.Path is { } path)
                HelpText($"자동으로 찾음: {path}");
            else
                WrappedText.Draw($"{exeName}를 찾지 못했습니다 — 설치했다면 위에 경로를 직접 넣어 주세요.", ErrorColor);
        }
        else if (!File.Exists(CliLocation.Normalize(configured)))
        {
            WrappedText.Draw("이 경로에 파일이 없습니다.", ErrorColor);
        }
        else if (CliLocation.IsScriptWrapper(configured.Trim().Trim('"')))
        {
            WrappedText.Draw(CliLocation.ScriptWrapperMessage, ErrorColor);
        }
    }

    /// <summary>
    /// 이름 줄 + 글 입력칸. presets 가 있으면 옆에 ▼ 를 두어 고를 수 있게 한다.
    /// 입력은 칸을 벗어날 때(또는 목록에서 고를 때) 저장하고 엔진을 다시 만든다 — 글자마다 다시 만들지 않게. 바꿨으면 true
    /// </summary>
    private bool DrawTextSetting(
        string label, string id, string hint, string? value, Action<string?> set, IReadOnlyList<string>? presets = null,
        Func<string, string>? describe = null)
    {
        ImGui.TextUnformatted(label);
        var changed = false;
        var buffer = value ?? "";
        var arrow = presets is { Count: > 0 } ? ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X : 0;
        ImGui.SetNextItemWidth(Math.Max(Layout.FitWidth(InputWidth) - arrow, 50 * ImGuiHelpers.GlobalScale));
        if (ImGui.InputTextWithHint($"##{id}", hint, ref buffer, 260))
            set(EngineSettings.OrNull(buffer));
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Apply();
            changed = true;
        }

        if (presets is { Count: > 0 })
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            using var combo = ImRaii.Combo($"##{id}Presets", "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.PopupAlignLeft);
            if (combo.Success)
            {
                foreach (var preset in presets)
                {
                    if (ImGui.Selectable(describe?.Invoke(preset) ?? preset, preset == value) && preset != value)
                    {
                        set(preset);
                        Apply();
                        changed = true;
                    }
                }
            }
        }
        return changed;
    }

    private void DrawChoice(string label, string id, string? value, Action<string?> set, (string? Value, string Label)[] choices)
    {
        ImGui.TextUnformatted(label);
        var current = choices.FirstOrDefault(c => c.Value == value);
        ImGui.SetNextItemWidth(Layout.ComboWidth(choices.Select(c => c.Label)));
        using var combo = ImRaii.Combo($"##{id}", current.Label ?? value ?? "");
        if (!combo.Success)
            return;
        foreach (var (choice, choiceLabel) in choices)
        {
            if (ImGui.Selectable(choiceLabel, choice == value) && choice != value)
            {
                set(choice);
                Apply();
            }
        }
    }

    private void DrawTimeout(string id, int seconds, Action<int> set)
    {
        ImGui.TextUnformatted("제한 시간");
        var value = Math.Clamp(seconds, EngineSettings.MinTimeoutSeconds, EngineSettings.MaxTimeoutSeconds);
        ImGui.SetNextItemWidth(Layout.FitWidth(TimeoutSliderWidth));
        if (ImGui.SliderInt($"##{id}", ref value, EngineSettings.MinTimeoutSeconds, EngineSettings.MaxTimeoutSeconds, "%d초"))
            set(value);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Apply();
        Layout.SameLineIfFits(Layout.TextWidth("이 시간 안에 답이 없으면 그 줄은 실패로 둡니다"));
        ImGui.TextColored(DimColor, "이 시간 안에 답이 없으면 그 줄은 실패로 둡니다");
    }

    private static void Heading(string text) => ImGui.TextUnformatted(text);

    private static void LinkButton(string label, string url)
    {
        if (ImGui.Button(label))
            Util.OpenLink(url);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(url);
    }

    /// <summary>설정을 저장하고, 바뀐 설정으로 엔진을 다시 만들어 상태를 확인한다</summary>
    private void Apply()
    {
        Config.Save();
        Queue.Reload();
    }

    /// <summary>실행 파일을 다시 찾고, Ollama 모델 목록을 다시 가져오고, 상태를 다시 확인한다</summary>
    private void Recheck()
    {
        _claudeFound = default;
        _codexFound = default;
        _ollamaModels = null;
        _downloadSizes.Clear();
        ResetLoaded();
        _serverVersion = null;
        _lastServerVersion = default;
        _updateCheckRequested = false;
        Queue.Reload();
    }

    private record struct Found(bool Searched, string? Path);
}
