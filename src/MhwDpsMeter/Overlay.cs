using ImGuiNET;

namespace MhwDpsMeter;

internal sealed class Overlay
{
    private static readonly System.Numerics.Vector4[] SlotColors =
    [
        new(1.00f, 0.62f, 0.18f, 1f),
        new(0.38f, 0.86f, 0.42f, 1f),
        new(0.32f, 0.72f, 1.00f, 1f),
        new(0.95f, 0.42f, 0.72f, 1f)
    ];

    public bool Visible { get; set; } = true;
    public float Opacity { get; set; } = 0.92f;

    public void Draw(PartySnapshot? snapshot, float elapsedSeconds, bool inQuest)
    {
        if (!Visible || !inQuest)
            return;

        var flags = ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoInputs;

        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(io.DisplaySize.X - 16f, 16f), ImGuiCond.Always, new System.Numerics.Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(Math.Clamp(Opacity, 0.2f, 1f) * 0.85f);

        if (!ImGui.Begin("##MhwDpsMeter", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("DPS");
        ImGui.Separator();

        if (snapshot is null || snapshot.Members.Length == 0)
        {
            ImGui.TextDisabled("Waiting for hunt data...");
            ImGui.End();
            return;
        }

        var members = snapshot.Members.OrderByDescending(member => member.Damage).ToArray();
        var duration = Math.Max(elapsedSeconds, 0.001f);
        var total = Math.Max(snapshot.TotalDamage, 0);

        if (ImGui.BeginTable("##mhw_dps_rows", 4, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("dmg", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("dps", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("pct", ImGuiTableColumnFlags.WidthFixed, 48f);

            foreach (var member in members)
            {
                var color = ImGui.ColorConvertFloat4ToU32(SlotColors[Math.Clamp(member.Slot, 0, SlotColors.Length - 1)]);
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                var dps = member.Damage / duration;
                var percent = total > 0 ? 100f * member.Damage / total : 0f;
                var marker = member.IsLocal ? "*" : "";

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{marker}{member.Name}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{member.Damage:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{dps:0.0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{percent:0.0}%");
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    public void DrawSettings(FightLogStore? logs, string diagnostics)
    {
        ImGui.TextWrapped(diagnostics);

        var visible = Visible;
        if (ImGui.Checkbox("Show overlay", ref visible))
            Visible = visible;

        var opacity = Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0.25f, 1f))
            Opacity = opacity;

        ImGui.TextUnformatted("Toggle overlay: F10");
        ImGui.TextDisabled("Overlay only appears after you depart into a quest.");
        ImGui.Separator();
        ImGui.TextUnformatted("Fight logs");

        if (logs is null || logs.History.Count == 0)
        {
            ImGui.TextDisabled("No hunts recorded yet.");
            return;
        }

        for (var i = 0; i < logs.History.Count; i++)
        {
            var log = logs.History[i];
            var winner = log.Players.FirstOrDefault()?.Name ?? "-";
            var label = $"{log.QuestName}  {log.Result}  {log.DurationSeconds:0}s  {winner}##{i}";
            var open = logs.ExpandedHistoryIndex == i;
            if (ImGui.Selectable(label, open))
                logs.ExpandedHistoryIndex = open ? null : i;

            if (logs.ExpandedHistoryIndex != i)
                continue;

            ImGui.Indent();
            ImGui.TextDisabled(log.FileName);
            foreach (var player in log.Players)
                ImGui.TextUnformatted($"{player.Name,-16} {player.Damage,7:N0}  {player.Dps,6:0.0}  {player.Percent,5:0.0}%");
            ImGui.Unindent();
        }
    }
}
