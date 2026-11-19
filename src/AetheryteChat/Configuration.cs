using Dalamud.Configuration;
using TermGlossary = AetheryteChat.Translation.Glossary;

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

    /// <summary>
    /// 번역 도구. null 이면 아직 고르지 않았다 — 처음 설치한 사람에게 '번역 도구' 탭을 열어 고르게 한다.
    /// 사람마다 쓸 수 있는 도구(구독·GPU)가 달라 하나를 기본으로 정해 두지 않는다
    /// </summary>
    public TranslationEngine? Engine { get; set; }

    public ClaudeSettings Claude { get; set; } = new();
    public CodexSettings Codex { get; set; } = new();
    public OllamaSettings Ollama { get; set; } = new();

    /// <summary>플러그인에 들어 있는 기본 용어를 쓸지. 끄면 내 용어만 쓴다</summary>
    public bool UseDefaultGlossary { get; set; } = true;

    /// <summary>내 용어 (한 줄에 '일본어 → 한국어'). 기본 용어와 일본어가 같으면 이쪽이 우선한다</summary>
    public string PersonalGlossary { get; set; } = "";

    /// <summary>
    /// 버전 0 의 용어집: null 이면 기본 용어집, 빈 문자열이면 용어집 없음, 그 밖은 기본 용어집을 고친 전체 텍스트.
    /// 이렇게 두면 한 번 고친 사람은 기본 용어집이 늘어도 받지 못해 버전 1 에서 나눴다. 옮길 때만 읽고 null 로 비운다
    /// </summary>
    public string? Glossary { get; set; }

    /// <summary>
    /// 번역하지 않을 채널 묶음. 끈 것만 적어, 나중에 묶음이 늘어도 새 묶음은 기본으로 켜진다
    /// </summary>
    public HashSet<TranslateChannelGroup> DisabledTranslateChannels { get; set; } = [];

    /// <summary>
    /// 개발용: 번역 품질 개선용 표본 기록을 남길 폴더 (<see cref="SampleLog"/>). 설정 화면에는 없고 설정 파일에 직접 적는다.
    /// 비어 있으면 아무것도 기록하지 않는다
    /// </summary>
    public string? SampleLogDirectory { get; set; }

    /// <summary>보내기 위치. 기본은 받은 채팅 아래</summary>
    public SendPanelPlacement SendPanelPlacement { get; set; } = SendPanelPlacement.BelowChat;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    public const int CurrentVersion = 1;

    /// <summary>번역에 쓸 용어집: 기본 용어(켜 두었으면) + 내 용어</summary>
    internal TermGlossary BuildGlossary() =>
        TermGlossary.Merge(UseDefaultGlossary ? TermGlossary.Default : TermGlossary.Empty, TermGlossary.Parse(PersonalGlossary));

    /// <summary>이전 버전의 설정을 지금 형식으로 옮긴다. 바꿨으면 true (저장은 부르는 쪽이 한다)</summary>
    internal bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        if (Version < 1)
        {
            if (Glossary == "")
            {
                UseDefaultGlossary = false; // '용어집 없음' 이던 사람은 그대로 용어 없이 번역한다
            }
            else if (Glossary is not null)
            {
                // 기본 용어와 똑같은 줄은 빼고 나머지(고치거나 덧붙인 줄)를 내 용어로 옮긴다
                var defaults = TermGlossary.Default.Entries.ToHashSet();
                PersonalGlossary = string.Join('\n', Glossary.ReplaceLineEndings("\n").Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !(TermGlossary.ParseLine(l) is { } e && defaults.Contains(e))));
            }
            Glossary = null;
        }

        Version = CurrentVersion;
        return true;
    }
}
