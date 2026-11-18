using Dalamud.Configuration;

namespace AetheryteChat;

/// <summary>보내기 화면을 어디에 둘지</summary>
public enum SendPanelPlacement
{
    /// <summary>받은 채팅 아래 (한 화면에서 받기·보내기)</summary>
    BelowChat,

    /// <summary>별도 창 (따로 옮기고 크기를 정할 수 있다)</summary>
    SeparateWindow,
}

public enum TranslationEngine
{
    Claude,
    Codex,
    Papago,
    Ollama,
}

/// <summary>
/// 플러그인 설정. Dalamud 가 %APPDATA%\XIVLauncher\pluginConfigs\AetheryteChat.json 에 저장한다 (저장소에는 들어가지 않음).
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>
    /// 창 배경만의 불투명도(0~1). null 이면 Dalamud 스타일 기본값.
    /// Dalamud 제목줄 메뉴의 불투명도는 글자까지 흐리게 하므로 따로 둔다.
    /// </summary>
    public float? BackgroundOpacity { get; set; }

    /// <summary>
    /// 이 창 배경의 색(RGB 0~1). null 이면 스타일의 배경색. 투명도는 <see cref="BackgroundOpacity"/> 가 맡는다.
    /// Dalamud 스타일에는 저장되지 않고 이 플러그인 창에만 적용된다
    /// </summary>
    public System.Numerics.Vector3? BackgroundColor { get; set; }

    /// <summary>이 창에만 적용하는 컨트롤 색 (묶음마다 기본색 RGB). 없는 묶음은 스타일 색을 따른다</summary>
    public Dictionary<Windows.ControlColorGroup, System.Numerics.Vector3> ControlColors { get; set; } = new();

    /// <summary>이 창에만 적용하는 컨트롤 불투명도(0~1, 묶음마다). 없는 묶음은 스타일 값을 따른다</summary>
    public Dictionary<Windows.ControlColorGroup, float> ControlOpacity { get; set; } = new();

    /// <summary>
    /// 마지막으로 맞춘 일괄 불투명도(0~1). 움직이면 배경과, 글자를 뺀 모든 묶음의 불투명도를 이 값으로 맞춘다.
    /// null 이면 한 번도 맞추지 않음
    /// </summary>
    public float? MasterOpacity { get; set; }

    /// <summary>이 창의 폰트와 크기 (Dalamud 폰트 선택 창에서 고른 값). null 이면 Dalamud 기본 폰트</summary>
    public Dalamud.Interface.FontIdentifier.SingleFontSpec? WindowFont { get; set; }

    /// <summary>이 창에만 적용할 Dalamud 스타일 이름 (스타일 편집기에서 저장한 것 또는 내장). null 이면 Dalamud 설정을 따른다</summary>
    public string? WindowStyleName { get; set; }

    /// <summary>번역 도구. Claude CLI 가 주력이고 Codex CLI 는 느리지만 대안 (DESIGN 4절)</summary>
    public TranslationEngine Engine { get; set; } = TranslationEngine.Claude;

    /// <summary>
    /// 번역하지 않을 채널 묶음. 끈 것만 적어, 나중에 묶음이 늘어도 새 묶음은 기본으로 켜진다
    /// </summary>
    public HashSet<TranslateChannelGroup> DisabledTranslateChannels { get; set; } = [];

    /// <summary>보내기 위치. 기본은 받은 채팅 아래</summary>
    public SendPanelPlacement SendPanelPlacement { get; set; } = SendPanelPlacement.BelowChat;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
