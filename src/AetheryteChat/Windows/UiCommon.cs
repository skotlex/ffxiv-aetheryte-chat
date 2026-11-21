using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AetheryteChat.Windows;

/// <summary>여러 창이 같이 쓰는 색과 그리기 조각</summary>
internal static class UiCommon
{
    public static readonly Vector4 TranslationColor = new(0.55f, 0.85f, 1f, 1f);
    public static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.4f, 1f);

    /// <summary>안 읽은 대화가 있는 채팅 탭의 점</summary>
    public static readonly Vector4 UnreadColor = new(1f, 0.62f, 0.2f, 1f);

    /// <summary>
    /// 엔진 상태를 바로 앞 컨트롤 옆에 그린다. 자리가 모자라면 다음 줄에 줄바꿈해서 그린다.
    /// rightAlign 이면 그 줄의 오른쪽 끝에 붙인다.
    /// 준비가 안 됐으면 글자를 눌러 다시 확인할 수 있다 — 터미널에서 로그인하거나 Ollama 를 켠 뒤 플러그인을 다시 켜지 않아도 되게.
    /// 로그인이 안 된 Claude·Codex 면 옆에 '로그인' 버튼을 둔다 — 터미널을 몰라도 로그인할 수 있게.
    /// 1순위를 쓰지 못해 예비 도구로 번역하는 중이면 그 이유를 덧붙이고, 1순위가 로그인 문제면 로그인 버튼도 둔다
    /// </summary>
    public static void DrawEngineStatus(TranslationQueue queue, bool rightAlign = false)
    {
        var name = queue.EngineName;
        var issue = queue.Status == EngineStatus.Ready ? queue.PrimaryIssue : null;
        var (text, color) = queue.Status switch
        {
            EngineStatus.Checking => ($"{name} 확인 중…".TrimStart(), DimColor),
            EngineStatus.Ready when issue is not null => ($"{name} 준비됨 ({issue})", ErrorColor),
            EngineStatus.Ready => ($"{name} 준비됨", DimColor),
            EngineStatus.NotLoggedIn => ($"{name} {queue.NotReadyHint}".TrimStart(), ErrorColor),
            _ => ($"{name} 사용 불가: {queue.StatusDetail}".TrimStart(), ErrorColor),
        };
        // 예비 도구로 번역하는 중이어도 로그인이 안 된 도구가 있으면 버튼을 둔다
        var showLogin = queue.Status != EngineStatus.Checking && queue.CanLogin;
        var loginLabel = queue.LoggingIn ? "로그인 중…###login" : "로그인###login";
        var loginWidth = showLogin ? ImGui.GetStyle().ItemSpacing.X + Layout.ButtonWidth(loginLabel) : 0;

        var width = Layout.TextWidth(text) + loginWidth;
        var placed = rightAlign ? Layout.RightAlign(width) : Layout.SameLineIfFits(width);
        if (placed)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(color, text);
        }
        else
        {
            WrappedText.Draw(text, color);
        }

        // 이름이 없으면 아직 도구를 고르지 않은 것 — 다시 확인할 것이 없다
        if ((queue.Status is EngineStatus.NotLoggedIn or EngineStatus.Unavailable || issue is not null) && name.Length > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("눌러서 다시 확인");
            if (ImGui.IsItemClicked())
                queue.Reload();
        }

        if (!showLogin)
            return;
        if (placed)
            ImGui.SameLine();
        using (ImRaii.Disabled(queue.LoggingIn))
        {
            if (ImGui.Button(loginLabel))
                queue.StartLogin();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(queue.LoggingIn
                ? "새로 열린 창과 브라우저에서 로그인을 마쳐 주세요. 창이 닫히면 자동으로 다시 확인합니다"
                : $"새 창과 브라우저가 열려 {queue.LoginTargetName ?? "구독 계정"}에 로그인합니다");
        }
    }

    /// <summary>설명 글 (흐린 색, 창 폭에 맞춰 줄바꿈)</summary>
    public static void HelpText(string text) => WrappedText.Draw(text, DimColor);
}
