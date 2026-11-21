using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace AetheryteChat.Windows;

/// <summary>
/// 채팅 탭 하나의 이름·보일 채널·순서·삭제. 탭을 오른쪽 클릭한 메뉴와 설정 탭에서 같이 쓴다
/// </summary>
internal static class ChatTabEditor
{
    private const int MaxNameLength = 32;

    /// <summary>이름이 비어 있으면 탭이 너무 작아져 누르기 어려우므로 대신 보일 이름</summary>
    public static string DisplayName(ChatTab tab) => string.IsNullOrWhiteSpace(tab.Name) ? "(이름 없음)" : tab.Name;

    /// <summary>새 탭 이름: '새 탭', 이미 있으면 '새 탭 2', '새 탭 3' …</summary>
    public static string NewTabName(Configuration config)
    {
        const string baseName = "새 탭";
        var used = config.ChatTabs.Select(t => t.Name).ToHashSet();
        if (!used.Contains(baseName))
            return baseName;
        var n = 2;
        while (used.Contains($"{baseName} {n}"))
            n++;
        return $"{baseName} {n}";
    }

    /// <summary>모든 채널을 보이는 새 탭을 맨 오른쪽에 더한다</summary>
    public static ChatTab Add(Configuration config)
    {
        var tab = new ChatTab { Name = NewTabName(config) };
        config.ChatTabs.Add(tab);
        config.Save();
        return tab;
    }

    /// <summary>편집 칸들을 그린다. 탭을 지웠으면 true (부르는 쪽이 팝업을 닫는다)</summary>
    /// <param name="columns">채널 체크박스를 몇 칸씩 늘어놓을지. null 이면 남은 폭에 맞춘다 (팝업은 폭이 내용에 따라 정해지므로 고정 값을 준다)</param>
    public static bool Draw(Configuration config, ChatTab tab, int? columns = null)
    {
        var id = tab.Id.ToString("N");

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("탭 이름");
        ImGui.SameLine();
        var name = tab.Name;
        ImGui.SetNextItemWidth(Layout.FitWidth(200 * ImGuiHelpers.GlobalScale));
        if (ImGui.InputText($"##name{id}", ref name, MaxNameLength))
            tab.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit())
            config.Save();

        ImGui.TextUnformatted("보일 채널");
        Layout.SameLineIfFits(Layout.ButtonWidth("모두 켜기") + ImGui.GetStyle().ItemSpacing.X + Layout.ButtonWidth("모두 끄기"));
        using (ImRaii.Disabled(tab.HiddenChannels.Count == 0))
        {
            if (ImGui.Button($"모두 켜기##showAll{id}"))
            {
                tab.HiddenChannels.Clear();
                config.Save();
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(tab.HiddenChannels.Count == TranslateChannels.Groups.Length))
        {
            if (ImGui.Button($"모두 끄기##hideAll{id}"))
            {
                foreach (var (group, _, _) in TranslateChannels.Groups)
                    tab.HiddenChannels.Add(group);
                config.Save();
            }
        }
        DrawChannelGrid(config, tab, id, columns);

        ImGuiHelpers.ScaledDummy(4);
        return DrawOrderAndDelete(config, tab, id);
    }

    /// <summary>채널 체크박스를 칸을 맞춰 늘어놓는다</summary>
    private static void DrawChannelGrid(Configuration config, ChatTab tab, string id, int? columns)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cellWidth = TranslateChannels.Groups.Max(g => Layout.CheckboxWidth(g.Label)) + spacing * 2;
        var perRow = columns ?? Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / cellWidth));
        var left = ImGui.GetCursorPosX();

        for (var i = 0; i < TranslateChannels.Groups.Length; i++)
        {
            var (group, label, _) = TranslateChannels.Groups[i];
            if (i % perRow != 0)
                ImGui.SameLine(left + i % perRow * cellWidth);

            var shown = !tab.HiddenChannels.Contains(group);
            if (ImGui.Checkbox($"{label}##show{group}{id}", ref shown))
            {
                if (shown)
                    tab.HiddenChannels.Remove(group);
                else
                    tab.HiddenChannels.Add(group);
                config.Save();
            }
        }
    }

    /// <summary>[왼쪽으로] [오른쪽으로] [탭 삭제]. 지웠으면 true</summary>
    private static bool DrawOrderAndDelete(Configuration config, ChatTab tab, string id)
    {
        var tabs = config.ChatTabs;
        var index = tabs.IndexOf(tab);

        using (ImRaii.Disabled(index <= 0))
        {
            if (ImGui.Button($"왼쪽으로##left{id}"))
                Move(config, index, index - 1);
        }
        Layout.SameLineIfFits(Layout.ButtonWidth("오른쪽으로"));
        using (ImRaii.Disabled(index < 0 || index >= tabs.Count - 1))
        {
            if (ImGui.Button($"오른쪽으로##right{id}"))
                Move(config, index, index + 1);
        }

        // 마지막 탭은 지우지 않는다 — 받은 채팅을 볼 곳이 없어진다
        Layout.SameLineIfFits(Layout.ButtonWidth("탭 삭제"));
        var deleted = false;
        using (ImRaii.Disabled(tabs.Count <= 1))
        {
            if (ImGui.Button($"탭 삭제##delete{id}"))
            {
                tabs.Remove(tab);
                config.Save();
                deleted = true;
            }
        }
        if (tabs.Count <= 1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("채팅 탭은 하나 이상 있어야 합니다");
        return deleted;
    }

    private static void Move(Configuration config, int from, int to)
    {
        var tabs = config.ChatTabs;
        if (from < 0 || to < 0 || from >= tabs.Count || to >= tabs.Count)
            return;
        (tabs[from], tabs[to]) = (tabs[to], tabs[from]);
        config.Save();
    }
}
