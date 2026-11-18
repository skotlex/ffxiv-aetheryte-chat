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

    /// <summary>원하는 폭과 이 줄에 남은 폭 중 작은 쪽 (목록 상자·슬라이더 폭)</summary>
    public static float FitWidth(float preferred) => Math.Min(preferred, ImGui.GetContentRegionAvail().X);

    /// <summary>오른쪽에 이름표가 붙는 컨트롤의 폭 — 이름표 자리를 빼고 남은 폭에 맞춘다</summary>
    public static float FitWidth(float preferred, string rightLabel) =>
        Math.Max(1f, Math.Min(preferred, ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X - TextWidth(VisibleLabel(rightLabel))));

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
