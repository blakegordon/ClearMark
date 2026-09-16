using ClearMark;
using Spectre.Console;

AnsiConsole.MarkupLine("[bold cyan]ClearMark v1.0[/] — Transparent System Benchmark");
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

AnsiConsole.Status().Start("Running...", ctx =>
{
    ctx.Spinner(Spinner.Known.Dots);

    // CPU
    cpuResults = CpuBenchmark.Run(status => ctx.Status(status));

    // Memory
    memResults = MemoryBenchmark.Run(status => ctx.Status(status));

    // Latency Ladder
    ladderResults = MemoryBenchmark.RunLatencyLadder(status => ctx.Status(status));

    // Storage
    if (!skipStorage)
        storageResults = StorageBenchmark.Run(status => ctx.Status(status));

    // GPU
    if (!skipGpu)
        gpuResults = GpuBenchmark.Run(status => ctx.Status(status), hw.GpuName);
});

// ── 3. Display results ──────────────────────────────────────────────────
AnsiConsole.WriteLine();
AnsiConsole.Write(new Rule("[bold cyan]Results[/]"));
AnsiConsole.WriteLine();

Report.PrintHeader(hw);
Report.PrintCpu(cpuResults);
Report.PrintMemory(memResults);
Report.PrintLatencyLadder(ladderResults);

if (storageResults.Count > 0)
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
