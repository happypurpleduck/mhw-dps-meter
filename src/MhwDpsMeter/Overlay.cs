using System.Numerics;
using ImGuiNET;

namespace MhwDpsMeter;

internal sealed class Overlay
{
    private static readonly Vector4[] SlotColors =
    [
        new(1.00f, 0.62f, 0.18f, 1f),
        new(0.38f, 0.86f, 0.42f, 1f),
        new(0.32f, 0.72f, 1.00f, 1f),
        new(0.95f, 0.42f, 0.72f, 1f)
    ];

    private static readonly Vector4 TrialRunning = new(0.35f, 0.75f, 1.00f, 1f);
    private static readonly Vector4 TrialEnding = new(1.00f, 0.45f, 0.30f, 1f);
    private static readonly Vector4 TrialBest = new(1.00f, 0.85f, 0.25f, 1f);
    private const float TrialEndingWarningSeconds = 5f;
    private const int TrialMovesShown = 5;

    public bool Visible { get; set; } = true;
    public float Opacity { get; set; } = 0.92f;

    public void Draw(PartySnapshot? snapshot, float elapsedSeconds, bool inQuest, string? hint = null, TimeTrial? trial = null)
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
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - 16f, 16f), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(Math.Clamp(Opacity, 0.2f, 1f) * 0.85f);

        if (!ImGui.Begin("##MhwDpsMeter", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("DPS");
        ImGui.SameLine();
        ImGui.TextDisabled(Plugin.BuildStamp);
        ImGui.Separator();

        if (snapshot is null || snapshot.Members.Length == 0)
        {
            ImGui.TextDisabled("Waiting for hunt data...");
            ImGui.End();
            return;
        }

        DrawPartyTable(snapshot, elapsedSeconds);

        if (trial is { Active: true })
            DrawTrial(trial);
        else if (!string.IsNullOrEmpty(hint))
            ImGui.TextDisabled(hint);

        ImGui.End();
    }

    private static void DrawPartyTable(PartySnapshot snapshot, float elapsedSeconds)
    {
        var members = snapshot.Members
            .OrderByDescending(member => member.Damage)
            .ThenBy(member => member.Slot)
            .ToArray();
        var duration = Math.Max(elapsedSeconds, 1f);
        var total = Math.Max(snapshot.TotalDamage, 0);

        if (ImGui.BeginTable("##mhw_dps_rows", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("dmg", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("dps", ImGuiTableColumnFlags.WidthFixed, 52f);
            ImGui.TableSetupColumn("pct", ImGuiTableColumnFlags.WidthFixed, 48f);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled("Name");
            ImGui.TableNextColumn();
            ImGui.TextDisabled("Dmg");
            ImGui.TableNextColumn();
            ImGui.TextDisabled("DPS");
            ImGui.TableNextColumn();
            ImGui.TextDisabled("%");

            foreach (var member in members)
            {
                var color = ImGui.ColorConvertFloat4ToU32(SlotColors[Math.Clamp(member.Slot, 0, SlotColors.Length - 1)]);
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                var marker = member.IsLocal ? "*" : "";
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{marker}{member.Name}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{member.Damage:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{member.Dps:0.0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{member.Percent:0.0}%");
                ImGui.PopStyleColor();
            }

            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Total  {total:N0}   {total / duration:0.0} DPS");
    }

    /// <summary>Trial block under the table: countdown while running, frozen summary with top moves when done.</summary>
    private static void DrawTrial(TimeTrial trial)
    {
        ImGui.Separator();
        switch (trial.State)
        {
            case TimeTrialState.Armed:
                ImGui.TextColored(TrialRunning, $"Time trial {trial.DurationSeconds}s armed");
                ImGui.TextDisabled("Clock starts on your first hit.  F8 cancels");
                break;

            case TimeTrialState.Running:
            {
                var remaining = trial.Remaining;
                var color = remaining <= TrialEndingWarningSeconds ? TrialEnding : TrialRunning;
                ImGui.TextColored(color, $"Time trial {trial.DurationSeconds}s   {remaining:0.0}s left");
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
                ImGui.ProgressBar(trial.Elapsed / trial.DurationSeconds, new Vector2(-1f, 6f), "");
                ImGui.PopStyleColor();
                ImGui.TextUnformatted($"{trial.Damage:N0} dmg   {trial.Dps:0.0} DPS   {trial.Hits} hits   {trial.Crits} crit");
                if (trial.PreviousBest is { } best)
                    ImGui.TextDisabled($"Best {best:N0} ({best / (float)trial.DurationSeconds:0.0} DPS)");
                break;
            }

            case TimeTrialState.Finished:
            {
                ImGui.TextColored(TrialRunning, $"Time trial {trial.DurationSeconds}s finished");
                if (trial.IsPersonalBest)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(TrialBest, trial.PreviousBest is { } prev ? $"NEW BEST (+{trial.Damage - prev:N0})" : "FIRST RECORD");
                }
                else if (trial.PreviousBest is { } best)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"best {best:N0} ({trial.Damage - best:+#,0;-#,0})");
                }

                ImGui.TextUnformatted($"{trial.Damage:N0} dmg   {trial.Dps:0.0} DPS   {trial.Hits} hits   {trial.Crits} crit");
                foreach (var move in trial.TopMoves(TrialMovesShown))
                {
                    var share = trial.Damage > 0 ? 100f * move.Damage / trial.Damage : 0f;
                    ImGui.TextDisabled($"  {TimeTrial.ShortMoveName(move.Name),-22} {move.Damage,6:N0}  {share,4:0}%  x{move.Hits}");
                }

                ImGui.TextDisabled("F8 runs it again   F7 clears");
                break;
            }
        }
    }

    public void DrawSettings(
        FightLogStore? logs,
        string diagnostics,
        Action onDumpDiagnostics,
        Action? onResetTraining,
        PluginSettings settings,
        TimeTrial? trial,
        Action? onToggleTrial)
    {
        ImGui.TextWrapped(diagnostics);
        if (ImGui.Button("Dump diagnostics (F6)"))
            onDumpDiagnostics();

        if (onResetTraining is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Reset training damage (F7)"))
                onResetTraining();
        }

        var visible = Visible;
        if (ImGui.Checkbox("Show overlay", ref visible))
        {
            Visible = visible;
            settings.OverlayVisible = visible;
            settings.Save();
        }

        var opacity = Opacity;
        if (ImGui.SliderFloat("Opacity", ref opacity, 0.25f, 1f))
        {
            Opacity = opacity;
            settings.OverlayOpacity = opacity;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            settings.Save();

        ImGui.TextUnformatted("F10 overlay   F6 diagnostics   F7 reset training   F8 time trial");
        ImGui.TextDisabled("Overlay after depart, or in the training area (DPS clock starts at your first hit).");

        DrawTrialSettings(logs, settings, trial, onToggleTrial);
        DrawHistory(logs);
    }

    private static void DrawTrialSettings(FightLogStore? logs, PluginSettings settings, TimeTrial? trial, Action? onToggleTrial)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Time trial (training area)");

        var duration = settings.TrialDurationSeconds;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Duration (s)", ref duration, 5, 30))
        {
            settings.TrialDurationSeconds = Math.Clamp(duration, TimeTrial.MinDurationSeconds, TimeTrial.MaxDurationSeconds);
            settings.Save();
        }

        foreach (var preset in TimeTrial.PresetDurations)
        {
            ImGui.SameLine();
            if (!ImGui.SmallButton($"{preset}s"))
                continue;
            settings.TrialDurationSeconds = preset;
            settings.Save();
        }

        if (trial is null || onToggleTrial is null)
        {
            ImGui.TextDisabled("Enter the training area to run a trial. F8 arms it; the clock starts on your first hit.");
        }
        else
        {
            var label = trial.State switch
            {
                TimeTrialState.Armed or TimeTrialState.Running => "Cancel trial (F8)",
                TimeTrialState.Finished => $"Run again: {settings.TrialDurationSeconds}s (F8)",
                _ => $"Start {settings.TrialDurationSeconds}s trial (F8)"
            };
            if (ImGui.Button(label))
                onToggleTrial();

            if (trial.State == TimeTrialState.Finished)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(trial.SavedFileName ?? "not saved");
            }
        }

        if (logs is null)
            return;

        var best = logs.Trials()
            .GroupBy(entry => (entry.Weapon ?? "?", Math.Round(entry.DurationSeconds)))
            .Select(group => group.MaxBy(entry => entry.TotalDamage)!)
            .OrderBy(entry => entry.Weapon)
            .ThenBy(entry => entry.DurationSeconds)
            .ToArray();
        if (best.Length == 0)
        {
            ImGui.TextDisabled("No trials recorded yet.");
            return;
        }

        ImGui.TextDisabled("Personal bests");
        foreach (var entry in best)
        {
            ImGui.TextUnformatted(
                $"  {entry.Weapon ?? "?",-14} {entry.DurationSeconds,4:0}s  {entry.TotalDamage,7:N0} dmg  {entry.TotalDamage / Math.Max(entry.DurationSeconds, 1f),6:0.0} DPS   {entry.StartedAt.ToLocalTime():yyyy-MM-dd}");
        }
    }

    private static void DrawHistory(FightLogStore? logs)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Fight logs");

        if (logs is null || logs.History.Count == 0)
        {
            ImGui.TextDisabled("No hunts recorded yet.");
            return;
        }

        ImGui.TextDisabled(logs.LogsDirectory);
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
            if (log.Monsters.Length > 0)
                ImGui.TextDisabled(string.Join(", ", log.Monsters.Select(monster => monster.Name).Distinct()));
            foreach (var player in log.Players)
            {
                var weapon = player.Weapon is null ? "" : $"  {player.Weapon}";
                ImGui.TextUnformatted($"{player.Name,-16} {player.Damage,7:N0}  {player.Dps,6:0.0}  {player.Percent,5:0.0}%{weapon}");
            }

            if (log.Hits.Length > 0)
                ImGui.TextDisabled($"{log.Hits.Length} hits, {log.Events.Length} events recorded");
            ImGui.Unindent();
        }
    }
}
