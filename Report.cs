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
                new Markup($"[bold]GPU:[/]  {Markup.Escape(hw.GpuName)}{FormatDriver(hw.GpuDriver)}"),
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
            table.AddRow(FormatResult(r.Test, r.Value));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintMemory(List<MemoryResult> results)
    {
        var table = new Table().Title("[bold blue]Memory[/]").Border(TableBorder.Rounded)
            .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach (var r in results)
            table.AddRow(FormatResult(r.Test, r.Value));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintStorage(List<StorageResult> results)
    {
        var table = new Table().Title("[bold green]Storage[/]").Border(TableBorder.Rounded)
            .AddColumn("Test").AddColumn(new TableColumn("Result").RightAligned())
            .AddColumn(new TableColumn("Score").RightAligned());

        foreach (var r in results)
            table.AddRow(FormatResult(r.Test, r.Value));

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
            string bar = new('█', Math.Max(1, barLen));
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
                if (r.Unsupported)
                {
                    table.AddRow(Scoring.Spec(r.Test).Name, "[dim]unsupported[/]", "[dim]—[/]");
                    continue;
                }
                table.AddRow(FormatResult(r.Test, r.Value));
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

        static string Fmt(double[] w) => string.Join(" / ", w.Select(v => $"{v:P0}"));
        table.AddRow("Gaming",       ScoreMarkup(gaming),       Fmt(Scoring.GamingWeights));
        table.AddRow("Productivity", ScoreMarkup(productivity), Fmt(Scoring.ProdWeights));
        table.AddRow("Balanced",     ScoreMarkup(balanced),     Fmt(Scoring.BalancedWeights));

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Score of 100 = mid-range 2024 desktop baseline. Above 100 = better than baseline.[/]");
        AnsiConsole.MarkupLine("[dim]Gaming: skipped storage/GPU score 0 (not redistributed); FP64 excluded. Productivity/Balanced redistribute skips and include FP64.[/]");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string[] FormatResult(BenchTest test, double value)
    {
        var spec = Scoring.Spec(test);
        double score = Scoring.ScoreOne(test, value);
        string formatted = spec.LowerIsBetter ? $"{value:N1} {spec.Unit}" : $"{value:N0} {spec.Unit}";
        return [spec.Name, formatted, ScoreMarkup(score)];
    }

    private static string FormatDriver(string driver)
        => string.IsNullOrEmpty(driver) || driver == "Unknown" ? "" : $"  [dim]{Markup.Escape(driver)}[/]";

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
