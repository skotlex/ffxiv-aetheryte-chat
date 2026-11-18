using Dalamud.Bindings.ImGui;

namespace AetheryteChat.Windows;

/// <summary>
/// 창 폭이 좁아도 컨트롤이 오른쪽 끝에서 잘리지 않게 하는 배치 도우미.
/// ImGui.SameLine 은 자리가 없어도 같은 줄에 붙여 잘리므로, 들어갈 때만 붙이고 아니면 다음 줄로 내린다.
/// </summary>
internal static class Layout
{
    /// <summary>
    /// 바로 앞 항목 뒤에 nextWidth 폭의 항목이 들어가면 같은 줄에, 아니면 다음 줄에 놓는다. 같은 줄에 놓았으면 true
    /// </summary>
    public static bool SameLineIfFits(float nextWidth)
    {
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + spacing + nextWidth > right)
            return false;
        ImGui.SameLine();
        return true;
    }

    /// <summary>
    /// width 폭의 항목을 오른쪽 끝에 붙인다. 바로 앞 항목과 같은 줄에 들어가면 그 줄 오른쪽에, 아니면 다음 줄 오른쪽에.
    /// 창보다 넓어 한 줄에 못 놓으면 false — 부르는 쪽이 줄바꿈해서 그린다
    /// </summary>
    public static bool RightAlign(float width)
    {
        var contentMaxX = ImGui.GetWindowContentRegionMax().X; // 창 기준 좌표
        var right = ImGui.GetWindowPos().X + contentMaxX;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + spacing + width <= right)
        {
            ImGui.SameLine(contentMaxX - width);
            return true;
        }

        if (width > ImGui.GetContentRegionAvail().X)
            return false;
        ImGui.SetCursorPosX(contentMaxX - width);
        return true;
    }

    /// <summary>원하는 폭과 이 줄에 남은 폭 중 작은 쪽 (목록 상자·슬라이더 폭)</summary>
    public static float FitWidth(float preferred) => Math.Min(preferred, ImGui.GetContentRegionAvail().X);
    /// <summary>
    /// 목록 상자 폭: 목록 상자에 보일 수 있는 이름 가운데 가장 긴 것에 맞춘다(글자 + 안쪽 여백 + ▼ 버튼).
    /// 남은 폭보다 넓으면 남은 폭에 맞춘다
    /// </summary>
    public static float ComboWidth(IEnumerable<string> previews)
    {
        var text = previews.Select(TextWidth).DefaultIfEmpty(0f).Max();
        return FitWidth(text + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight());
    }

    /// <summary>버튼 폭. '##' 뒤의 ID 부분은 보이지 않으므로 빼고 잰다</summary>
    public static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(VisibleLabel(label)).X + ImGui.GetStyle().FramePadding.X * 2;

    /// <summary>체크박스 폭 (네모 + 간격 + 글자)</summary>
    public static float CheckboxWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(VisibleLabel(label)).X;

    public static float TextWidth(string text) => ImGui.CalcTextSize(text).X;

    private static string VisibleLabel(string label)
    {
        var at = label.IndexOf("##", StringComparison.Ordinal);
        return at < 0 ? label : label[..at];
    }
}
