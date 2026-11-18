using System.Globalization;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ManagedFontAtlas;

namespace AetheryteChat.Windows;

/// <summary>
/// 이 창에서 쓰는 폰트. 게임 폰트에는 한글이 없어 ImGui 창에서 따로 불러온다 (DESIGN 3절).
/// 사용자가 고른 폰트(없으면 Dalamud 기본 폰트)에, 그 폰트에 없는 글자를 Windows 기본 한글·일본어 폰트로 채우고
/// 채팅에 섞여 오는 게임 전용 기호(자동 번역 괄호·파티 모집 표시 등 U+E0xx)를 합친다.
/// </summary>
internal sealed class WindowFont(IFontAtlas atlas, SingleFontSpec? initial) : IDisposable
{
    private readonly Lock _lock = new();
    private IFontHandle _handle = Create(atlas, initial);
    private SingleFontSpec? _pending;
    private bool _hasPending;

    /// <summary>
    /// 지금 폰트. 바꾸기를 부탁받았으면 여기서 새로 만들고 이전 것을 버린다 — 창이 그리는 중(push 된 상태)에
    /// 버리지 않도록, Draw 첫머리에서 push 하기 직전에만 읽는다.
    /// </summary>
    public IFontHandle Handle
    {
        get
        {
            lock (_lock)
            {
                if (_hasPending)
                {
                    var old = _handle;
                    _handle = Create(atlas, _pending);
                    old.Dispose();
                    _hasPending = false;
                    _pending = null;
                }
                return _handle;
            }
        }
    }

    /// <summary>다음에 그릴 때 이 폰트로 바꾼다. null 이면 Dalamud 기본 폰트. 어느 스레드에서 불러도 된다</summary>
    public void Change(SingleFontSpec? spec)
    {
        lock (_lock)
        {
            _pending = spec;
            _hasPending = true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
            _handle.Dispose();
    }

    private static IFontHandle Create(IFontAtlas atlas, SingleFontSpec? spec) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = spec is null ? tk.AddDalamudDefaultFont(-1, null) : spec.AddToBuildToolkit(tk, default);
            var config = new SafeFontConfig { SizePx = spec?.SizePx ?? UiBuilder.DefaultFontSizePx, MergeFont = font };
            // 합칠 때는 앞 폰트에 이미 있는 글자를 건너뛴다 — 고른 폰트의 글자가 우선이고 없는 글자만 채워진다
            tk.AttachWindowsDefaultFont(CultureInfo.GetCultureInfo("ko-KR"), in config, 400, 5, 0);
            tk.AttachWindowsDefaultFont(CultureInfo.GetCultureInfo("ja-JP"), in config, 400, 5, 0);
            tk.AddGameSymbol(in config);
        }));
}
