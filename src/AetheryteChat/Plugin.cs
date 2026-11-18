using System.Globalization;
using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
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

    private const string CommandName = "/achat";

    private readonly WindowSystem _windowSystem = new("AetheryteChat");
    private readonly MainWindow _mainWindow;

    internal ChatListener ChatListener { get; }
    internal TranslationQueue TranslationQueue { get; }

    /// <summary>
    /// 게임 폰트에는 한글이 없어 ImGui 창에서 따로 불러온다 (DESIGN 3절).
    /// Dalamud 기본 폰트에 Windows 기본 한글 폰트(맑은 고딕) 글리프와, 채팅에 섞여 오는 게임 전용 기호
    /// (자동 번역 괄호·파티 모집 표시·파티 번호 등 U+E0xx) 글리프를 합친다.
    /// </summary>
    internal IFontHandle KoreanFont { get; }

    public Plugin()
    {
        KoreanFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = tk.AddDalamudDefaultFont(-1, null);
            var config = new SafeFontConfig { SizePx = UiBuilder.DefaultFontSizePx, MergeFont = font };
            tk.AttachWindowsDefaultFont(CultureInfo.GetCultureInfo("ko-KR"), in config, 400, 5, 0);
            tk.AddGameSymbol(in config);
        }));

        TranslationQueue = new TranslationQueue(new ClaudeCliTranslator(new ClaudeCliOptions()));
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

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= _mainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);

        _windowSystem.RemoveAllWindows();
        ChatListener.Dispose();
        TranslationQueue.Dispose();
        KoreanFont.Dispose();
    }
}
