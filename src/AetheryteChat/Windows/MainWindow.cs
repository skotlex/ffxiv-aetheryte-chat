using System.Numerics;
using System.Text;
using AetheryteChat.Translation;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetheryteChat.Windows;

/// <summary>받은 채팅(원문 + 한국어 번역), 보내기(한국어 → 일본어 + 뜻 확인 → 전송), 설정 창.</summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector4 TranslationColor = new(0.55f, 0.85f, 1f, 1f);
    private static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.4f, 1f);

    private readonly Plugin _plugin;
    private readonly ChannelColors _channelColors = new(Plugin.GameConfig);
    private readonly WindowStyles _windowStyles = new();
    private StyleModel? _pushedStyle; // PreDraw 에서 push 한 스타일 — PostDraw 에서 같은 것을 pop 한다
    private int _pushedColorCount;    // PreDraw 에서 push 한 색 개수 (배경 + 컨트롤 색)

    // 보내기 탭 상태. 번역·전송 결과는 백그라운드에서 채워진다 (문자열 대입이라 UI 스레드에서 읽어도 안전)
    private string _koreanInput = "";
    private string _translatedFrom = ""; // 지금 일본어를 만든 한국어 — 입력이 이와 다르면 다시 번역해야 한다
    private string _japanese = "";
    private string _back = "";           // 일본어를 다시 한국어로 옮긴 것 (뜻 확인용)
    private TranslationState _backState = TranslationState.None;
    private int _requestId;              // 새 번역을 시작하면 늘린다 — 늦게 끝난 이전 결과를 버리는 데 쓴다
    private string _channelKey = SendChannel.AutoKey; // 기본: 게임 입력창의 현재 채널
    private string _tellTarget = "";
    private volatile bool _busy;         // 번역 또는 전송 중. 뜻 확인은 보내기를 막지 않는다
    private string _sendStatus = "";
    private bool _sendStatusIsError;

    public MainWindow(Plugin plugin) : base("Aetheryte Chat##main")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void PreDraw()
    {
        // 배경만 투명하게 한다 (Dalamud 제목줄 메뉴의 불투명도는 글자까지 흐려진다)
        BgAlpha = _plugin.Configuration.BackgroundOpacity;

        // 이 창에만 다른 스타일을 쓴다. Dalamud 는 PreDraw 뒤에 현재 스타일에서 제목줄 색을 읽어 push 하고,
        // 창을 닫은(End) 뒤 PostDraw 를 부르므로 여기서 push 하고 PostDraw 에서 pop 하면 이 창만 감싼다 (WindowHost.cs)
        _pushedStyle = _windowStyles.Find(_plugin.Configuration.WindowStyleName);
        _pushedStyle?.Push();

        // 배경색·컨트롤 색은 고른 스타일 위에 덮는다. 알파는 스타일 값을 두고, 배경은 BgAlpha(배경 불투명도)가 다시 정한다
        _pushedColorCount = 0;
        if (_plugin.Configuration.BackgroundColor is { } rgb)
        {
            var alpha = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W;
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(rgb, alpha));
            _pushedColorCount++;
        }
        _pushedColorCount += CustomColors.Push(_plugin.Configuration.ControlColors, _plugin.Configuration.ControlOpacity);
    }

    public override void PostDraw()
    {
        if (_pushedColorCount > 0)
        {
            ImGui.PopStyleColor(_pushedColorCount);
            _pushedColorCount = 0;
        }
        _pushedStyle?.Pop();
        _pushedStyle = null;
    }

    public override void Draw()
    {
        using var font = _plugin.WindowFont.Handle.Push();
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("받은 채팅"))
        {
            if (tab.Success)
                DrawReceived();
        }
        using (var tab = ImRaii.TabItem("보내기"))
        {
            if (tab.Success)
                DrawSend();
        }
        using (var tab = ImRaii.TabItem("설정"))
        {
            if (tab.Success)
                DrawSettings();
        }
    }

    private void DrawReceived()
    {
        var queue = _plugin.TranslationQueue;

        if (ImGui.Button("지우기"))
            _plugin.ChatListener.Clear();
        Layout.SameLineIfFits(Layout.CheckboxWidth("자동 번역"));
        var auto = queue.AutoTranslate;
        if (ImGui.Checkbox("자동 번역", ref auto))
            queue.AutoTranslate = auto;
        DrawEngineStatus(queue);

        using var child = ImRaii.Child("##log", Vector2.Zero, true);
        if (!child.Success)
            return;

        foreach (var line in _plugin.ChatListener.Snapshot())
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

    /// <summary>
    /// 한국어 입력 → Enter(번역) → 일본어와 '다시 한국어로' 확인 → Enter 한 번 더(보내기).
    /// 사용자가 일본어를 읽지 못하므로 일본어는 고칠 수 없게 보여 주고, 뜻은 다시 옮긴 한국어로 확인한다.
    /// </summary>
    private void DrawSend()
    {
        var queue = _plugin.TranslationQueue;

        var channel = DrawChannelSelector();
        var prefix = channel.NeedsTellTarget ? $"/t {_tellTarget.Trim()}" : channel.Prefix;

        ImGui.TextUnformatted("한국어");
        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputText("##ko", ref _koreanInput, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        if (enter)
            ImGui.SetKeyboardFocusHere(-1); // Enter 를 눌러도 입력칸에 머물러 한 번 더 누르면 보내지게 한다

        var korean = _koreanInput.Trim();
        var hasTranslation = _japanese.Length > 0;
        var stale = hasTranslation && korean != _translatedFrom;
        var sendError = !hasTranslation || stale ? null : SendError(channel, prefix);

        var canTranslate = !_busy && queue.Status == EngineStatus.Ready && korean.Length > 0 && (!hasTranslation || stale);
        var canSend = !_busy && hasTranslation && !stale && sendError is null;

        using (ImRaii.Disabled(!canTranslate))
        {
            if (ImGui.Button("번역") && canTranslate)
                Translate(korean);
        }
        Layout.SameLineIfFits(Layout.ButtonWidth("보내기"));
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.Button("보내기") && canSend)
                Send(channel.Label, prefix);
        }
        var enterHint = canSend ? "Enter: 보내기" : "Enter: 번역";
        Layout.SameLineIfFits(Layout.TextWidth(enterHint));
        ImGui.TextColored(DimColor, enterHint);

        if (enter)
        {
            if (canSend)
                Send(channel.Label, prefix);
            else if (canTranslate)
                Translate(korean);
        }

        ImGuiHelpers.ScaledDummy(4);
        DrawTranslationCheck(stale);

        if (sendError is not null)
            ImGui.TextColored(ErrorColor, sendError);
        if (_sendStatus.Length > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, _sendStatusIsError ? ErrorColor : DimColor))
                WrappedText.Draw(_sendStatus);
        }
    }

    private void DrawTranslationCheck(bool stale)
    {
        if (_japanese.Length == 0)
        {
            WrappedText.Draw("번역하면 여기에 일본어와, 그 일본어를 다시 한국어로 옮긴 뜻이 나온다.", DimColor);
            return;
        }

        ImGui.TextColored(DimColor, "보낼 일본어");
        using (ImRaii.PushIndent())
            WrappedText.Draw(_japanese);

        ImGui.TextColored(DimColor, "다시 한국어로 (뜻 확인)");
        using (ImRaii.PushIndent())
        {
            switch (_backState)
            {
                case TranslationState.Pending:
                    ImGui.TextColored(DimColor, "… 확인 중 (기다리지 않고 보내도 된다)");
                    break;
                case TranslationState.Done:
                    using (ImRaii.PushColor(ImGuiCol.Text, TranslationColor))
                        WrappedText.Draw(_back);
                    break;
                case TranslationState.Failed:
                    WrappedText.Draw($"확인 실패: {_back}", ErrorColor);
                    break;
            }
        }

        if (stale)
            ImGui.TextColored(ErrorColor, "한국어가 바뀌었다 — Enter 로 다시 번역한다.");
    }

    private string? SendError(SendChannel channel, string prefix)
    {
        if (channel.NeedsTellTarget && string.IsNullOrWhiteSpace(_tellTarget))
            return "귓속말 받는 사람을 적는다 (이름 성 또는 이름 성@서버).";
        return ChatSender.Validate(prefix, _japanese);
    }

    private void Translate(string korean)
    {
        var id = ++_requestId;
        _busy = true;
        _japanese = "";
        _back = "";
        _backState = TranslationState.None;
        SetStatus("번역 중…", false);

        _plugin.TranslationQueue.TranslateOutgoingAsync(korean).ContinueWith(t =>
        {
            if (id != _requestId)
                return;

            if (t.IsCompletedSuccessfully)
            {
                _translatedFrom = korean;
                _japanese = t.Result.Text;
                SetStatus($"번역됨 ({t.Result.Elapsed.TotalSeconds:0.0}초, {Encoding.UTF8.GetByteCount(_japanese)}바이트).", false);
                CheckMeaning(id, _japanese);
            }
            else if (!t.IsCanceled)
            {
                SetStatus($"번역 실패: {t.Exception?.InnerException?.Message}", true);
            }
            _busy = false;
        });
    }

    private void CheckMeaning(int id, string japanese)
    {
        _backState = TranslationState.Pending;
        _plugin.TranslationQueue.TranslateBackAsync(japanese).ContinueWith(t =>
        {
            if (id != _requestId)
                return;

            if (t.IsCompletedSuccessfully)
            {
                _back = t.Result.Text;
                _backState = TranslationState.Done;
            }
            else if (!t.IsCanceled)
            {
                _back = t.Exception?.InnerException?.Message ?? "";
                _backState = TranslationState.Failed;
            }
        });
    }

    /// <summary>채널 목록을 그리고 고른 채널을 돌려준다. 귓속말이면 받는 사람 칸도 그린다.</summary>
    private SendChannel DrawChannelSelector()
    {
        // 링크셸 가입·탈퇴, 게임 입력창의 채널 변경을 바로 반영하려고 매번 게임에서 읽는다
        var channels = SendChannels.Build();
        var selected = channels.FirstOrDefault(c => c.Key == _channelKey) ?? channels[0];

        ImGui.SetNextItemWidth(Layout.FitWidth(260 * ImGuiHelpers.GlobalScale, "채널"));
        using (var combo = ImRaii.Combo("채널", selected.Label))
        {
            if (combo.Success)
            {
                foreach (var c in channels)
                {
                    if (ImGui.Selectable(c.Label, c.Key == selected.Key) && c.Key != selected.Key)
                    {
                        selected = c;
                        _channelKey = c.Key;
                        // 귓속말로 바꾸면 마지막으로 귓속말을 보낸 사람을 미리 채운다
                        if (c.NeedsTellTarget && _tellTarget.Length == 0)
                            _tellTarget = _plugin.ChatListener.LastTellSender ?? "";
                    }
                }
            }
        }
        DrawEngineStatus(_plugin.TranslationQueue);

        if (selected.NeedsTellTarget)
        {
            ImGui.SetNextItemWidth(Layout.FitWidth(260 * ImGuiHelpers.GlobalScale, "받는 사람"));
            ImGui.InputTextWithHint("받는 사람", "이름 성@서버", ref _tellTarget, 64);
        }
        return selected;
    }

    private void Send(string channelLabel, string prefix)
    {
        var japanese = _japanese.Trim();
        var korean = _translatedFrom;
        _busy = true;
        SetStatus("보내는 중…", false);
        // 받은 채팅에 돌아오는 내 글은 다시 번역하지 않고 한국어 원문을 보여 준다
        _plugin.TranslationQueue.RememberSent(japanese, korean);
        Plugin.Framework.RunOnFrameworkThread(() => ChatSender.Send(prefix, japanese))
            .ContinueWith(t =>
            {
                if (t.Exception?.InnerException is { } e)
                {
                    SetStatus($"보내기 실패: {e.Message}", true);
                    Plugin.Log.Warning(e, "채팅 보내기 실패");
                }
                else
                {
                    SetStatus($"보냄 ({channelLabel}): {korean}", false);
                    _requestId++; // 아직 끝나지 않은 뜻 확인 결과가 다음 입력에 붙지 않게
                    _koreanInput = "";
                    _translatedFrom = "";
                    _japanese = "";
                    _back = "";
                    _backState = TranslationState.None;
                }
                _busy = false;
            });
    }

    /// <summary>번역 도구를 바꿀 때 새 도구로 다시 번역할 최근 대화 수</summary>
    private const int RetranslateCount = 10;

    private static readonly (TranslationEngine Engine, string Label)[] Engines =
    [
        (TranslationEngine.Claude, "Claude CLI — 빠름 (약 2초)"),
        (TranslationEngine.Codex, "Codex CLI — 느림 (약 5~6초), 토큰 많이 씀"),
        (TranslationEngine.Ollama, "Ollama (로컬) — 가장 빠르나 (약 1초) GPU를 사용"),
        (TranslationEngine.Papago, "Papago (웹) — 막힐 수 있으며 게임 용어에 약함"),
    ];

    private void DrawSettings()
    {
        var config = _plugin.Configuration;

        ImGui.TextUnformatted("번역 도구");
        var current = Engines.FirstOrDefault(e => e.Engine == config.Engine);
        ImGui.SetNextItemWidth(Layout.FitWidth(320 * ImGuiHelpers.GlobalScale));
        using (var combo = ImRaii.Combo("##engine", current.Label ?? config.Engine.ToString()))
        {
            if (combo.Success)
            {
                foreach (var (engine, label) in Engines)
                {
                    if (ImGui.Selectable(label, engine == config.Engine) && engine != config.Engine)
                    {
                        config.Engine = engine;
                        config.Save();
                        _plugin.TranslationQueue.SetTranslator(_plugin.CreateTranslator());
                        // 새 도구로 최근 대화를 다시 번역한다. 이전 번역은 남겨 두고 모델 이름으로 구분한다
                        _ = _plugin.TranslationQueue.RetranslateRecentAsync(_plugin.ChatListener.Snapshot(), RetranslateCount);
                    }
                }
            }
        }
        DrawEngineStatus(_plugin.TranslationQueue);

        ImGuiHelpers.ScaledDummy(8);

        DrawFontSettings(config);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextUnformatted("창 스타일");
        const string followDalamud = "Dalamud 설정 따르기";
        ImGui.SetNextItemWidth(Layout.FitWidth(320 * ImGuiHelpers.GlobalScale));
        using (var combo = ImRaii.Combo("##windowStyle", config.WindowStyleName ?? followDalamud))
        {
            if (combo.Success)
            {
                if (ImGui.Selectable(followDalamud, config.WindowStyleName is null) && config.WindowStyleName is not null)
                {
                    config.WindowStyleName = null;
                    config.Save();
                }
                foreach (var style in _windowStyles.All)
                {
                    if (ImGui.Selectable(style.Name, style.Name == config.WindowStyleName) && style.Name != config.WindowStyleName)
                    {
                        config.WindowStyleName = style.Name;
                        config.Save();
                    }
                }
            }
        }
        if (config.WindowStyleName is not null && _windowStyles.Find(config.WindowStyleName) is null)
            WrappedText.Draw($"'{config.WindowStyleName}' 스타일을 찾지 못해 Dalamud 설정을 따른다.", ErrorColor);
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
                _plugin.WindowFont.Change(null);
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
            _plugin.WindowFont.Change(t.Result);
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

    private void SetStatus(string text, bool isError)
    {
        _sendStatusIsError = isError;
        _sendStatus = text;
    }

    /// <summary>
    /// 엔진 상태를 바로 앞 컨트롤 옆에 그린다. 자리가 모자라면 다음 줄에 줄바꿈해서 그린다
    /// </summary>
    private static void DrawEngineStatus(TranslationQueue queue)
    {
        var (text, color) = queue.Status switch
        {
            EngineStatus.Checking => ("로그인 확인 중…", DimColor),
            EngineStatus.Ready => ($"{queue.EngineName} 준비됨", DimColor),
            EngineStatus.NotLoggedIn => ($"{queue.EngineName} {queue.NotReadyHint}", ErrorColor),
            _ => ($"{queue.EngineName} 사용 불가: {queue.StatusDetail}", ErrorColor),
        };
        if (Layout.SameLineIfFits(Layout.TextWidth(text)))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, text);
        }
        else
        {
            WrappedText.Draw(text, color);
        }
    }
}
