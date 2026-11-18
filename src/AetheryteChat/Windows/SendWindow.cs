using System.Numerics;
using Dalamud.Interface.Windowing;

namespace AetheryteChat.Windows;

/// <summary>설정의 보내기 위치가 '별도 창' 일 때 쓰는 보내기 전용 창. 받은 채팅 창과 같은 스타일·폰트를 쓴다</summary>
internal sealed class SendWindow : StyledWindow
{
    public SendWindow(Plugin plugin) : base(plugin, "Aetheryte Chat 보내기##send")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 160),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    protected override void DrawContent() => Owner.SendPanel.Draw(compact: false);
}
