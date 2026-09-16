using Spectre.Console;

namespace ClearMark;

internal static class Report
{
    public static void PrintHeader(HardwareInfo hw)
    {
        var panel = new Panel(
            new Rows(
                new Markup($"[bold]CPU:[/]  {Markup.Escape(hw.CpuName)} ({hw.Cores}C/{hw.Threads}T, {hw.Architecture})"),
                new Markup($"[bold]RAM:[/]  {FormatBytes(hw.TotalRamBytes)} @ {Markup.Escape(hw.RamSpeed)}"),
                new Markup($"[bold]GPU:[/]  {Markup.Escape(hw.GpuName)}"),
                new Markup($"[bold]Disk:[/] {Markup.Escape(hw.OsDrive)}"),
                new Markup($"[bold]OS:[/]   {Markup.Escape(hw.OsVersion)}")))
        {
            Header = new PanelHeader("[bold cyan]ClearMark v1.0[/]"),
            Border = BoxBorder.Double,
            Padding = new Padding(2, 0, 2, 0)
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static void PrintCpu(List<CpuResult> results)
    {
        var table = new Table().Title("[bold yellow]CPU[/]").Border(TableBorder.Rounded)
            .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach (var r in results)
        {
            double score = Scoring.ScoreOne(r.TestName, r.Value);
            table.AddRow(r.TestName, $"{r.Value:N0} {r.Unit}", ScoreMarkup(score));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintMemory(List<MemoryResult> results)
    {
        var table = new Table().Title("[bold blue]Memory[/]").Border(TableBorder.Rounded)
            .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach (var r in results)
        {
            double score = Scoring.ScoreOne(r.TestName, r.Value);
            string formatted = r.TestName.Contains("Latency") ? $"{r.Value:N1} {r.Unit}" : $"{r.Value:N0} {r.Unit}";
            table.AddRow(r.TestName, formatted, ScoreMarkup(score));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintStorage(List<StorageResult> results)
    {
        var table = new Table().Title("[bold green]Storage[/]").Border(TableBorder.Rounded)
            .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach (var r in results)
        {
            double score = Scoring.ScoreOne(r.TestName, r.Value);
            table.AddRow(r.TestName, $"{r.Value:N0} {r.Unit}", ScoreMarkup(score));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintLatencyLadder(List<LatencyLadderPoint> points)
    {
        var table = new Table().Title("[bold blue]Memory Latency Ladder[/]").Border(TableBorder.Rounded)
            .AddColumn("Working Set").AddColumn(new TableColumn("Latency").RightAligned())
            .AddColumn(""); // bar chart

        double maxLatency = points.Max(p => p.LatencyNs);
        foreach (var p in points)
        {
            int barLen = (int)(p.LatencyNs / maxLatency * 30);
            string bar = new string('█', Math.Max(1, barLen));
            string color = p.SizeKB <= 32 ? "green" : p.SizeKB <= 512 ? "yellow" : p.SizeKB <= 8192 ? "orange3" : "red";
            table.AddRow(p.SizeLabel, $"{p.LatencyNs:N1} ns", $"[{color}]{bar}[/]");
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim]  L1 ≈ green │ L2 ≈ yellow │ L3 ≈ orange │ RAM ≈ red[/]");
        AnsiConsole.WriteLine();
    }

    public static void PrintGpu(List<GpuResult> results)
    {
        // Show primary GPU first
        var deviceGroups = results.GroupBy(r => r.DeviceName)
            .OrderByDescending(g => g.First().IsPrimary);

        foreach (var group in deviceGroups)
        {
            bool isPrimary = group.First().IsPrimary;
            string suffix = isPrimary ? " (primary)" : " (secondary — not scored)";
            string escapedName = Markup.Escape(group.Key);
            var table = new Table()
                .Title($"[bold yellow]GPU: {escapedName}{suffix}[/]")
                .Border(TableBorder.Rounded)
                .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
                .AddColumn(new TableColumn("Score").RightAligned());

            foreach (var r in group)
            {
                if (r.Value == 0 && r.Unit.Contains("N/A"))
                {
                    table.AddRow(r.TestName, "[dim]unsupported[/]", "[dim]—[/]");
                    continue;
                }
                double score = Scoring.ScoreOne(r.TestName, r.Value);
                table.AddRow(r.TestName, $"{r.Value:N0} {r.Unit}", ScoreMarkup(score));
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
    }

    public static void PrintComposite(double gaming, double productivity, double balanced)
    {
        var table = new Table().Title("[bold magenta]Composite Scores[/]").Border(TableBorder.Double)
            .AddColumn("Profile").AddColumn(new TableColumn("Score").RightAligned())
            .AddColumn("Weights (1T / nT / Mem / Stor / GPU)");

        string Fmt(double[] w) => string.Join(" / ", w.Select(v => $"{v:P0}"));
        table.AddRow("Gaming",       ScoreMarkup(gaming),       Fmt(Scoring.GamingWeights));
        table.AddRow("Productivity", ScoreMarkup(productivity), Fmt(Scoring.ProdWeights));
        table.AddRow("Balanced",     ScoreMarkup(balanced),     Fmt(Scoring.BalancedWeights));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Score of 100 = mid-range 2024 desktop baseline. Above 100 = better than baseline.[/]");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string ScoreMarkup(double score)
    {
        string color = score switch
        {
            >= 120 => "bold green",
            >= 90  => "green",
            >= 70  => "yellow",
            >= 50  => "orange3",
            _      => "red"
        };
        return $"[{color}]{score:N0}[/]";
    }

    private static string FormatBytes(long bytes)
    {
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        return $"{gb:N1} GB";
    }
}
