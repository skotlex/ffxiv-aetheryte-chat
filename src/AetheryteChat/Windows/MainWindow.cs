using System.Numerics;
using Dalamud.Bindings.ImGui;
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
internal sealed class MainWindow : StyledWindow
{
    private readonly ChannelColors _channelColors = new(Plugin.GameConfig);

    // 지난 프레임에 잰 보내기 묶음의 높이 — 받은 채팅 로그가 그만큼 비켜 준다 (보내기 높이는 번역 확인 줄에 따라 바뀐다)
    private float _sendPanelHeight;

    public MainWindow(Plugin plugin) : base(plugin, "Aetheryte Chat##main")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    protected override void DrawContent()
    {
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        var sendBelow = Owner.Configuration.SendPanelPlacement == SendPanelPlacement.BelowChat;
        // 보이는 이름은 바뀌어도 탭 ID(### 뒤)는 같게 해 보내기 위치를 바꿔도 탭 선택이 풀리지 않게 한다
        using (var tab = ImRaii.TabItem(sendBelow ? "채팅###chat" : "받은 채팅###chat"))
        {
            if (tab.Success)
                DrawChat(sendBelow);
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

            // 번역 도구를 바꾸면 번역이 더해진다 — 어느 모델의 번역인지 [모델] 로 구분한다
            using var indent = ImRaii.PushIndent();
            foreach (var t in line.Translations)
            {
                switch (t.State)
                {
                    case TranslationState.Pending:
                        WrappedText.Draw($"… [{t.Engine}] 번역 중", DimColor);
                        break;
                    case TranslationState.Done:
                        WrappedText.Draw($"→ [{t.Engine}] {t.Result}", TranslationColor);
                        break;
                    case TranslationState.Failed:
                        WrappedText.Draw($"[{t.Engine}] 번역 실패: {t.Result}", ErrorColor);
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

    /// <summary>번역 도구를 바꿀 때 새 도구로 다시 번역할 최근 대화 수</summary>
    private const int RetranslateCount = 10;

    // 목록 상자에는 이름만 보이고(길면 잘린다), 설명은 펼친 목록에만 나온다
    private static readonly (TranslationEngine Engine, string Name, string Description)[] Engines =
    [
        (TranslationEngine.Claude, "Claude CLI", "빠릅니다 (약 2초)"),
        (TranslationEngine.Codex, "Codex CLI", "느리고 (약 5~6초) 토큰을 많이 씁니다"),
        (TranslationEngine.Ollama, "Ollama (로컬)", "가장 빠르지만 (약 1초) GPU를 사용합니다"),
        (TranslationEngine.Papago, "Papago (웹)", "막힐 수 있으며 게임 용어에 약합니다"),
    ];

    private void DrawSettings()
    {
        var config = Owner.Configuration;

        ImGui.TextUnformatted("번역 도구");
        var current = Engines.FirstOrDefault(e => e.Engine == config.Engine);
        ImGui.SetNextItemWidth(Layout.ComboWidth(Engines.Select(e => e.Name)));
        using (var combo = ImRaii.Combo("##engine", current.Name ?? config.Engine.ToString()))
        {
            if (combo.Success)
            {
                foreach (var (engine, name, description) in Engines)
                {
                    if (ImGui.Selectable($"{name} — {description}", engine == config.Engine) && engine != config.Engine)
                    {
                        config.Engine = engine;
                        config.Save();
                        Owner.TranslationQueue.SetTranslator(Owner.CreateTranslator());
                        // 새 도구로 최근 대화를 다시 번역한다. 이전 번역은 남겨 두고 모델 이름으로 구분한다
                        _ = Owner.TranslationQueue.RetranslateRecentAsync(Owner.ChatListener.Snapshot(), RetranslateCount);
                    }
                }
            }
        }
        DrawEngineStatus(Owner.TranslationQueue);

        ImGuiHelpers.ScaledDummy(8);

        DrawSendPlacement(config);

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
        if (config.WindowStyleName is not null && Owner.WindowStyles.Find(config.WindowStyleName) is null)
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
