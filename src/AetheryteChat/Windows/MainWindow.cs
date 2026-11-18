using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetheryteChat.Windows;

/// <summary>
/// 지금은 실험 창이다. 받은 채팅 원문 · 한글 입력 · 게임 채팅으로 보내기가 되는지 확인한다 (DESIGN 9절).
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly ChatChannel[] Channels = Enum.GetValues<ChatChannel>();
    private static readonly string[] ChannelLabels =
        Channels.Select(c => $"{c} ({ChatSender.Prefix(c)})").ToArray();

    private readonly Plugin _plugin;

    private string _koreanInput = "";
    private string _sendInput = "テスト送信です。";
    private int _channelIndex; // 기본 Echo — 나에게만 보인다
    private string _sendStatus = "";

    public MainWindow(Plugin plugin) : base("Aetheryte Chat##main")
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        using var font = _plugin.KoreanFont.Push();
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("받은 채팅"))
        {
            if (tab.Success)
                DrawReceived();
        }
        using (var tab = ImRaii.TabItem("한글 입력 시험"))
        {
            if (tab.Success)
                DrawKoreanInput();
        }
        using (var tab = ImRaii.TabItem("보내기 시험"))
        {
            if (tab.Success)
                DrawSend();
        }
    }

    private static readonly Vector4 TranslationColor = new(0.55f, 0.85f, 1f, 1f);
    private static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.4f, 1f);

    private void DrawReceived()
    {
        var queue = _plugin.TranslationQueue;

        if (ImGui.Button("지우기"))
            _plugin.ChatListener.Clear();
        ImGui.SameLine();
        var auto = queue.AutoTranslate;
        if (ImGui.Checkbox("자동 번역", ref auto))
            queue.AutoTranslate = auto;
        ImGui.SameLine();
        DrawEngineStatus(queue);

        using var child = ImRaii.Child("##log", Vector2.Zero, true);
        if (!child.Success)
            return;

        foreach (var line in _plugin.ChatListener.Snapshot())
        {
            ImGui.TextWrapped($"{line.Time:HH:mm} [{line.Channel}] {line.Sender}: {line.Message}");
            using var indent = ImRaii.PushIndent();
            switch (line.State)
            {
                case TranslationState.Pending:
                    ImGui.TextColored(DimColor, "… 번역 중");
                    break;
                case TranslationState.Done:
                    using (ImRaii.PushColor(ImGuiCol.Text, TranslationColor))
                        ImGui.TextWrapped($"→ {line.Result}");
                    break;
                case TranslationState.Failed:
                    using (ImRaii.PushColor(ImGuiCol.Text, ErrorColor))
                        ImGui.TextWrapped($"번역 실패: {line.Result}");
                    break;
            }
        }

        // 맨 아래에 있으면 새 줄을 따라간다
        if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            ImGui.SetScrollHereY(1.0f);
    }

    private static void DrawEngineStatus(TranslationQueue queue)
    {
        var (text, color) = queue.Status switch
        {
            EngineStatus.Checking => ("로그인 확인 중…", DimColor),
            EngineStatus.Ready => ($"{queue.EngineName} 준비됨", DimColor),
            EngineStatus.NotLoggedIn => ($"{queue.EngineName} 로그인 안 됨 — 터미널에서 claude 로 로그인", ErrorColor),
            _ => ($"{queue.EngineName} 사용 불가: {queue.StatusDetail}", ErrorColor),
        };
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, text);
    }

    private void DrawKoreanInput()
    {
        ImGui.TextWrapped("아래 칸에 한글을 입력해 조합 중인 글자가 바르게 보이는지, 완성된 글이 아래에 그대로 나오는지 확인한다.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ko", ref _koreanInput, 500);

        ImGui.Separator();
        ImGui.TextWrapped($"입력값: {_koreanInput}");
        ImGui.TextUnformatted($"글자 수: {_koreanInput.Length}");
        ImGui.TextUnformatted("표시 시험: 가나다라 한글 테스트 — 零式 お疲れ様でした");
    }

    private void DrawSend()
    {
        ImGui.TextWrapped("게임 채팅으로 보내기 시험. 처음에는 Echo(나에게만 보임)로 확인한다.");

        ImGui.SetNextItemWidth(200);
        ImGui.Combo("채널", ref _channelIndex, ChannelLabels, ChannelLabels.Length);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##send", ref _sendInput, 500);

        var error = ChatSender.Validate(_sendInput);
        using (ImRaii.Disabled(error is not null))
        {
            if (ImGui.Button("보내기"))
                SendAsync(Channels[_channelIndex], _sendInput);
        }

        if (error is not null)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), error);
        if (_sendStatus.Length > 0)
            ImGui.TextWrapped(_sendStatus);
    }

    private void SendAsync(ChatChannel channel, string text)
    {
        _sendStatus = "보내는 중…";
        Plugin.Framework.RunOnFrameworkThread(() => ChatSender.Send(channel, text))
            .ContinueWith(t =>
            {
                if (t.Exception?.InnerException is { } e)
                {
                    _sendStatus = $"실패: {e.Message}";
                    Plugin.Log.Warning(e, "채팅 보내기 실패");
                }
                else
                {
                    _sendStatus = $"보냄 ({ChatSender.Prefix(channel)}) — 게임 채팅창을 확인한다.";
                }
            });
    }
}
