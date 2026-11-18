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

        WindowFont = new WindowFont(PluginInterface.UiBuilder.FontAtlas, Configuration.WindowFont);

        TranslationQueue = new TranslationQueue(CreateTranslator());
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
    }

    /// <summary>받은 채팅 창을 열고 닫는다. 보내기가 별도 창이면 그 창도 같이 열고 닫는다</summary>
    private void ToggleWindows()
    {
        _mainWindow.Toggle();
        if (Configuration.SendPanelPlacement == SendPanelPlacement.SeparateWindow)
            SendWindow.IsOpen = _mainWindow.IsOpen;
    }

    /// <summary>설정의 번역 도구로 엔진을 만든다</summary>
    internal ITranslator CreateTranslator() => Configuration.Engine switch
    {
        TranslationEngine.Codex => new CodexCliTranslator(new CodexCliOptions()),
        TranslationEngine.Papago => new PapagoWebTranslator(),
        // 모델은 고정(OllamaOptions.Model 기본값 gemma3:12b). 더 나은 모델이 나오면 그때 바꾼다 (DESIGN 6.2)
        TranslationEngine.Ollama => new OllamaTranslator(new OllamaOptions()),
        _ => new ClaudeCliTranslator(new ClaudeCliOptions()),
    };

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindows;
        CommandManager.RemoveHandler(CommandName);

        _windowSystem.RemoveAllWindows();
        ChatListener.Dispose();
        TranslationQueue.Dispose();
        WindowFont.Dispose();
    }
}
