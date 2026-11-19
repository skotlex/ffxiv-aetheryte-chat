using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;

namespace AetheryteChat;

/// <summary>
/// 키보드에서 지금 실제로 눌려 있는 키 (Windows 에 직접 묻는다).
/// 게임 쪽 키 상태(IKeyState)는 이 플러그인 입력칸에 글을 쓰는 동안 Dalamud 가 매 프레임 지우므로, 그때부터 누르고 있던
/// Shift 같은 키는 떼었다 다시 누를 때까지 게임 쪽에서 안 눌린 것으로 보인다
/// </summary>
internal static partial class PhysicalKeys
{
    public static bool IsDown(VirtualKey key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);
}
