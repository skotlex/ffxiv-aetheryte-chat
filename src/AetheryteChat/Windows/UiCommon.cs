using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AetheryteChat.Windows;

/// <summary>여러 창이 같이 쓰는 색과 그리기 조각</summary>
internal static class UiCommon
{
    public static readonly Vector4 TranslationColor = new(0.55f, 0.85f, 1f, 1f);
    public static readonly Vector4 DimColor = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 ErrorColor = new(1f, 0.5f, 0.4f, 1f);

    /// <summary>
    /// 엔진 상태를 바로 앞 컨트롤 옆에 그린다. 자리가 모자라면 다음 줄에 줄바꿈해서 그린다.
    /// rightAlign 이면 그 줄의 오른쪽 끝에 붙인다.
    /// 준비가 안 됐으면 글자를 눌러 다시 확인할 수 있다 — 터미널에서 로그인하거나 Ollama 를 켠 뒤 플러그인을 다시 켜지 않아도 되게
    /// </summary>
    public static void DrawEngineStatus(TranslationQueue queue, bool rightAlign = false)
    {
        var name = queue.EngineName;
        var (text, color) = queue.Status switch
        {
            EngineStatus.Checking => ($"{name} 확인 중…".TrimStart(), DimColor),
            EngineStatus.Ready => ($"{name} 준비됨", DimColor),
            EngineStatus.NotLoggedIn => ($"{name} {queue.NotReadyHint}".TrimStart(), ErrorColor),
            _ => ($"{name} 사용 불가: {queue.StatusDetail}".TrimStart(), ErrorColor),
        };
        var width = Layout.TextWidth(text);
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
        if (queue.Status is EngineStatus.NotLoggedIn or EngineStatus.Unavailable && name.Length > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("눌러서 다시 확인");
            if (ImGui.IsItemClicked())
                queue.Reload();
        }
    }

    /// <summary>설명 글 (흐린 색, 창 폭에 맞춰 줄바꿈)</summary>
    public static void HelpText(string text) => WrappedText.Draw(text, DimColor);
}
