using System.Numerics;
using Dalamud.Interface.Style;

namespace AetheryteChat.Windows;

/// <summary>
/// 이 플러그인이 함께 주는 Dalamud 스타일.
/// 'Aetheryte Dark' 는 kagerou 미터기 Umbra 스킨(tools/kagerou-umbra)과 같은 색 — 짙은 회색 바탕, 얇은 테두리,
/// 테두리가 있는 회색 버튼·입력칸, 붉은 강조색 없음.
/// </summary>
internal static class BuiltInStyles
{
    public const string AetheryteDarkName = "Aetheryte Dark";

    /// <summary>
    /// StyleModelV1 은 생성자가 공개돼 있지 않아 Dalamud Standard 사본에서 값을 바꿔 만든다.
    /// 여기서 바꾸지 않은 값(내장 색 등)은 Dalamud Standard 를 따른다
    /// </summary>
    public static StyleModelV1 AetheryteDark()
    {
        var s = StyleModelV1.DalamudStandard;
        s.Name = AetheryteDarkName;

        s.WindowPadding = new Vector2(8, 8);
        s.WindowRounding = 5;
        s.WindowBorderSize = 1;
        s.ChildRounding = 3;
        s.ChildBorderSize = 1;
        s.PopupRounding = 3;
        s.PopupBorderSize = 1;
        s.FramePadding = new Vector2(6, 3);
        s.FrameRounding = 3;
        s.FrameBorderSize = 1;      // 버튼·입력칸에 테두리 — 미터기의 칸 모양
        s.ItemSpacing = new Vector2(8, 5);
        s.ScrollbarSize = 10;
        s.ScrollbarRounding = 2;
        s.GrabMinSize = 10;
        s.GrabRounding = 2;
        s.TabRounding = 3;
        s.TabBorderSize = 0;

        var c = s.Colors;

        // 글자
        c["Text"] = Gray(0xe6);
        c["TextDisabled"] = Gray(0x8c);
        c["TextSelectedBg"] = Gray(0x80, 0.45f);

        // 창
        c["WindowBg"] = Gray(0x1f, 0.93f);
        c["ChildBg"] = Gray(0x00, 0);
        c["PopupBg"] = Gray(0x1e, 0.98f);
        c["MenuBarBg"] = Gray(0x19);
        c["Border"] = Gray(0x3a);
        c["BorderShadow"] = Gray(0x00, 0);
        c["ModalWindowDimBg"] = Gray(0x00, 0.5f);

        // 제목줄 — 본문보다 조금 진하게
        c["TitleBg"] = Gray(0x16, 0.97f);
        c["TitleBgActive"] = Gray(0x19, 0.97f);
        c["TitleBgCollapsed"] = Gray(0x16, 0.85f);

        // 입력칸·목록 상자
        c["FrameBg"] = Gray(0x29);
        c["FrameBgHovered"] = Gray(0x31);
        c["FrameBgActive"] = Gray(0x39);

        // 버튼
        c["Button"] = Gray(0x29);
        c["ButtonHovered"] = Gray(0x34);
        c["ButtonActive"] = Gray(0x40);

        // 목록에서 고른 줄·마우스를 올린 줄, 접는 머리글
        c["Header"] = Gray(0x34);
        c["HeaderHovered"] = Gray(0x2e);
        c["HeaderActive"] = Gray(0x40);

        // 탭 — 고른 탭만 칸처럼 밝게
        c["Tab"] = Gray(0x1f, 0);
        c["TabHovered"] = Gray(0x31);
        c["TabActive"] = Gray(0x2e);
        c["TabUnfocused"] = Gray(0x1f, 0);
        c["TabUnfocusedActive"] = Gray(0x29);

        // 체크·슬라이더
        c["CheckMark"] = Gray(0xe6);
        c["SliderGrab"] = Gray(0x8c);
        c["SliderGrabActive"] = Gray(0xb4);

        // 스크롤바
        c["ScrollbarBg"] = Gray(0x00, 0);
        c["ScrollbarGrab"] = Gray(0x4a);
        c["ScrollbarGrabHovered"] = Gray(0x5c);
        c["ScrollbarGrabActive"] = Gray(0x6e);

        // 구분선·크기 조절 손잡이
        c["Separator"] = Gray(0x3a);
        c["SeparatorHovered"] = Gray(0x55);
        c["SeparatorActive"] = Gray(0x77);
        c["ResizeGrip"] = Gray(0x55, 0.25f);
        c["ResizeGripHovered"] = Gray(0x77, 0.6f);
        c["ResizeGripActive"] = Gray(0x99, 0.9f);

        // 표
        c["TableHeaderBg"] = Gray(0x24);
        c["TableBorderStrong"] = Gray(0x3a);
        c["TableBorderLight"] = Gray(0x2e);
        c["TableRowBg"] = Gray(0x00, 0);
        c["TableRowBgAlt"] = Gray(0xff, 0.03f);

        // 그래프·도킹·키보드 탐색 — 붉은 강조 대신 회색
        c["PlotLines"] = Gray(0x9c);
        c["PlotLinesHovered"] = Gray(0xe6);
        c["PlotHistogram"] = Gray(0x80);
        c["PlotHistogramHovered"] = Gray(0xb4);
        c["DockingPreview"] = Gray(0x99, 0.5f);
        c["DockingEmptyBg"] = Gray(0x19);
        c["DragDropTarget"] = Gray(0xe6, 0.9f);
        c["NavHighlight"] = Gray(0xb4);
        c["NavWindowingHighlight"] = Gray(0xff, 0.7f);
        c["NavWindowingDimBg"] = Gray(0x80, 0.2f);

        return s;
    }

    /// <summary>회색 #vvvvvv (0~255) 와 불투명도</summary>
    private static Vector4 Gray(int v, float alpha = 1f) => new(v / 255f, v / 255f, v / 255f, alpha);
}
