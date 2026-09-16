# ClearMark

**Transparent System Benchmark for Windows**

ClearMark measures CPU, memory, storage, and GPU performance with every scoring formula visible in source code. No black boxes, no hidden weights — just honest numbers you can audit and verify.

## Quick Start

```
ClearMark.exe
```

That's it. ClearMark detects your hardware and runs all benchmarks automatically. Results are printed as a formatted report with scores relative to a mid-range 2024 desktop baseline (score of 100).

### Options

| Flag | Effect |
|---|---|
| `--skip-storage` | Skip storage benchmarks (useful for quick runs) |
| `--skip-gpu` | Skip GPU benchmarks (useful if no DX12 GPU) |

## What It Measures

### CPU (8 tests)
| Test | What | 1T + nT |
|---|---|---|
| **Integer** | Sieve of Eratosthenes (ALU, branching, L2 cache) | ✓ |
| **Float** | Mandelbrot set (FMA, FP pipeline depth) | ✓ |
| **Crypto** | SHA-256 hashing (hardware acceleration where available) | ✓ |
| **Compression** | Brotli compress + decompress (mixed IPC workload) | ✓ |

### Memory (5 tests)
| Test | What |
|---|---|
| **Seq. Bandwidth (1T)** | Single-thread sequential read+write |
| **Seq. Bandwidth (nT)** | All-core bandwidth with NUMA-aware pinned threads |
| **Copy Bandwidth (1T)** | Single-thread `Buffer.BlockCopy` |
| **Copy Bandwidth (nT)** | All-core AVX NT-store copy with thread pinning |
| **Random Latency** | Pointer-chase latency at full working set |

Plus a **latency ladder** showing access times from L1 through RAM (4 KB → 128 MB).

### Storage (4 tests)
| Test | What |
|---|---|
| **Seq. Read / Write** | 256 MB sequential I/O via unbuffered native reads |
| **4K Random Read / Write** | 4 KB random I/O at QD1 — the access pattern that matters most |

### GPU (3 tests per device)
| Test | What |
|---|---|
| **FP32** | 4K×4K Mandelbrot (single precision) |
| **FP64** | 4K×4K Mandelbrot (double precision, if supported) |
| **Integer** | 16M-thread xorshift-multiply hash chain |

All GPUs with hardware DX12 support are benchmarked. Only the primary (display) GPU contributes to composite scores.

## Composite Scores

Three weighted profiles combine all category scores:

| Profile | CPU 1T | CPU nT | Memory | Storage | GPU | Use Case |
|---|---|---|---|---|---|---|
| **Gaming** | 20% | 10% | 10% | 20% | 40% | Frame rates, game loading |
| **Productivity** | 10% | 30% | 15% | 15% | 30% | Compilation, rendering, VMs |
| **Balanced** | 20% | 20% | 20% | 20% | 20% | General-purpose |

When a category is skipped (`--skip-storage` / `--skip-gpu`), its weight is redistributed proportionally.

## Sample Output

```
╔═ClearMark v1.0══════════════════════════════════════════════════════╗
║  CPU:  11th Gen Intel(R) Core(TM) i5-1135G7 @ 2.40GHz (4C/8T, X64)  ║
║  RAM:  7.7 GB @ 4267 MHz                                            ║
║  GPU:  Intel(R) Iris(R) Xe Graphics                                 ║
║  Disk: NVMe KBG40ZNS512G NVMe KIOXIA 512GB                          ║
║  OS:   Microsoft Windows 10.0.19045                                 ║
╚═════════════════════════════════════════════════════════════════════╝

                       Composite Scores
╔══════════════╦═══════╦══════════════════════════════════════╗
║ Profile      ║ Score ║ Weights (1T / nT / Mem / Stor / GPU) ║
╠══════════════╬═══════╬══════════════════════════════════════╣
║ Gaming       ║    28 ║ 20% / 10% / 10% / 20% / 40%          ║
║ Productivity ║    30 ║ 10% / 30% / 15% / 15% / 30%          ║
║ Balanced     ║    37 ║ 20% / 20% / 20% / 20% / 20%          ║
╚══════════════╩═══════╩══════════════════════════════════════╝

Score of 100 = mid-range 2024 desktop baseline. Above 100 = better than baseline.
```

## Building from Source

Requires [.NET 10 SDK](https://dot.net) on Windows.

```bash
dotnet build --configuration Release
```

The compiled binary is at `bin/Release/net10.0-windows/ClearMark.exe`.

## Design Philosophy

- **Transparent** — Every baseline, weight, and formula is a readable constant in [`Scoring.cs`](Scoring.cs). No obfuscation.
- **Simple** — Under 1,000 lines of benchmark code. Each test is a single method you can read in a minute.
- **Honest** — Median of multiple iterations. No cherry-picking. Lower-is-better metrics (latency) are scored correctly.
- **NUMA-aware** — Multi-threaded memory tests use native memory allocation, first-touch page distribution, and thread pinning for accurate bandwidth measurement on multi-socket systems.

## Technical Details

- **GPU compute** via [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) (DX12 compute shaders)
- **Storage I/O** via native `CreateFile` / `ReadFile` / `WriteFile` with `FILE_FLAG_NO_BUFFERING` — bypasses OS cache
- **Memory bandwidth** via `NativeMemory.AlignedAlloc` + `SetThreadAffinityMask` + AVX non-temporal stores
- **Console output** via [Spectre.Console](https://spectreconsole.net/) for rich table formatting

## License

[MIT](LICENSE)
