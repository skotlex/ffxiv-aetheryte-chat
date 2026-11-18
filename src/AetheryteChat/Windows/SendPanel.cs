using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using static AetheryteChat.Windows.UiCommon;

namespace AetheryteChat.Windows;

/// <summary>
/// 보내기 화면: 한국어 입력 → Enter(번역) → 일본어와 '다시 한국어로' 확인 → Enter 한 번 더(보내기).
/// 사용자가 일본어를 읽지 못하므로 일본어는 고칠 수 없게 보여 주고, 뜻은 다시 옮긴 한국어로 확인한다.
/// 채팅 아래와 따로 뗀 창 두 곳에서 같은 인스턴스를 그려, 보내기 위치를 바꿔도 입력하던 글이 남는다.
/// </summary>
internal sealed class SendPanel(Plugin plugin)
{
    // 상태. 번역·전송 결과는 백그라운드에서 채워진다 (문자열 대입이라 UI 스레드에서 읽어도 안전)
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

    /// <param name="compact">
    /// 채팅 아래에 붙일 때. 채널 이름을 목록 상자 앞에 두고, '한국어' 이름표 대신 입력칸 안내를 쓰고,
    /// 번역 전의 빈 안내와 엔진 상태(채팅 윗줄에 있음)를 빼서 세로 공간을 아낀다
    /// </param>
    public void Draw(bool compact)
    {
        var queue = plugin.TranslationQueue;

        var channel = DrawChannelSelector(compact);
        var prefix = channel.NeedsTellTarget ? $"/t {_tellTarget.Trim()}" : channel.Prefix;

        bool enter;
        if (compact)
        {
            ImGui.SetNextItemWidth(-1);
            enter = ImGui.InputTextWithHint("##ko", "한국어로 입력하고 Enter", ref _koreanInput, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        }
        else
        {
            ImGuiHelpers.ScaledDummy(8); // 설정 탭처럼 묶음 사이를 띄운다
            ImGui.TextUnformatted("한국어");
            ImGui.SetNextItemWidth(-1);
            enter = ImGui.InputText("##ko", ref _koreanInput, 500, ImGuiInputTextFlags.EnterReturnsTrue);
        }
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

        if (!compact)
            ImGuiHelpers.ScaledDummy(4);
        DrawTranslationCheck(stale, compact);

        if (sendError is not null)
            WrappedText.Draw(sendError, ErrorColor);
        if (_sendStatus.Length > 0)
            WrappedText.Draw(_sendStatus, _sendStatusIsError ? ErrorColor : DimColor);
    }

    private void DrawTranslationCheck(bool stale, bool compact)
    {
        if (_japanese.Length == 0)
        {
            if (!compact)
                WrappedText.Draw("번역하면 여기에 일본어와, 그 일본어를 다시 한국어로 옮긴 뜻이 나옵니다.", DimColor);
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
                    ImGui.TextColored(DimColor, "… 확인 중 (기다리지 않고 보내셔도 됩니다)");
                    break;
                case TranslationState.Done:
                    WrappedText.Draw(_back, TranslationColor);
                    break;
                case TranslationState.Failed:
                    WrappedText.Draw($"확인 실패: {_back}", ErrorColor);
                    break;
            }
        }

        if (stale)
            WrappedText.Draw("한국어가 바뀌었습니다 — Enter로 다시 번역해 주세요.", ErrorColor);
    }

    private string? SendError(SendChannel channel, string prefix)
    {
        if (channel.NeedsTellTarget && string.IsNullOrWhiteSpace(_tellTarget))
            return "귓속말 받는 사람을 적어 주세요 (이름 성 또는 이름 성@서버).";
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

        plugin.TranslationQueue.TranslateOutgoingAsync(korean).ContinueWith(t =>
        {
            if (id != _requestId)
                return;

            if (t.IsCompletedSuccessfully)
            {
                _translatedFrom = korean;
                _japanese = t.Result.Text;
                SetStatus($"번역했습니다 ({t.Result.Elapsed.TotalSeconds:0.0}초, {Encoding.UTF8.GetByteCount(_japanese)}바이트).", false);
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
        plugin.TranslationQueue.TranslateBackAsync(japanese).ContinueWith(t =>
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
    private SendChannel DrawChannelSelector(bool compact)
    {
        // 링크셸 가입·탈퇴, 게임 입력창의 채널 변경을 바로 반영하려고 매번 게임에서 읽는다
        var channels = SendChannels.Build();
        var selected = channels.FirstOrDefault(c => c.Key == _channelKey) ?? channels[0];

        // '한국어' 처럼 이름을 위 줄에 두고 그 아래에 목록 상자를 놓는다 (좁은 모양에서는 같은 줄 앞에)
        if (compact)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("채널");
            ImGui.SameLine();
        }
        else
        {
            ImGui.TextUnformatted("채널");
        }
        ImGui.SetNextItemWidth(Layout.FitWidth(260 * ImGuiHelpers.GlobalScale));
        using (var combo = ImRaii.Combo("##channel", selected.Label))
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
                            _tellTarget = plugin.ChatListener.LastTellSender ?? "";
                    }
                }
            }
        }
        if (!compact)
            DrawEngineStatus(plugin.TranslationQueue);

        if (selected.NeedsTellTarget)
        {
            if (compact)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted("받는 사람");
                ImGui.SameLine();
            }
            else
            {
                ImGui.TextUnformatted("받는 사람");
            }
            ImGui.SetNextItemWidth(Layout.FitWidth(260 * ImGuiHelpers.GlobalScale));
            ImGui.InputTextWithHint("##tellTarget", "이름 성@서버", ref _tellTarget, 64);
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
        plugin.TranslationQueue.RememberSent(japanese, korean);
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
                    SetStatus($"보냈습니다 ({channelLabel}): {korean}", false);
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

    private void SetStatus(string text, bool isError)
    {
        _sendStatusIsError = isError;
        _sendStatus = text;
    }
}
