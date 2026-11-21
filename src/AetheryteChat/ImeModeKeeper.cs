using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace AetheryteChat;

/// <summary>
/// 알트탭 등으로 게임 창을 떠났다 돌아오면 한/영 상태를 떠나기 전대로 되돌린다.
/// Windows 11 의 새 Microsoft 한국어 IME 는 창을 다시 활성화할 때 입력 상태를 영어로 바꿔, 게임 채팅·이 플러그인
/// 입력칸 모두 한/영 키를 다시 눌러야 한글이 된다 (이 플러그인과 상관없이 생긴다).
/// 이전 버전 IME 에서는 생기지 않지만, 그쪽은 한글 조합 중 스페이스에서 글자가 겹쳐 들어가 쓸 수 없다.
/// 게임 창에 있는 동안 매 프레임 한/영 상태를 적어 두었다가, 돌아온 뒤 잠깐 동안 달라져 있으면 적어 둔 상태로 바꾼다.
/// 한국어 자판일 때만 손대고, 돌아온 뒤 사용자가 한/영 키를 누르면 그 선택을 따른다
/// </summary>
internal sealed partial class ImeModeKeeper(Func<bool> enabled)
{
    // 돌아온 뒤 되돌리기를 이어 하는 프레임 수. IME 가 활성화 직후 한두 번에 걸쳐 상태를 바꿀 수 있어 한 번만 보면 놓친다
    private const int RestoreFrames = 60;

    private const int ImeCmodeNative = 0x1; // 한국어 IME 에서는 한글 입력
    private const ushort LangKorean = 0x0412;
    private const int VkHangul = 0x15;

    private bool _active = true;
    private bool _saved;
    private bool _savedOpen;
    private bool _savedHangul;

    // 돌아온 뒤 IME 입력 상태(HIMC)를 아직 한 번도 얻지 못했는지 — 얻을 때까지 기다린다
    private bool _waitingContext;
    private int _restoreLeft;

    /// <summary>게임 스레드에서 매 프레임 (IME 함수는 창을 만든 스레드에서 불러야 한다)</summary>
    public void Update(IFramework framework)
    {
        var active = IsForeground();
        if (!active)
        {
            _active = false;
            _waitingContext = false;
            _restoreLeft = 0;
            return;
        }
        if (!_active)
        {
            _active = true;
            if (_saved && enabled())
                _waitingContext = true;
        }

        if (!IsKoreanLayout())
            return;

        // 돌아온 뒤 사용자가 한/영 키를 눌렀으면 그대로 둔다
        if ((_waitingContext || _restoreLeft > 0) && (GetAsyncKeyState(VkHangul) & 0x8000) != 0)
        {
            _waitingContext = false;
            _restoreLeft = 0;
        }

        var window = FocusedWindow();
        if (window == 0)
            return;
        var context = ImmGetContext(window);
        if (context == 0)
            return;
        try
        {
            if (!ImmGetConversionStatus(context, out var conversion, out var sentence))
                return;
            var open = ImmGetOpenStatus(context);
            var hangul = open && (conversion & ImeCmodeNative) != 0;

            if (_waitingContext)
            {
                _waitingContext = false;
                _restoreLeft = RestoreFrames;
            }

            if (_restoreLeft > 0)
            {
                _restoreLeft--;
                if (hangul != _savedHangul)
                {
                    if (_savedOpen && !open)
                        ImmSetOpenStatus(context, true);
                    var restored = _savedHangul ? conversion | ImeCmodeNative : conversion & ~ImeCmodeNative;
                    ImmSetConversionStatus(context, restored, sentence);
                }
                return; // 되돌리는 동안에는 바뀐 상태를 적어 두지 않는다
            }

            _saved = true;
            _savedOpen = open;
            _savedHangul = hangul;
        }
        finally
        {
            ImmReleaseContext(window, context);
        }
    }

    /// <summary>지금 앞에 있는 창이 이 게임(Dalamud 의 분리된 ImGui 창 포함)인지</summary>
    private static bool IsForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0)
            return false;
        GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private static bool IsKoreanLayout() => (ushort)(GetKeyboardLayout(0) & 0xFFFF) == LangKorean;

    /// <summary>이 스레드에서 키보드 포커스를 가진 창. 없으면 게임 창</summary>
    private static unsafe nint FocusedWindow()
    {
        var focus = GetFocus();
        if (focus != 0)
            return focus;
        var framework = GameFramework.Instance();
        return framework == null || framework->GameWindow == null ? 0 : framework->GameWindow->WindowHandle;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("imm32.dll")]
    private static partial nint ImmGetContext(nint hWnd);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmReleaseContext(nint hWnd, nint hImc);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmGetOpenStatus(nint hImc);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmSetOpenStatus(nint hImc, [MarshalAs(UnmanagedType.Bool)] bool open);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmGetConversionStatus(nint hImc, out int conversion, out int sentence);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmSetConversionStatus(nint hImc, int conversion, int sentence);
}
