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
/// 알파는 지금 스타일의 값을 그대로 쓴다.
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

    /// <summary>설정 탭에서 색 고르기를 시작할 때 보여 줄 지금 스타일의 기본색</summary>
    public static Vector3 CurrentBase(ControlColorGroup group)
    {
        var c = ImGui.GetStyle().Colors[(int)BaseCol(group)];
        return new Vector3(c.X, c.Y, c.Z);
    }

    /// <summary>정해 둔 색을 push 하고 push 한 개수를 돌려준다. PostDraw 에서 그만큼 pop 한다</summary>
    public static int Push(IReadOnlyDictionary<ControlColorGroup, Vector3> colors)
    {
        var count = 0;
        foreach (var (group, rgb) in colors)
        {
            foreach (var (col, adjust) in Members(group))
            {
                var alpha = ImGui.GetStyle().Colors[(int)col].W;
                ImGui.PushStyleColor(col, new Vector4(adjust(rgb), alpha));
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
