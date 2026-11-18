using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Style;
using Dalamud.Interface.Windowing;

namespace AetheryteChat.Windows;

/// <summary>
/// 설정 탭의 창 스타일·배경·컨트롤 색·폰트를 적용하는 이 플러그인 창의 공통 부모.
/// 받은 채팅 창과 따로 뗀 보내기 창이 같은 모양이 되게 한다.
/// </summary>
internal abstract class StyledWindow(Plugin plugin, string name) : Window(name)
{
    protected Plugin Owner { get; } = plugin;

    private StyleModel? _pushedStyle; // PreDraw 에서 push 한 스타일 — PostDraw 에서 같은 것을 pop 한다
    private int _pushedColorCount;    // PreDraw 에서 push 한 색 개수 (배경 + 컨트롤 색)

    public override void PreDraw()
    {
        var config = Owner.Configuration;

        // 배경만 투명하게 한다 (Dalamud 제목줄 메뉴의 불투명도는 글자까지 흐려진다)
        BgAlpha = config.BackgroundOpacity;

        // 이 창에만 다른 스타일을 쓴다. Dalamud 는 PreDraw 뒤에 현재 스타일에서 제목줄 색을 읽어 push 하고,
        // 창을 닫은(End) 뒤 PostDraw 를 부르므로 여기서 push 하고 PostDraw 에서 pop 하면 이 창만 감싼다 (WindowHost.cs)
        _pushedStyle = Owner.WindowStyles.Find(config.WindowStyleName);
        _pushedStyle?.Push();

        // 배경색·컨트롤 색은 고른 스타일 위에 덮는다. 알파는 스타일 값을 두고, 배경은 BgAlpha(배경 불투명도)가 다시 정한다
        _pushedColorCount = 0;
        if (config.BackgroundColor is { } rgb)
        {
            var alpha = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W;
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(rgb, alpha));
            _pushedColorCount++;
        }
        _pushedColorCount += CustomColors.Push(config.ControlColors, config.ControlOpacity);
    }

    public override void PostDraw()
    {
        if (_pushedColorCount > 0)
        {
            ImGui.PopStyleColor(_pushedColorCount);
            _pushedColorCount = 0;
        }
        _pushedStyle?.Pop();
        _pushedStyle = null;
    }

    public sealed override void Draw()
    {
        using var font = Owner.WindowFont.Handle.Push();
        DrawContent();
    }

    /// <summary>창 안을 그린다. 폰트·스타일은 이미 적용돼 있다</summary>
    protected abstract void DrawContent();
}
