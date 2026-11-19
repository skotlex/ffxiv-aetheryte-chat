using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using AetheryteChat.Translation;
using AetheryteChat.Windows;

namespace AetheryteChat;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

    private const string CommandName = "/achat";

    private readonly WindowSystem _windowSystem = new("AetheryteChat");
    private readonly MainWindow _mainWindow;

    internal Configuration Configuration { get; }
    internal ChatListener ChatListener { get; }
    internal TranslationQueue TranslationQueue { get; }

    /// <summary>개발용 표본 기록. 설정 파일에 SampleLogDirectory 를 적었을 때만 켜진다</summary>
    internal SampleLog SampleLog { get; }

    /// <summary>이 PC 의 Ollama 켜고 끄기·설치</summary>
    internal OllamaHost OllamaHost { get; }

    /// <summary>이 플러그인 창들의 폰트 (설정에서 고른 폰트 + 한글·일본어·게임 기호 보충)</summary>
    internal WindowFont WindowFont { get; }

    /// <summary>창 스타일 목록 (받은 채팅 창·보내기 창이 같이 쓴다)</summary>
    internal WindowStyles WindowStyles { get; } = new();

    /// <summary>보내기 화면과 그 상태. 채팅 아래와 별도 창이 같은 것을 그려, 위치를 바꿔도 입력하던 글이 남는다</summary>
    internal SendPanel SendPanel { get; }

    /// <summary>보내기 위치가 '별도 창' 일 때의 보내기 창</summary>
    internal SendWindow SendWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Migrate())
            Configuration.Save();

        WindowFont = new WindowFont(PluginInterface.UiBuilder.FontAtlas, Configuration.WindowFont);

        OllamaHost = new OllamaHost(PluginInterface.GetPluginConfigDirectory());
        SampleLog = new SampleLog(Configuration.SampleLogDirectory);
        TranslationQueue = new TranslationQueue(
            CreateTranslator, channel => TranslateChannels.IsEnabled(Configuration, channel), SampleLog);
        ChatListener = new ChatListener(ChatGui, OnChatLine, SampleLog.RememberName);
        SendPanel = new SendPanel(this);
        _mainWindow = new MainWindow(this);
        SendWindow = new SendWindow(this);
        _windowSystem.AddWindow(_mainWindow);
        _windowSystem.AddWindow(SendWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => ToggleWindows())
        {
            HelpMessage = "Aetheryte Chat 창을 열고 닫습니다",
        });

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        Framework.Update += CheckFocusHotkey;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindows;
        // 플러그인 목록의 설정(톱니) 버튼 — 번역 도구 탭을 연다
        PluginInterface.UiBuilder.OpenConfigUi += _mainWindow.OpenEngineTab;
    }

    /// <summary>받은 채팅 한 줄 (게임 스레드). 표본 기록을 켰으면 내 캐릭터 이름도 가리게 기억해 둔다</summary>
    private void OnChatLine(ChatLine line)
    {
        if (SampleLog.Enabled)
            SampleLog.RememberName(PlayerState.CharacterName);
        TranslationQueue.Enqueue(line);
    }

    /// <summary>받은 채팅 창을 열고 닫는다. 보내기가 별도 창이면 그 창도 같이 열고 닫는다</summary>
    private void ToggleWindows()
    {
        _mainWindow.Toggle();
        if (Configuration.SendPanelPlacement == SendPanelPlacement.SeparateWindow)
            SendWindow.IsOpen = _mainWindow.IsOpen;
    }

    /// <summary>단축키에 쓸 수 있는 같이 누르는 키 (없음·Ctrl·Shift·Alt)</summary>
    internal static readonly VirtualKey[] HotkeyModifiers = [VirtualKey.NO_KEY, VirtualKey.CONTROL, VirtualKey.SHIFT, VirtualKey.MENU];

    /// <summary>
    /// 게임 스레드에서 매 프레임. 단축키를 누르면 보내기 창을 열고 한국어 입력칸에 포커스를 준다.
    /// 우리 입력칸에 글을 쓰는 동안은 Dalamud 가 키를 게임에 넘기지 않아 여기서 보이지 않는다
    /// </summary>
    private void CheckFocusHotkey(IFramework framework)
    {
        var key = Configuration.FocusInputKey;
        if (key == VirtualKey.NO_KEY || !KeyState.IsVirtualKeyValid(key) || !KeyState[key])
            return;
        // 고른 같이 누르는 키만 눌려 있어야 한다 (Ctrl+Enter 로 정했으면 Ctrl+Shift+Enter 에는 반응하지 않는다)
        foreach (var modifier in HotkeyModifiers)
        {
            if (modifier != VirtualKey.NO_KEY && KeyState[modifier] != (modifier == Configuration.FocusInputModifier))
                return;
        }

        // 게임도 같은 키에 반응하지 않게 눌림을 지운다 (Enter 로 게임 채팅 입력이 같이 열리지 않게)
        KeyState[key] = false;
        FocusSendInput();
    }

    /// <summary>보내기가 보이게 창을 열고, 다음에 그릴 때 한국어 입력칸에 포커스를 준다</summary>
    private void FocusSendInput()
    {
        _mainWindow.IsOpen = true;
        if (Configuration.SendPanelPlacement == SendPanelPlacement.SeparateWindow)
            SendWindow.IsOpen = true;
        SendPanel.RequestFocus();
    }

    /// <summary>
    /// 설정의 번역 도구들(1순위 + 예비 도구)과 각 도구의 설정으로 엔진을 만든다.
    /// 우선순위대로 쓰고 문제가 생기면 다음 도구로 넘어가는 <see cref="FallbackTranslator"/> 로 묶는다
    /// </summary>
    private ITranslator CreateTranslator()
    {
        var chain = Configuration.EngineChain();

        // Ollama 를 이 PC 에서 쓰지 않게 되면 플러그인이 켠 서버는 끈다 (메모리를 돌려준다)
        var usesLocalOllama = chain.Contains(TranslationEngine.Ollama) && OllamaModels.IsLocal(Configuration.Ollama.Endpoint);
        if (!usesLocalOllama)
            OllamaHost.StopServerInBackground();

        if (chain.Count == 0)
            return new NoEngineTranslator();
        var glossary = Configuration.BuildGlossary();
        return new FallbackTranslator(chain.Select((engine, i) =>
            (engine.ToString(), CreateTranslator(engine, glossary, primary: i == 0, hasNext: i < chain.Count - 1))));
    }

    /// <summary>
    /// 뒤에 예비 도구가 있을 때 Claude·Codex 를 기다리는 최대 시간. 설정한 제한 시간이 더 길어도 이만큼 지나면 다음 도구로 넘어간다 —
    /// 평소 2~5초, Codex 가 서버에서 늦을 때도 18초 안이었다(2026-11-19 실측). 막힌 도구의 제한 시간(기본 30·60초)을 줄마다 기다리지 않게.
    /// Ollama 는 빼다 — 처음 모델을 메모리에 올리는 데 CPU 노트북에서 17초쯤 걸린다
    /// </summary>
    internal static readonly TimeSpan BeforeFallbackTimeout = TimeSpan.FromSeconds(20);

    /// <summary>이 도구 뒤에 예비 도구가 있어 제한 시간을 <see cref="BeforeFallbackTimeout"/> 로 줄이는지</summary>
    internal static bool CapsTimeout(TranslationEngine engine, bool hasNext) =>
        hasNext && engine is TranslationEngine.Claude or TranslationEngine.Codex;

    private ITranslator CreateTranslator(TranslationEngine engine, Glossary glossary, bool primary, bool hasNext) => engine switch
    {
        TranslationEngine.Claude => new ClaudeCliTranslator(Cap(Configuration.Claude.ToOptions(glossary), engine, hasNext)),
        TranslationEngine.Codex => new CodexCliTranslator(Cap(Configuration.Codex.ToOptions(glossary), engine, hasNext)),
        TranslationEngine.Ollama => CreateOllamaTranslator(glossary, primary),
        TranslationEngine.Papago => new PapagoWebTranslator(),
        _ => new NoEngineTranslator(),
    };

    private static T Cap<T>(T options, TranslationEngine engine, bool hasNext) where T : TranslatorOptions
    {
        if (CapsTimeout(engine, hasNext) && options.Timeout > BeforeFallbackTimeout)
            options.Timeout = BeforeFallbackTimeout;
        return options;
    }

    /// <param name="primary">
    /// 1순위일 때만 미리 GPU 에 올린다 — 예비 도구로만 둔 Ollama 가 쓰지도 않는 동안 게임의 GPU 메모리를 차지하지 않게
    /// (예비로 넘어오면 첫 번역 때 올린다)
    /// </param>
    private OllamaTranslator CreateOllamaTranslator(Glossary glossary, bool primary)
    {
        var options = Configuration.Ollama.ToOptions(glossary);
        options.Preload &= primary;
        // 꺼져 있으면 창 없이 켠다 (Ollama 앱의 환영 화면·작업 표시줄 아이콘 없이)
        options.EnsureServerAsync = ct => OllamaHost.EnsureServerAsync(options.Endpoint, ct);
        return new OllamaTranslator(options);
    }

    /// <summary>
    /// 게임·플러그인을 끌 때 Ollama 모델을 메모리에서 내린다. 플러그인이 켠 서버는 곧 끄므로 상관없지만,
    /// 사용자가 켜 둔 Ollama 앱의 서버는 계속 돌기 때문에 모델만이라도 내려 GPU 메모리를 돌려준다 (기다리는 것은 최대 2초)
    /// </summary>
    private void UnloadOllamaModel()
    {
        if (!Configuration.EngineChain().Contains(TranslationEngine.Ollama) || OllamaHost.OwnsRunningServer)
            return;
        var settings = Configuration.Ollama.ToOptions(Glossary.Empty);
        try
        {
            OllamaModels.UnloadAsync(settings.Endpoint, settings.Model).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Ollama 가 이미 꺼졌다
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        Framework.Update -= CheckFocusHotkey;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindows;
        PluginInterface.UiBuilder.OpenConfigUi -= _mainWindow.OpenEngineTab;
        CommandManager.RemoveHandler(CommandName);

        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
        ChatListener.Dispose();
        TranslationQueue.Dispose();
        SampleLog.Dispose(); // 받은 채팅·번역이 멈춘 뒤 남은 기록을 쓴다
        UnloadOllamaModel();
        OllamaHost.Dispose();
        WindowFont.Dispose();
    }
}
