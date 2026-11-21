using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using static AetheryteChat.Windows.UiCommon;

namespace AetheryteChat.Windows;

/// <summary>
/// 받은 채팅(원문 + 한국어 번역)과 설정 창. 보내기 위치가 '채팅 아래' 면 받은 채팅 아래에 보내기를 붙인다.
/// </summary>
internal sealed class MainWindow : StyledWindow, IDisposable
{
    private readonly ChannelColors _channelColors = new(Plugin.GameConfig);
    private readonly EngineTab _engineTab;

    // 다음 프레임에 '번역 도구' 탭을 연다 — 아직 번역 도구를 고르지 않았거나 플러그인 목록의 설정 버튼을 눌렀을 때
    private bool _selectEngineTab;

    // 지난 프레임에 잰 보내기 묶음의 높이 — 받은 채팅 로그가 그만큼 비켜 준다 (보내기 높이는 번역 확인 줄에 따라 바뀐다)
    private float _sendPanelHeight;

    public MainWindow(Plugin plugin) : base(plugin, "Aetheryte Chat##main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        _engineTab = new EngineTab(plugin);
        _selectEngineTab = plugin.Configuration.Engine is null;
        IsOpen = plugin.Configuration.MainWindowOpen;
    }

    // 창을 열고 닫을 때(/achat·닫기 버튼·단축키) 기억해 두었다가 다음에 플러그인을 켤 때 그대로 연다.
    // 게임을 끄거나 플러그인을 내릴 때는 불리지 않아 마지막 상태가 남는다
    public override void OnOpen() => RememberOpen(true);

    public override void OnClose() => RememberOpen(false);

    private void RememberOpen(bool open)
    {
        if (Owner.Configuration.MainWindowOpen == open)
            return;
        Owner.Configuration.MainWindowOpen = open;
        Owner.Configuration.Save();
    }

    /// <summary>창을 열고 '번역 도구' 탭을 보여 준다</summary>
    public void OpenEngineTab()
    {
        IsOpen = true;
        _selectEngineTab = true;
    }

    public void Dispose() => _engineTab.Dispose();

    protected override void DrawContent()
    {
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        var sendBelow = Owner.Configuration.SendPanelPlacement == SendPanelPlacement.BelowChat;
        // 보이는 이름은 바뀌어도 탭 ID(### 뒤)는 같게 해 보내기 위치를 바꿔도 탭 선택이 풀리지 않게 한다
        // 단축키로 입력칸에 가려는데 보내기가 채팅 탭에 붙어 있으면 채팅 탭을 연다 (다른 탭을 보고 있었어도)
        var chatTabFlags = sendBelow && Owner.SendPanel.FocusPending ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using (var tab = ImRaii.TabItem(sendBelow ? "채팅###chat" : "받은 채팅###chat", chatTabFlags))
        {
            if (tab.Success)
                DrawChat(sendBelow);
        }
        var engineTabFlags = _selectEngineTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        _selectEngineTab = false;
        using (var tab = ImRaii.TabItem("번역 도구###engine", engineTabFlags))
        {
            if (tab.Success)
                _engineTab.Draw();
        }
        using (var tab = ImRaii.TabItem("설정###settings"))
        {
            if (tab.Success)
                DrawSettings();
        }
        using (var tab = ImRaii.TabItem("스타일###style"))
        {
            if (tab.Success)
                DrawStyleSettings();
        }
    }

    private void DrawChat(bool sendBelow)
    {
        var queue = Owner.TranslationQueue;

        if (ImGui.Button("지우기"))
            Owner.ChatListener.Clear();
        Layout.SameLineIfFits(Layout.CheckboxWidth("자동 번역"));
        var auto = queue.AutoTranslate;
        if (ImGui.Checkbox("자동 번역", ref auto))
            queue.AutoTranslate = auto;
        // 보내기를 별도 창으로 두었는데 그 창을 닫았으면 다시 여는 버튼
        if (!sendBelow && !Owner.SendWindow.IsOpen)
        {
            Layout.SameLineIfFits(Layout.ButtonWidth("보내기 창 열기"));
            if (ImGui.Button("보내기 창 열기"))
                Owner.SendWindow.IsOpen = true;
        }
        DrawEngineStatus(queue, rightAlign: true);

        // 보내기를 아래에 붙이면 로그는 그 높이만큼 남기고 나머지를 채운다 (음수 높이 = 남은 높이에서 뺀 만큼)
        using (var child = ImRaii.Child("##log", new Vector2(0, sendBelow ? -_sendPanelHeight : 0), true))
        {
            if (child.Success)
                DrawLog();
        }

        if (sendBelow)
        {
            var top = ImGui.GetCursorPosY();
            Owner.SendPanel.Draw(compact: true);
            _sendPanelHeight = ImGui.GetCursorPosY() - top;
        }
    }

    private void DrawLog()
    {
        foreach (var line in Owner.ChatListener.Snapshot())
        {
            // 원문은 게임 채팅창과 같은 채널 색(채널은 색으로 구분), 번역은 구분되게 따로 정한 색으로 그린다
            var original = $"[{line.Time:HH:mm}] {line.Sender}: {line.Message}";
            if (_channelColors.Get(line.Channel) is { } channelColor)
                WrappedText.Draw(original, channelColor);
            else
                WrappedText.Draw(original);

            // 번역 도구를 바꾸면 번역이 더해진다 — 한 줄에 번역이 둘 이상일 때만 어느 모델의 번역인지 [모델] 로 구분한다
            using var indent = ImRaii.PushIndent();
            var translations = line.Translations;
            var showEngine = translations.Count > 1;
            foreach (var t in translations)
            {
                var tag = showEngine ? $"[{t.Engine}] " : "";
                switch (t.State)
                {
                    case TranslationState.Pending:
                        WrappedText.Draw($"… {tag}번역 중", DimColor);
                        break;
                    case TranslationState.Done:
                        WrappedText.Draw($"→ {tag}{t.Result}", TranslationColor);
                        break;
                    case TranslationState.Failed:
                        WrappedText.Draw($"{tag}번역 실패: {t.Result}", ErrorColor);
                        break;
                    case TranslationState.Skipped:
                        WrappedText.Draw($"{tag}{t.Result}", DimColor);
                        break;
                }
            }
        }

        // 맨 아래에 있으면 새 줄을 따라간다
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);
    }

    private static readonly (SendPanelPlacement Placement, string Label)[] Placements =
    [
        (SendPanelPlacement.BelowChat, "채팅 아래"),
        (SendPanelPlacement.SeparateWindow, "별도 창"),
    ];

    /// <summary>보내기를 받은 채팅 아래에 붙일지, 별도 창으로 뗄지</summary>
    private void DrawSendPlacement(Configuration config)
    {
        ImGui.TextUnformatted("보내기 위치");
        var current = Placements.FirstOrDefault(p => p.Placement == config.SendPanelPlacement);
        ImGui.SetNextItemWidth(Layout.ComboWidth(Placements.Select(p => p.Label)));
        using var combo = ImRaii.Combo("##sendPlacement", current.Label ?? config.SendPanelPlacement.ToString());
        if (!combo.Success)
            return;
        foreach (var (placement, label) in Placements)
        {
            if (ImGui.Selectable(label, placement == config.SendPanelPlacement) && placement != config.SendPanelPlacement)
            {
                config.SendPanelPlacement = placement;
                config.Save();
                Owner.SendWindow.IsOpen = placement == SendPanelPlacement.SeparateWindow;
            }
        }
    }

    private static string ModifierLabel(VirtualKey key) => key switch
    {
        VirtualKey.NO_KEY => "없음",
        VirtualKey.CONTROL => "Ctrl",
        VirtualKey.SHIFT => "Shift",
        VirtualKey.MENU => "Alt",
        _ => key.GetFancyName(),
    };

    private static string KeyLabel(VirtualKey key) => key == VirtualKey.NO_KEY ? "끔" : key.GetFancyName();

    /// <summary>단축키 목록에서 뺄 키: 같이 누르는 키(왼쪽·오른쪽 따로인 것 포함)와 마우스 버튼</summary>
    private static readonly HashSet<VirtualKey> NotHotkeys =
    [
        VirtualKey.CONTROL, VirtualKey.SHIFT, VirtualKey.MENU,
        VirtualKey.LCONTROL, VirtualKey.RCONTROL, VirtualKey.LSHIFT, VirtualKey.RSHIFT, VirtualKey.LMENU, VirtualKey.RMENU,
        VirtualKey.LBUTTON, VirtualKey.RBUTTON, VirtualKey.MBUTTON, VirtualKey.XBUTTON1, VirtualKey.XBUTTON2,
    ];

    /// <summary>게임 화면에서 한국어 입력칸으로 가는 단축키: [같이 누르는 키] + [키]</summary>
    private static void DrawFocusHotkey(Configuration config)
    {
        ImGui.TextUnformatted("한국어 입력칸 단축키");

        ImGui.SetNextItemWidth(Layout.ComboWidth(Plugin.HotkeyModifiers.Select(ModifierLabel)));
        using (var combo = ImRaii.Combo("##hotkeyModifier", ModifierLabel(config.FocusInputModifier)))
        {
            if (combo.Success)
            {
                foreach (var modifier in Plugin.HotkeyModifiers)
                {
                    if (ImGui.Selectable(ModifierLabel(modifier), modifier == config.FocusInputModifier) && modifier != config.FocusInputModifier)
                    {
                        config.FocusInputModifier = modifier;
                        config.Save();
                    }
                }
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("+");
        ImGui.SameLine();

        var keys = Plugin.KeyState.GetValidVirtualKeys().Where(k => !NotHotkeys.Contains(k)).Prepend(VirtualKey.NO_KEY).Distinct().ToList();
        ImGui.SetNextItemWidth(Layout.FitWidth(160 * ImGuiHelpers.GlobalScale));
        using (var combo = ImRaii.Combo("##hotkeyKey", KeyLabel(config.FocusInputKey)))
        {
            if (combo.Success)
            {
                foreach (var key in keys)
                {
                    if (ImGui.Selectable(KeyLabel(key), key == config.FocusInputKey) && key != config.FocusInputKey)
                    {
                        config.FocusInputKey = key;
                        config.Save();
                    }
                }
            }
        }

        WrappedText.Draw(
            "게임 화면에서 누르면 보내기 창을 열고 한국어 입력칸으로 갑니다. 입력칸에서 한 번 더 누르거나 글을 보내면 " +
            "입력칸에서 빠져나와 바로 게임을 조작할 수 있습니다. 같이 누르는 키 없이 글자 키를 고르면 게임 채팅에 그 글자를 쓸 때도 반응합니다.",
            DimColor);
    }

    /// <summary>게임 채팅창이 보일 때만 이 창을 보일지</summary>
    private static void DrawShowOnlyWithGameChat(Configuration config)
    {
        var only = config.ShowOnlyWithGameChat;
        if (ImGui.Checkbox("게임 채팅창이 보일 때만 창 표시", ref only))
        {
            config.ShowOnlyWithGameChat = only;
            config.Save();
        }
        WrappedText.Draw(
            "로딩 화면, 타이틀·캐릭터 선택 화면처럼 게임 채팅창이 사라지는 동안은 이 창을 숨깁니다. " +
            "컷신·단체 사진(GPose)·UI 숨기기 중에는 이 설정과 상관없이 숨겨집니다.",
            DimColor);
    }

    /// <summary>알트탭으로 돌아오면 한/영 상태를 되돌릴지</summary>
    private static void DrawKeepImeMode(Configuration config)
    {
        var keep = config.KeepImeModeOnRefocus;
        if (ImGui.Checkbox("게임에 돌아오면 한/영 상태 되돌리기", ref keep))
        {
            config.KeepImeModeOnRefocus = keep;
            config.Save();
        }
        WrappedText.Draw(
            "Windows 11 의 한국어 입력기는 알트탭으로 게임에 돌아올 때마다 영어로 바뀝니다. 켜 두면 게임을 떠나기 전의 " +
            "한/영 상태로 되돌립니다.",
            DimColor);
    }

    private void DrawSettings()
    {
        var config = Owner.Configuration;

        DrawSendPlacement(config);

        ImGuiHelpers.ScaledDummy(8);

        DrawShowOnlyWithGameChat(config);

        ImGuiHelpers.ScaledDummy(8);

        DrawFocusHotkey(config);

        ImGuiHelpers.ScaledDummy(8);

        DrawKeepImeMode(config);

        ImGuiHelpers.ScaledDummy(8);

        DrawTranslateChannels(config);
    }

    /// <summary>
    /// 번역할 채널 묶음마다 체크박스. 끈 채널의 대화도 채팅 기록에는 그대로 나오고 번역만 하지 않는다.
    /// 폭에 맞춰 옆으로 늘어놓고, 자리가 없으면 다음 줄로 내린다
    /// </summary>
    private static void DrawTranslateChannels(Configuration config)
    {
        ImGui.TextUnformatted("번역할 채널");
        var first = true;
        foreach (var (group, label, _) in TranslateChannels.Groups)
        {
            if (!first)
                Layout.SameLineIfFits(Layout.CheckboxWidth(label));
            first = false;

            var enabled = !config.DisabledTranslateChannels.Contains(group);
            if (ImGui.Checkbox($"{label}##translate{group}", ref enabled))
            {
                if (enabled)
                    config.DisabledTranslateChannels.Remove(group);
                else
                    config.DisabledTranslateChannels.Add(group);
                config.Save();
            }
        }
    }

    /// <summary>스타일 탭: 이 플러그인 창들에만 적용하는 폰트·창 스타일·색·불투명도</summary>
    private void DrawStyleSettings()
    {
        var config = Owner.Configuration;

        DrawFontSettings(config);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextUnformatted("창 스타일");
        const string followDalamud = "Dalamud 설정 따르기";
        ImGui.SetNextItemWidth(Layout.ComboWidth(Owner.WindowStyles.All.Select(s => s.Name).Append(followDalamud)));
        using (var combo = ImRaii.Combo("##windowStyle", config.WindowStyleName ?? followDalamud))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(followDalamud, config.WindowStyleName is null) && config.WindowStyleName is not null)
                {
                    config.WindowStyleName = null;
                    config.Save();
                }
                foreach (var style in Owner.WindowStyles.All)
                {
                    if (ImGui.Selectable(style.Name, style.Name == config.WindowStyleName) && style.Name != config.WindowStyleName)
                    {
                        config.WindowStyleName = style.Name;
                        config.Save();
                    }
                }
            }
        }
        // 고른 스타일을 Dalamud 스타일 편집기에서 가져올 수 있는 문자열로 복사 — 이 플러그인 내장 스타일을 게임 전체 창에 쓰려고
        var selectedStyle = Owner.WindowStyles.Find(config.WindowStyleName);
        if (selectedStyle is not null)
        {
            const string copyLabel = "Dalamud 용으로 복사";
            Layout.SameLineIfFits(Layout.ButtonWidth(copyLabel));
            if (ImGui.Button(copyLabel))
                ImGui.SetClipboardText(selectedStyle.Serialize());
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("이 스타일을 클립보드에 복사합니다. Dalamud 설정 > 스타일 편집기에서 클립보드에서 가져오기를 누르면 다른 Dalamud 창에도 쓸 수 있습니다");
        }
        if (config.WindowStyleName is not null && selectedStyle is null)
            WrappedText.Draw($"'{config.WindowStyleName}' 스타일을 찾지 못해 Dalamud 설정을 따릅니다.", ErrorColor);
        ImGuiHelpers.ScaledDummy(8);

        DrawColorSettings(config);
    }

    /// <summary>
    /// 배경·컨트롤마다 [색] [불투명도] [초기화] 이름. 맨 위 일괄 불투명도는 배경과, 글자를 뺀 모든 묶음의 불투명도를 한 번에 맞춘다.
    /// 지정하지 않은 값은 지금 스타일 값에서 시작한다 (Draw 안이라 고른 스타일·정해 둔 값이 이미 적용돼 있다)
    /// </summary>
    private static void DrawColorSettings(Configuration config)
    {
        ImGui.TextUnformatted("색·불투명도");

        // 일괄 불투명도 — 움직이면 각 줄의 불투명도를 이 값으로 맞춘다. 맞춘 뒤 줄마다 따로 다듬을 수 있다
        var master = (config.MasterOpacity ?? 1f) * 100f;
        ImGui.SetNextItemWidth(Layout.FitWidth(OpacitySliderWidth));
        if (ImGui.SliderFloat("##masterAlpha", ref master, 0f, 100f, "%.0f%%"))
        {
            var value = Math.Clamp(master / 100f, 0f, 1f);
            config.MasterOpacity = value;
            config.BackgroundOpacity = value;
            foreach (var (group, _) in CustomColors.Groups)
            {
                if (group != ControlColorGroup.Text)
                    config.ControlOpacity[group] = value;
            }
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();
        Layout.SameLineIfFits(Layout.ButtonWidth("초기화##master"));
        // 불투명도만 되돌린다 — 골라 둔 색은 줄마다의 초기화로만 지운다
        var anyOpacity = config.MasterOpacity is not null || config.BackgroundOpacity is not null || config.ControlOpacity.Count > 0;
        using (ImRaii.Disabled(!anyOpacity))
        {
            if (ImGui.Button("초기화##master"))
            {
                config.MasterOpacity = null;
                config.BackgroundOpacity = null;
                config.ControlOpacity.Clear();
                config.Save();
            }
        }
        const string masterLabel = "일괄 불투명도 (글자 제외)";
        Layout.SameLineIfFits(Layout.TextWidth(masterLabel));
        ImGui.TextUnformatted(masterLabel);

        ImGuiHelpers.ScaledDummy(2);

        // 배경: 색은 WindowBg, 불투명도는 Window.BgAlpha (PreDraw)
        var styleBg = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        DrawColorRow(
            "bg", "배경",
            config.BackgroundColor ?? new Vector3(styleBg.X, styleBg.Y, styleBg.Z),
            config.BackgroundOpacity ?? styleBg.W,
            config.BackgroundColor is not null || config.BackgroundOpacity is not null,
            c => config.BackgroundColor = c,
            a => config.BackgroundOpacity = a,
            () =>
            {
                config.BackgroundColor = null;
                config.BackgroundOpacity = null;
            },
            config);

        foreach (var (group, label) in CustomColors.Groups)
        {
            var current = CustomColors.CurrentBase(group);
            var hasColor = config.ControlColors.TryGetValue(group, out var rgb);
            var hasOpacity = config.ControlOpacity.TryGetValue(group, out var opacity);
            DrawColorRow(
                group.ToString(), label,
                hasColor ? rgb : new Vector3(current.X, current.Y, current.Z),
                hasOpacity ? opacity : current.W,
                hasColor || hasOpacity,
                c => config.ControlColors[group] = c,
                a => config.ControlOpacity[group] = a,
                () =>
                {
                    config.ControlColors.Remove(group);
                    config.ControlOpacity.Remove(group);
                },
                config);
        }

    }

    /// <summary>
    /// 이 창의 폰트와 크기. Dalamud 의 폰트 선택 창(설치된 Windows 폰트·Dalamud 폰트·게임 폰트, 크기 포함)으로 고른다
    /// </summary>
    private void DrawFontSettings(Configuration config)
    {
        ImGui.TextUnformatted("폰트");
        // 폰트 이름은 길어서 자기 줄에서 줄바꿈하고, 버튼은 그 아래에 둔다
        using (ImRaii.PushIndent())
            WrappedText.Draw(config.WindowFont is { } spec ? spec.ToLocalizedString("ko-KR") : "Dalamud 기본", DimColor);
        using (ImRaii.Disabled(_fontChooserOpen))
        {
            if (ImGui.Button("폰트 고르기"))
                OpenFontChooser(config);
        }
        Layout.SameLineIfFits(Layout.ButtonWidth("초기화##font"));
        using (ImRaii.Disabled(config.WindowFont is null))
        {
            if (ImGui.Button("초기화##font"))
            {
                config.WindowFont = null;
                config.Save();
                Owner.WindowFont.Change(null);
            }
        }
    }

    private bool _fontChooserOpen;

    private void OpenFontChooser(Configuration config)
    {
        // CreateAuto 는 창을 알아서 그리고 닫히면 스스로 정리한다
        var chooser = SingleFontChooserDialog.CreateAuto((UiBuilder)Plugin.PluginInterface.UiBuilder);
        chooser.Title = "Aetheryte Chat 폰트";
        chooser.PreviewText = "가나다 한글 테스트 — 零式4層、初見です ABC 123";
        // 기본은 모달(BeginPopupModal)이라 ImGui 가 뒤의 다른 창 전체를 밝은 반투명 막(ModalWindowDimBg)으로 덮는다.
        // 일반 창으로 연다 — 열려 있는 동안 '폰트 고르기' 버튼은 막혀 있어 두 개가 뜨지 않는다
        chooser.IsModal = false;
        if (config.WindowFont is { } current)
            chooser.SelectedFont = current;
        _fontChooserOpen = true;

        chooser.ResultTask.ContinueWith(t =>
        {
            _fontChooserOpen = false;
            if (!t.IsCompletedSuccessfully)
                return; // 취소
            config.WindowFont = t.Result;
            config.Save();
            Owner.WindowFont.Change(t.Result);
        });
    }

    private static float OpacitySliderWidth => 120 * ImGuiHelpers.GlobalScale;

    /// <summary>[색] [불투명도] [초기화] 이름 한 줄. 값을 바꾸면 setColor·setOpacity 로 넣고, 손을 떼면 저장한다</summary>
    private static void DrawColorRow(
        string id, string label, Vector3 color, float opacity, bool customized,
        Action<Vector3> setColor, Action<float> setOpacity, Action reset, Configuration config)
    {
        if (ImGui.ColorEdit3($"##color{id}", ref color, ImGuiColorEditFlags.NoInputs))
            setColor(color);
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        // 폭이 좁으면 슬라이더·초기화·이름을 다음 줄로 내린다
        Layout.SameLineIfFits(Math.Min(OpacitySliderWidth, ImGui.GetContentRegionAvail().X));
        var percent = opacity * 100f;
        ImGui.SetNextItemWidth(Layout.FitWidth(OpacitySliderWidth));
        if (ImGui.SliderFloat($"##alpha{id}", ref percent, 0f, 100f, "%.0f%%"))
            setOpacity(Math.Clamp(percent / 100f, 0f, 1f));
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        Layout.SameLineIfFits(Layout.ButtonWidth("초기화"));
        using (ImRaii.Disabled(!customized))
        {
            if (ImGui.Button($"초기화##reset{id}"))
            {
                reset();
                config.Save();
            }
        }
        Layout.SameLineIfFits(Layout.TextWidth(label));
        ImGui.TextUnformatted(label);
    }

}
