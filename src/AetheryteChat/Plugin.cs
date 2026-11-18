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

    /// <summary>이 창의 폰트 (설정에서 고른 폰트 + 한글·일본어·게임 기호 보충)</summary>
    internal WindowFont WindowFont { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        WindowFont = new WindowFont(PluginInterface.UiBuilder.FontAtlas, Configuration.WindowFont);

        TranslationQueue = new TranslationQueue(CreateTranslator());
        ChatListener = new ChatListener(ChatGui, TranslationQueue.Enqueue);
        _mainWindow = new MainWindow(this);
        _windowSystem.AddWindow(_mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo((_, _) => _mainWindow.Toggle())
        {
            HelpMessage = "Aetheryte Chat 창을 열고 닫는다",
        });

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += _mainWindow.Toggle;
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
        PluginInterface.UiBuilder.OpenMainUi -= _mainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);

        _windowSystem.RemoveAllWindows();
        ChatListener.Dispose();
        TranslationQueue.Dispose();
        WindowFont.Dispose();
    }
}
