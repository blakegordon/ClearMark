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
| **Integer** | Sieve of Eratosthenes (ALU, branching, L2 cache), repeated ≥150 ms | ✓ |
| **Float** | 256×256 dense matrix multiply (FMA / cache) | ✓ |
| **Crypto** | SHA-256 hashing (hardware acceleration where available) | ✓ |
| **Compression** | Brotli Fastest **compress only** of incompressible random data | ✓ |

### Memory (5 tests)
| Test | What |
|---|---|
| **Seq. Bandwidth (1T)** | Pinned P-core sequential read + NT-store write (STREAM 2×) |
| **Seq. Bandwidth (nT)** | All-core, NUMA first-touch, pinned threads, separate read/write arrays |
| **Copy Bandwidth (1T)** | Pinned P-core AVX NT-store copy (STREAM 2×, same kernel as nT) |
| **Copy Bandwidth (nT)** | All-core AVX NT-store copy with processor-group pinning |
| **Random Latency** | Single-cycle pointer-chase at `max(4× L3, 1 GB)` |

Plus a **latency ladder** from 4 KB through `max(128 MB, 2× L3)`.

### Storage (4 tests)
| Test | What |
|---|---|
| **Seq. Read / Write** | 4 GB unbuffered sequential I/O at **QD1** (`File.OpenHandle` + `FILE_FLAG_NO_BUFFERING`) |
| **4K Random Read / Write** | 4 KB random I/O at **QD1** — desktop snappiness, not CrystalDiskMark QD32 |

### GPU (3 tests per device)
| Test | What |
|---|---|
| **FP32** | 4K×4K Mandelbrot (single precision) |
| **FP64** | 4K×4K Mandelbrot (double precision, if supported) |
| **Integer** | 16M-thread xorshift-multiply hash chain |

All GPUs with hardware DX12 support are benchmarked. Only the DXGI primary (display) GPU contributes to composite scores. FP64 is shown in the GPU table but is **not** part of the Gaming composite.

## Composite Scores

Three weighted profiles combine all category scores:

| Profile | CPU 1T | CPU nT | Memory | Storage | GPU | Use Case |
|---|---|---|---|---|---|---|
| **Gaming** | 20% | 10% | 10% | 20% | 40% | Frame rates, game loading |
| **Productivity** | 10% | 30% | 15% | 15% | 30% | Compilation, rendering, VMs |
| **Balanced** | 20% | 20% | 20% | 20% | 20% | General-purpose |

When storage or GPU is skipped (`--skip-storage` / `--skip-gpu`) or missing:

- **Gaming** — that category scores **0** (40% GPU cannot be inflated by skipping the GPU). FP64 is excluded from Gaming even when it ran.
- **Productivity** and **Balanced** — skipped-category weight is redistributed; FP64 is included in the GPU average.

## Sample Output

Scores are color-coded in the terminal: 🟢 **green** (≥ 90) · 🟡 **yellow** (70–89) · 🟠 **orange** (50–69) · 🔴 **red** (< 50). A score of **100 = mid-range 2024 desktop baseline**.

This run is from a dual-Xeon workstation with an RTX 4090 and a secondary Titan V (layout illustration; copy 1T units and Gaming/FP64 rules have changed since this capture):

![ClearMark sample output](docs/output.svg)

<details>
<summary>Text version (for copy-paste)</summary>

```
ClearMark v1.0 — Transparent System Benchmark
https://github.com/blakegordon/ClearMark — All scoring formulas are visible in source code.

╔═ClearMark v1.0══════════════════════════════════════════════════════╗
║  CPU:  2x Intel(R) Xeon(R) Gold 6244 CPU @ 3.60GHz (16C/32T, X64)  ║
║  RAM:  766.7 GB @ 2400 MHz                                         ║
║  GPU:  NVIDIA GeForce RTX 4090                                     ║
║  Disk: WD_BLACK SN850X 4000GB                                      ║
║  OS:   Microsoft Windows 10.0.22000                                ║
╚════════════════════════════════════════════════════════════════════╝

                    CPU                          Scores are color-coded:
┌──────────────────┬───────────────┬───────┐     🟢 ≥ 120  bold green
│ Test             │        Result │ Score │     🟢 90–119  green
├──────────────────┼───────────────┼───────┤     🟡 70–89   yellow
│ Integer (1T)     │    1,971 Mops │    66 │     🟠 50–69   orange
│ Integer (nT)     │   13,401 Mops │    67 │     🔴 < 50    red
│ Float (1T)       │    716 Mflops │    24 │
│ Float (nT)       │ 11,205 Mflops │    51 │
│ Crypto (1T)      │      279 MB/s │    14 │  ← Xeon lacks SHA-NI hardware
│ Crypto (nT)      │    4,298 MB/s │    31 │
│ Compression (1T) │      486 MB/s │    19 │
│ Compression (nT) │    3,023 MB/s │    17 │
└──────────────────┴───────────────┴───────┘

                    Memory
┌─────────────────────┬──────────────┬───────┐
│ Test                │       Result │ Score │
├─────────────────────┼──────────────┼───────┤
│ Seq. Bandwidth (1T) │   8,724 MB/s │    22 │
│ Seq. Bandwidth (nT) │ 104,064 MB/s │   130 │  ← 12 DDR4 channels saturated
│ Random Latency      │      68.1 ns │   103 │
│ Copy Bandwidth (1T) │   5,034 MB/s │    14 │
│ Copy Bandwidth (nT) │ 137,330 MB/s │   196 │  ← AVX NT stores + thread pinning
└─────────────────────┴──────────────┴───────┘

                  Memory Latency Ladder
┌─────────────┬─────────┬────────────────────────────────┐
│ Working Set │ Latency │                                │
├─────────────┼─────────┼────────────────────────────────┤
│ 4 KB        │  1.2 ns │ █                              │  ← L1 (green)
│ 8 KB        │  1.2 ns │ █                              │
│ 16 KB       │  1.2 ns │ █                              │
│ 32 KB       │  1.2 ns │ █                              │
│ 64 KB       │  2.2 ns │ █                              │  ← L2 (yellow)
│ 128 KB      │  2.7 ns │ █                              │
│ 256 KB      │  3.0 ns │ █                              │
│ 512 KB      │  4.1 ns │ █                              │
│ 1 MB        │  5.4 ns │ █                              │  ← L3 (orange)
│ 2 MB        │ 13.7 ns │ █████                          │
│ 4 MB        │ 17.9 ns │ ██████                         │
│ 8 MB        │ 20.8 ns │ ███████                        │
│ 16 MB       │ 25.0 ns │ █████████                      │
│ 32 MB       │ 45.7 ns │ ████████████████               │  ← RAM (red)
│ 64 MB       │ 67.7 ns │ ████████████████████████       │
│ 128 MB      │ 81.6 ns │ ██████████████████████████████ │
└─────────────┴─────────┴────────────────────────────────┘
  L1 ≈ green │ L2 ≈ yellow │ L3 ≈ orange │ RAM ≈ red

GPU: NVIDIA GeForce RTX 4090 (primary)
┌─────────────┬───────────────┬───────┐
│ Test        │        Result │ Score │
├─────────────┼───────────────┼───────┤
│ GPU FP32    │ 22,641 Mpix/s │   283 │
│ GPU FP64    │    622 Mpix/s │   415 │
│ GPU Integer │  30,583 GIOPS │   306 │
└─────────────┴───────────────┴───────┘

GPU: NVIDIA TITAN V (secondary — not scored)
┌─────────────┬──────────────┬───────┐
│ Test        │       Result │ Score │
├─────────────┼──────────────┼───────┤
│ GPU FP32    │ 6,585 Mpix/s │    82 │
│ GPU FP64    │ 3,170 Mpix/s │ 2,113 │  ← Volta 1:2 FP64:FP32 ratio
│ GPU Integer │ 10,698 GIOPS │   107 │
└─────────────┴──────────────┴───────┘

                       Composite Scores
╔══════════════╦═══════╦══════════════════════════════════════╗
║ Profile      ║ Score ║ Weights (1T / nT / Mem / Stor / GPU) ║
╠══════════════╬═══════╬══════════════════════════════════════╣
║ Gaming       ║   192 ║ 20% / 10% / 10% / 20% / 40%          ║
║ Productivity ║   153 ║ 10% / 30% / 15% / 15% / 30%          ║
║ Balanced     ║   125 ║ 20% / 20% / 20% / 20% / 20%          ║
╚══════════════╩═══════╩══════════════════════════════════════╝

Score of 100 = mid-range 2024 desktop baseline. Above 100 = better than baseline.
```

</details>

## Building from Source

Requires [.NET 10 SDK](https://dot.net) on Windows.

```bash
dotnet build --configuration Release
```

The compiled binary is at `bin/Release/net10.0-windows/ClearMark.exe`.

## Design Philosophy

- **Transparent** — Every baseline, weight, and formula is a readable constant in [`Scoring.cs`](Scoring.cs). No obfuscation.
- **Simple** — Each test is a short method you can read in a minute. Scoring is an enum + table, not string keys.
- **Honest** — Median of multiple iterations. No cherry-picking. Lower-is-better metrics (latency) are scored correctly.
- **NUMA-aware** — Multi-threaded memory tests use native memory allocation, first-touch page distribution, and thread pinning for accurate bandwidth measurement on multi-socket systems.

## Technical Details

- **GPU compute** via [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) (DX12 compute shaders)
- **Storage I/O** via `File.OpenHandle` + `RandomAccess` with `FILE_FLAG_NO_BUFFERING` — bypasses OS cache; QD1
- **Memory bandwidth** via `NativeMemory.AlignedAlloc` + `SetThreadGroupAffinity` + AVX non-temporal stores
- **Console output** via [Spectre.Console](https://spectreconsole.net/) for rich table formatting

## License

[MIT](LICENSE)
