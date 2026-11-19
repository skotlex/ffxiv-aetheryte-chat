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

    private const string CommandName = "/achat";

    private readonly WindowSystem _windowSystem = new("AetheryteChat");
    private readonly MainWindow _mainWindow;

    internal Configuration Configuration { get; }
    internal ChatListener ChatListener { get; }
    internal TranslationQueue TranslationQueue { get; }

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
        TranslationQueue = new TranslationQueue(CreateTranslator, channel => TranslateChannels.IsEnabled(Configuration, channel));
        ChatListener = new ChatListener(ChatGui, TranslationQueue.Enqueue);
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
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindows;
        // 플러그인 목록의 설정(톱니) 버튼 — 번역 도구 탭을 연다
        PluginInterface.UiBuilder.OpenConfigUi += _mainWindow.OpenEngineTab;
    }

    /// <summary>받은 채팅 창을 열고 닫는다. 보내기가 별도 창이면 그 창도 같이 열고 닫는다</summary>
    private void ToggleWindows()
    {
        _mainWindow.Toggle();
        if (Configuration.SendPanelPlacement == SendPanelPlacement.SeparateWindow)
            SendWindow.IsOpen = _mainWindow.IsOpen;
    }

    /// <summary>설정의 번역 도구와 그 도구의 설정으로 엔진을 만든다</summary>
    private ITranslator CreateTranslator()
    {
        var glossary = Configuration.BuildGlossary();

        // Ollama 를 이 PC 에서 쓰지 않게 되면 플러그인이 켠 서버는 끈다 (메모리를 돌려준다)
        var usesLocalOllama = Configuration.Engine == TranslationEngine.Ollama && OllamaModels.IsLocal(Configuration.Ollama.Endpoint);
        if (!usesLocalOllama)
            OllamaHost.StopServerInBackground();

        return Configuration.Engine switch
        {
            TranslationEngine.Claude => new ClaudeCliTranslator(Configuration.Claude.ToOptions(glossary)),
            TranslationEngine.Codex => new CodexCliTranslator(Configuration.Codex.ToOptions(glossary)),
            TranslationEngine.Ollama => CreateOllamaTranslator(glossary),
            TranslationEngine.Papago => new PapagoWebTranslator(),
            _ => new NoEngineTranslator(),
        };
    }

    private OllamaTranslator CreateOllamaTranslator(Glossary glossary)
    {
        var options = Configuration.Ollama.ToOptions(glossary);
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
        if (Configuration.Engine != TranslationEngine.Ollama || OllamaHost.OwnsRunningServer)
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
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindows;
        PluginInterface.UiBuilder.OpenConfigUi -= _mainWindow.OpenEngineTab;
        CommandManager.RemoveHandler(CommandName);

        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
        ChatListener.Dispose();
        TranslationQueue.Dispose();
        UnloadOllamaModel();
        OllamaHost.Dispose();
        WindowFont.Dispose();
    }
}
