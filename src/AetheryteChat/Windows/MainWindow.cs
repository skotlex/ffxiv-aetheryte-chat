using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AetheryteChat.Windows;

/// <summary>받은 채팅(원문 + 한국어 번역)과 보내기(한국어 → 일본어 미리보기 → 전송) 창.</summary>
internal sealed class MainWindow : Window
{
    private static readonly ChatChannel[] Channels = Enum.GetValues<ChatChannel>();
    private static readonly string[] ChannelLabels =
        Channels.Select(c => $"{c} ({ChatSender.Prefix(c)})").ToArray();

    private static readonly Vector4 TranslationColor = new(0.55f, 0.85f, 1f, 1f);
    private static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.4f, 1f);

    private readonly Plugin _plugin;

    // 보내기 탭 상태. 번역·전송 결과는 백그라운드에서 채워진다 (문자열 대입이라 UI 스레드에서 읽어도 안전)
    private string _koreanInput = "";
    private string _japanesePreview = "";
    // 마지막 번역의 한국어 원문과 결과 — 미리보기를 고치지 않고 보냈을 때만 받은 채팅에 원문을 보여 준다
    private string _translatedFrom = "";
    private string _translatedTo = "";
    private int _channelIndex; // 기본 Echo — 나에게만 보인다
    private volatile bool _busy;
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
        using (var tab = ImRaii.TabItem("보내기"))
        {
            if (tab.Success)
                DrawSend();
        }
    }

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

    private void DrawSend()
    {
        var queue = _plugin.TranslationQueue;

        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        ImGui.Combo("채널", ref _channelIndex, ChannelLabels, ChannelLabels.Length);
        ImGui.SameLine();
        DrawEngineStatus(queue);

        // 1. 한국어 입력 → 번역 (Enter 또는 버튼)
        ImGui.TextUnformatted("한국어");
        ImGui.SetNextItemWidth(-1);
        var enter = ImGui.InputText("##ko", ref _koreanInput, 500, ImGuiInputTextFlags.EnterReturnsTrue);

        var canTranslate = !_busy && queue.Status == EngineStatus.Ready && !string.IsNullOrWhiteSpace(_koreanInput);
        using (ImRaii.Disabled(!canTranslate))
        {
            if ((ImGui.Button("번역") || enter) && canTranslate)
                Translate(_koreanInput.Trim());
        }

        ImGuiHelpers.ScaledDummy(4);

        // 2. 일본어 미리보기 — 보내기 전에 직접 고칠 수 있다
        ImGui.TextUnformatted("일본어 (보내기 전에 고칠 수 있음)");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ja", ref _japanesePreview, 500);

        var error = _japanesePreview.Length == 0 ? null : ChatSender.Validate(_japanesePreview);
        using (ImRaii.Disabled(_busy || _japanesePreview.Length == 0 || error is not null))
        {
            if (ImGui.Button("보내기"))
            {
                var japanese = _japanesePreview.Trim();
                Send(Channels[_channelIndex], japanese, japanese == _translatedTo ? _translatedFrom : "");
            }
        }
        ImGui.SameLine();
        ImGui.TextColored(DimColor, $"{Encoding.UTF8.GetByteCount(_japanesePreview)} 바이트");

        if (error is not null)
            ImGui.TextColored(ErrorColor, error);
        if (_sendStatus.Length > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, _sendStatusIsError ? ErrorColor : DimColor))
                ImGui.TextWrapped(_sendStatus);
        }
    }

    private void Translate(string korean)
    {
        _busy = true;
        SetStatus("번역 중…", false);
        _plugin.TranslationQueue.TranslateOutgoingAsync(korean).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
            {
                _japanesePreview = t.Result.Text;
                _translatedFrom = korean;
                _translatedTo = t.Result.Text.Trim();
                SetStatus($"번역됨 ({t.Result.Elapsed.TotalSeconds:0.0}초). 확인하고 보내기를 누른다.", false);
            }
            else if (!t.IsCanceled)
            {
                SetStatus($"번역 실패: {t.Exception?.InnerException?.Message}", true);
            }
            _busy = false;
        });
    }

    private void Send(ChatChannel channel, string japanese, string korean)
    {
        _busy = true;
        SetStatus("보내는 중…", false);
        // 받은 채팅에 돌아오는 내 글은 다시 번역하지 않고 한국어 원문을 보여 준다
        _plugin.TranslationQueue.RememberSent(japanese, korean);
        Plugin.Framework.RunOnFrameworkThread(() => ChatSender.Send(channel, japanese))
            .ContinueWith(t =>
            {
                if (t.Exception?.InnerException is { } e)
                {
                    SetStatus($"보내기 실패: {e.Message}", true);
                    Plugin.Log.Warning(e, "채팅 보내기 실패");
                }
                else
                {
                    SetStatus($"보냄 ({ChatSender.Prefix(channel)})", false);
                    _koreanInput = "";
                    _japanesePreview = "";
                    _translatedFrom = "";
                    _translatedTo = "";
                }
                _busy = false;
            });
    }

    private void SetStatus(string text, bool isError)
    {
        _sendStatusIsError = isError;
        _sendStatus = text;
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
}
