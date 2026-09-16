using ClearMark;
using Spectre.Console;

AnsiConsole.MarkupLine($"[bold cyan]{AppInfo.Label}[/] — Transparent System Benchmark");
AnsiConsole.MarkupLine("[dim]https://github.com/blakegordon/ClearMark — All scoring formulas are visible in source code.[/]");
AnsiConsole.WriteLine();

bool skipStorage = args.Any(a => a.Equals("--skip-storage", StringComparison.OrdinalIgnoreCase));
bool skipGpu = args.Any(a => a.Equals("--skip-gpu", StringComparison.OrdinalIgnoreCase));

// ── 1. Detect hardware ──────────────────────────────────────────────────
var hw = SystemInfo.Detect();

// ── 2. Run benchmarks ───────────────────────────────────────────────────
AnsiConsole.MarkupLine("[bold]Starting benchmarks...[/] Close other applications for best results.");
AnsiConsole.MarkupLine("[dim]Each test runs multiple iterations; the median is reported.[/]");
AnsiConsole.WriteLine();

List<CpuResult> cpuResults = [];
List<MemoryResult> memResults = [];
List<LatencyLadderPoint> ladderResults = [];
List<StorageResult> storageResults = [];
List<GpuResult>? gpuResults = null;
string? storageError = null;

AnsiConsole.Status().Start("Running...", ctx =>
{
    ctx.Spinner(Spinner.Known.Dots);

    cpuResults = CpuBenchmark.Run(status => ctx.Status(status));
    memResults = MemoryBenchmark.Run(status => ctx.Status(status));
    ladderResults = MemoryBenchmark.RunLatencyLadder(status => ctx.Status(status));

    if (!skipStorage)
    {
        try { storageResults = StorageBenchmark.Run(status => ctx.Status(status)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            storageError = ex.Message;
        }
    }

    if (!skipGpu)
        gpuResults = GpuBenchmark.Run(status => ctx.Status(status));
});

// ── 3. Display results ──────────────────────────────────────────────────
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold cyan]Results[/]"));
AnsiConsole.WriteLine();

Report.PrintHeader(hw);
Report.PrintCpu(cpuResults);
Report.PrintMemory(memResults);
Report.PrintLatencyLadder(ladderResults);

if (storageError is not null)
    AnsiConsole.MarkupLine($"[red]Storage tests failed:[/] {Markup.Escape(storageError)}\n");
else if (storageResults.Count > 0)
    Report.PrintStorage(storageResults);
else
    AnsiConsole.MarkupLine("[dim]Storage tests skipped (--skip-storage)[/]\n");

if (gpuResults != null)
    Report.PrintGpu(gpuResults);
else if (skipGpu)
    AnsiConsole.MarkupLine("[dim]GPU tests skipped (--skip-gpu)[/]\n");
else
    AnsiConsole.MarkupLine("[dim]GPU tests skipped (no DX12 GPU detected)[/]\n");

var (gaming, productivity, balanced) = Scoring.Composite(cpuResults, memResults, storageResults, gpuResults);
Report.PrintComposite(gaming, productivity, balanced);

AnsiConsole.WriteLine();
