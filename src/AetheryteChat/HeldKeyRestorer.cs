using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace AetheryteChat;

/// <summary>
/// 입력칸에 글을 쓰는 동안 계속 누르고 있던 키(이동 방향키 등)를 입력칸에서 빠져나온 뒤 게임에 다시 알린다.
/// 글을 쓰는 동안 Dalamud 가 게임 쪽 키 상태(IKeyState)를 매 프레임 지우고, Windows 의 반복 입력은 마지막에 누른 키
/// (Enter 등)로 옮겨 가므로, 그대로 두면 누르고 있던 W 를 떼었다 다시 누를 때까지 캐릭터가 멈춰 있다.
/// 게임 채팅 입력창은 닫으면 누르고 있던 방향으로 다시 움직이므로 그와 같게 한다.
/// Dalamud 는 IKeyState 에 '눌림' 을 쓰지 못하게 하므로, 키를 처음 누를 때와 같은 WM_KEYDOWN 을 게임 창에 보낸다
/// (실제로 누르고 있는 키만 — 떼면 Windows 가 보내는 WM_KEYUP 으로 게임이 멈춘다).
/// 글을 쓰는 동안 새로 누른 키(Enter·Esc·글자)는 다시 알리지 않는다 — 들어갈 때부터 떼지 않고 누르고 있던 키만
/// </summary>
/// <param name="hotkey">입력칸 단축키의 키. 누르고 있어도 다시 알리지 않는다 (단축키 처리가 게임에 가지 않게 지우는 키)</param>
internal sealed partial class HeldKeyRestorer(IKeyState keyState, Func<VirtualKey> hotkey)
{
    // 빠져나온 뒤 다시 알리기를 이어 하는 프레임 수와 다시 보내는 간격. 입력칸이 풀리고 한두 프레임은 Dalamud 가 아직
    // 키를 막을 수 있어 한 번만 보내면 묻힐 수 있다. 게임 쪽에서 눌린 것으로 보이면 그 키는 더 보내지 않는다
    private const int RestoreFrames = 30;
    private const int ResendEveryFrames = 5;

    // 마우스 버튼은 키보드 메시지가 아니다. Enter·Esc 는 다시 알리면 게임 채팅이나 메뉴가 열린다.
    // Shift·Ctrl·Alt·Windows 키는 이동과 상관없고, Alt 는 다른 메시지(WM_SYSKEYDOWN)라 건드리지 않는다
    private static readonly HashSet<VirtualKey> Skipped =
    [
        VirtualKey.LBUTTON, VirtualKey.RBUTTON, VirtualKey.MBUTTON, VirtualKey.XBUTTON1, VirtualKey.XBUTTON2,
        VirtualKey.RETURN, VirtualKey.ESCAPE,
        VirtualKey.SHIFT, VirtualKey.LSHIFT, VirtualKey.RSHIFT,
        VirtualKey.CONTROL, VirtualKey.LCONTROL, VirtualKey.RCONTROL,
        VirtualKey.MENU, VirtualKey.LMENU, VirtualKey.RMENU,
        VirtualKey.LWIN, VirtualKey.RWIN,
    ];

    // 확장 키 (lParam 의 24번 비트). 방향키 등은 이 비트가 있어야 숫자 패드 키와 구별된다
    private static readonly HashSet<VirtualKey> ExtendedKeys =
    [
        VirtualKey.LEFT, VirtualKey.UP, VirtualKey.RIGHT, VirtualKey.DOWN,
        VirtualKey.INSERT, VirtualKey.DELETE, VirtualKey.HOME, VirtualKey.END, VirtualKey.PRIOR, VirtualKey.NEXT,
        VirtualKey.DIVIDE,
    ];

    private readonly HashSet<VirtualKey> _held = [];
    private bool _typing;
    private int _restoreLeft;

    /// <summary>게임 스레드에서 매 프레임</summary>
    public void Update(IFramework framework)
    {
        var typing = ImGui.GetIO().WantTextInput;
        if (typing)
        {
            if (!_typing)
            {
                // 글을 쓰기 시작한 때 누르고 있던 키를 적어 둔다
                _held.Clear();
                var skippedHotkey = hotkey();
                foreach (var key in keyState.GetValidVirtualKeys())
                {
                    if (!Skipped.Contains(key) && key != skippedHotkey && PhysicalKeys.IsDown(key))
                        _held.Add(key);
                }
            }
            else
            {
                // 쓰는 동안 한 번이라도 뗀 키는 뺀다 (Shift+Enter 로 들어갈 때 같이 누른 Enter 등)
                _held.RemoveWhere(key => !PhysicalKeys.IsDown(key));
            }
            _typing = true;
            _restoreLeft = 0;
            return;
        }

        if (_typing)
        {
            _typing = false;
            _restoreLeft = RestoreFrames;
        }
        if (_restoreLeft == 0)
            return;

        // 뗀 키, 게임이 이미 눌린 것으로 아는 키는 뺀다
        _held.RemoveWhere(key => !PhysicalKeys.IsDown(key) || keyState[key]);
        if (_held.Count == 0)
        {
            _restoreLeft = 0;
            return;
        }
        if ((RestoreFrames - _restoreLeft) % ResendEveryFrames == 0)
        {
            var window = GameWindow();
            if (window != 0)
            {
                foreach (var key in _held)
                    PostKeyDown(window, key);
            }
        }
        _restoreLeft--;
    }

    private static unsafe nint GameWindow()
    {
        var framework = GameFramework.Instance();
        return framework == null || framework->GameWindow == null ? 0 : framework->GameWindow->WindowHandle;
    }

    /// <summary>키를 처음 누를 때 Windows 가 보내는 것과 같은 WM_KEYDOWN 을 보낸다</summary>
    private static void PostKeyDown(nint window, VirtualKey key)
    {
        const uint WmKeyDown = 0x0100;
        const uint MapVkToVsc = 0;
        var scanCode = MapVirtualKeyW((uint)key, MapVkToVsc);
        // 반복 횟수 1, 스캔 코드, 확장 키 비트. 30번 비트(이미 눌려 있던 키)는 두지 않는다 — 게임은 반복 입력을 무시하고
        // 처음 누를 때만 키 상태를 바꾼다
        var lParam = 1u | (scanCode & 0xFF) << 16 | (ExtendedKeys.Contains(key) ? 1u << 24 : 0);
        PostMessageW(window, WmKeyDown, (nint)key, (nint)lParam);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);
}
