using Dalamud.Interface.Style;

namespace AetheryteChat.Windows;

/// <summary>
/// 이 창에 고를 수 있는 Dalamud 스타일: 내장(Standard·Classic·Hazy) + Dalamud 스타일 편집기에서 저장한 스타일.
/// 스타일 편집기에서 고치거나 새로 저장하면 따라가도록 2초마다 다시 읽는다.
/// </summary>
internal sealed class WindowStyles
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private List<StyleModel> _styles = [];
    private DateTime _loadedAt;

    public IReadOnlyList<StyleModel> All
    {
        get
        {
            Refresh();
            return _styles;
        }
    }

    /// <summary>이름으로 찾는다. null 이거나 없어진 스타일이면 null (Dalamud 설정을 따른다)</summary>
    public StyleModel? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(s => s.Name == name);

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - _loadedAt < RefreshInterval)
            return;
        _loadedAt = now;

        var styles = new List<StyleModel> { StyleModelV1.DalamudStandard, StyleModelV1.DalamudClassic, StyleModelV1.DalamudHazy };
        styles.AddRange(StyleModel.GetConfiguredStyles() ?? []); // 저장한 스타일이 없으면 null 일 수 있다

        // 같은 이름이면 사용자가 저장한 쪽을 쓴다
        _styles = styles.GroupBy(s => s.Name).Select(g => g.Last()).ToList();
    }
}
