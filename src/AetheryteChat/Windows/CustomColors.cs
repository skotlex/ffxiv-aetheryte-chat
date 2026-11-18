using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AetheryteChat.Windows;

/// <summary>이 창에서 따로 색을 정할 수 있는 컨트롤 묶음. 순서는 설정 탭에 보이는 순서</summary>
public enum ControlColorGroup
{
    TitleBar,
    Button,
    Frame,
    Tab,
    Grab,
    Text,
}

/// <summary>
/// 이 창에만 적용하는 컨트롤 색 (Dalamud 스타일에는 저장되지 않는다).
/// 묶음마다 기본색 하나를 고르면 마우스를 올렸을 때·눌렀을 때 색은 기본색을 조금씩 밝혀 만든다.
/// 불투명도는 묶음마다 따로 정하고, 정하지 않으면 지금 스타일의 값을 그대로 쓴다.
/// </summary>
internal static class CustomColors
{
    private const float HoverLighten = 0.10f;
    private const float ActiveLighten = 0.20f;
    private const float DimDarken = 0.25f;

    public static readonly (ControlColorGroup Group, string Label)[] Groups =
    [
        (ControlColorGroup.TitleBar, "제목줄"),
        (ControlColorGroup.Button, "버튼·선택 강조"),
        (ControlColorGroup.Frame, "입력칸·목록"),
        (ControlColorGroup.Tab, "탭"),
        (ControlColorGroup.Grab, "슬라이더·체크"),
        (ControlColorGroup.Text, "글자"),
    ];

    /// <summary>설정 탭에서 보여 줄 지금 색(RGB)과 불투명도(W). 이 창 안에서 부르면 정해 둔 값이 이미 반영돼 있다</summary>
    public static Vector4 CurrentBase(ControlColorGroup group) => ImGui.GetStyle().Colors[(int)BaseCol(group)];

    /// <summary>
    /// 정해 둔 색·불투명도를 push 하고 push 한 개수를 돌려준다. PostDraw 에서 그만큼 pop 한다.
    /// 색만 정했으면 불투명도는 스타일 값, 불투명도만 정했으면 색은 스타일 값(마우스 올림·누름 색도 스타일 그대로)을 쓴다.
    /// </summary>
    public static int Push(
        IReadOnlyDictionary<ControlColorGroup, Vector3> colors,
        IReadOnlyDictionary<ControlColorGroup, float> opacities)
    {
        var count = 0;
        foreach (var (group, _) in Groups)
        {
            var hasColor = colors.TryGetValue(group, out var rgb);
            var hasOpacity = opacities.TryGetValue(group, out var opacity);
            if (!hasColor && !hasOpacity)
                continue;

            foreach (var (col, adjust) in Members(group))
            {
                var style = ImGui.GetStyle().Colors[(int)col];
                var color = hasColor ? adjust(rgb) : new Vector3(style.X, style.Y, style.Z);
                ImGui.PushStyleColor(col, new Vector4(color, hasOpacity ? opacity : style.W));
                count++;
            }
        }
        return count;
    }

    private static ImGuiCol BaseCol(ControlColorGroup group) => group switch
    {
        ControlColorGroup.TitleBar => ImGuiCol.TitleBgActive,
        ControlColorGroup.Button => ImGuiCol.Button,
        ControlColorGroup.Frame => ImGuiCol.FrameBg,
        ControlColorGroup.Tab => ImGuiCol.TabActive,
        ControlColorGroup.Grab => ImGuiCol.SliderGrab,
        _ => ImGuiCol.Text,
    };

    /// <summary>묶음에 속한 ImGui 색과, 기본색에서 그 색을 만드는 방법</summary>
    private static IEnumerable<(ImGuiCol Col, Func<Vector3, Vector3> Adjust)> Members(ControlColorGroup group)
    {
        Func<Vector3, Vector3> same = c => c;
        Func<Vector3, Vector3> hover = c => Lighten(c, HoverLighten);
        Func<Vector3, Vector3> active = c => Lighten(c, ActiveLighten);
        Func<Vector3, Vector3> dim = c => c * (1f - DimDarken);

        switch (group)
        {
            case ControlColorGroup.TitleBar:
                // Dalamud 는 창이 포커스를 잃으면 TitleBg 쪽으로, 얻으면 TitleBgActive 쪽으로 색을 옮긴다
                yield return (ImGuiCol.TitleBgActive, same);
                yield return (ImGuiCol.TitleBg, dim);
                yield return (ImGuiCol.TitleBgCollapsed, dim);
                break;
            case ControlColorGroup.Button:
                yield return (ImGuiCol.Button, same);
                yield return (ImGuiCol.ButtonHovered, hover);
                yield return (ImGuiCol.ButtonActive, active);
                // 목록·선택지에서 고른 줄과 마우스를 올린 줄
                yield return (ImGuiCol.Header, same);
                yield return (ImGuiCol.HeaderHovered, hover);
                yield return (ImGuiCol.HeaderActive, active);
                break;
            case ControlColorGroup.Frame:
                yield return (ImGuiCol.FrameBg, same);
                yield return (ImGuiCol.FrameBgHovered, hover);
                yield return (ImGuiCol.FrameBgActive, active);
                break;
            case ControlColorGroup.Tab:
                yield return (ImGuiCol.TabActive, same);
                yield return (ImGuiCol.TabHovered, hover);
                yield return (ImGuiCol.Tab, dim);
                yield return (ImGuiCol.TabUnfocusedActive, same);
                yield return (ImGuiCol.TabUnfocused, dim);
                break;
            case ControlColorGroup.Grab:
                yield return (ImGuiCol.SliderGrab, same);
                yield return (ImGuiCol.SliderGrabActive, active);
                yield return (ImGuiCol.CheckMark, same);
                break;
            case ControlColorGroup.Text:
                yield return (ImGuiCol.Text, same);
                break;
        }
    }

    private static Vector3 Lighten(Vector3 c, float amount) => Vector3.Lerp(c, Vector3.One, amount);
}
