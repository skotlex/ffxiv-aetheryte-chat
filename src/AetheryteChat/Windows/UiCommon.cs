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
    /// rightAlign 이면 그 줄의 오른쪽 끝에 붙인다
    /// </summary>
    public static void DrawEngineStatus(TranslationQueue queue, bool rightAlign = false)
    {
        var (text, color) = queue.Status switch
        {
            EngineStatus.Checking => ("로그인 확인 중…", DimColor),
            EngineStatus.Ready => ($"{queue.EngineName} 준비됨", DimColor),
            EngineStatus.NotLoggedIn => ($"{queue.EngineName} {queue.NotReadyHint}", ErrorColor),
            _ => ($"{queue.EngineName} 사용 불가: {queue.StatusDetail}", ErrorColor),
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
    }
}
