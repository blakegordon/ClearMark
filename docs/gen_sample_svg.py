"""Generate docs/output.svg from the reference-machine sample text."""
from pathlib import Path

LINES = r"""
ClearMark v1.0 — Transparent System Benchmark
https://github.com/blakegordon/ClearMark — All scoring formulas are visible in source code.

╔═ClearMark v1.0═══════════════════════════════════════════════════════╗
║  CPU:  2x Intel(R) Xeon(R) Gold 6244 CPU @ 3.60GHz (16C/32T, X64)    ║
║  RAM:  766.7 GB @ 2400 MHz                                           ║
║  GPU:  NVIDIA GeForce RTX 4090           PCIe 3.0 x16  32.0.15.8180  ║
║  GPU:  NVIDIA TITAN V                    PCIe 3.0 x16  32.0.15.8180  ║
║  Disk: WD_BLACK SN850X 4000GB            PCIe 3.0 x4                 ║
║  OS:   Microsoft Windows 10.0.22000                                  ║
╚══════════════════════════════════════════════════════════════════════╝

                    CPU
┌──────────────────┬───────────────┬───────┐
│ Test             │        Result │ Score │
├──────────────────┼───────────────┼───────┤
│ Integer (1T)     │    2,163 Mops │    72 │
│ Integer (nT)     │   33,603 Mops │   168 │
│ Float (1T)       │    933 Mflops │    31 │
│ Float (nT)       │ 16,429 Mflops │    75 │
│ Crypto (1T)      │      277 MB/s │    14 │
│ Crypto (nT)      │    4,260 MB/s │    30 │
│ Compression (1T) │    1,037 MB/s │    41 │
│ Compression (nT) │   12,914 MB/s │    72 │
└──────────────────┴───────────────┴───────┘

                    Memory
┌─────────────────────┬──────────────┬───────┐
│ Test                │       Result │ Score │
├─────────────────────┼──────────────┼───────┤
│ Seq. Bandwidth (1T) │   8,504 MB/s │    21 │
│ Seq. Bandwidth (nT) │ 129,244 MB/s │   162 │
│ Random Latency      │      95.9 ns │    73 │
│ Copy Bandwidth (1T) │  10,401 MB/s │    15 │
│ Copy Bandwidth (nT) │ 153,197 MB/s │   219 │
└─────────────────────┴──────────────┴───────┘

                  Memory Latency Ladder
┌─────────────┬─────────┬────────────────────────────────┐
│ Working Set │ Latency │                                │
├─────────────┼─────────┼────────────────────────────────┤
│ 4 KB        │  1.4 ns │ █                              │
│ 8 KB        │  1.4 ns │ █                              │
│ 16 KB       │  1.4 ns │ █                              │
│ 32 KB       │  1.4 ns │ █                              │
│ 64 KB       │  3.5 ns │ █                              │
│ 128 KB      │  3.5 ns │ █                              │
│ 256 KB      │  3.5 ns │ █                              │
│ 512 KB      │  4.7 ns │ █                              │
│ 1 MB        │  5.8 ns │ █                              │
│ 2 MB        │ 22.4 ns │ ███████                        │
│ 4 MB        │ 22.4 ns │ ███████                        │
│ 8 MB        │ 23.2 ns │ ███████                        │
│ 16 MB       │ 27.9 ns │ █████████                      │
│ 32 MB       │ 62.2 ns │ ████████████████████           │
│ 64 MB       │ 84.5 ns │ ████████████████████████████   │
│ 128 MB      │ 90.0 ns │ ██████████████████████████████ │
└─────────────┴─────────┴────────────────────────────────┘
  L1 │ L2 │ L3 │ RAM

               Storage
┌───────────────┬────────────┬───────┐
│ Test          │     Result │ Score │
├───────────────┼────────────┼───────┤
│ Seq. Write    │ 2,384 MB/s │    60 │
│ Seq. Read     │ 2,417 MB/s │    48 │
│ 4K Rand Write │   191 MB/s │    96 │
│ 4K Rand Read  │    62 MB/s │    78 │
└───────────────┴────────────┴───────┘

GPU: NVIDIA GeForce RTX 4090 (primary)
┌─────────────┬───────────────┬───────┐
│ Test        │        Result │ Score │
├─────────────┼───────────────┼───────┤
│ GPU FP32    │ 27,367 Mpix/s │   342 │
│ GPU FP64    │    395 Mpix/s │   263 │
│ GPU Integer │  30,466 GIOPS │   305 │
└─────────────┴───────────────┴───────┘

GPU: NVIDIA TITAN V (secondary — not scored)
┌─────────────┬──────────────┬───────┐
│ Test        │       Result │ Score │
├─────────────┼──────────────┼───────┤
│ GPU FP32    │ 7,136 Mpix/s │    89 │
│ GPU FP64    │ 4,659 Mpix/s │ 3,106 │
│ GPU Integer │ 10,927 GIOPS │   109 │
└─────────────┴──────────────┴───────┘

                       Composite Scores
╔══════════════╦═══════╦══════════════════════════════════════╗
║ Profile      ║ Score ║ Weights (1T / nT / Mem / Stor / GPU) ║
╠══════════════╬═══════╬══════════════════════════════════════╣
║ Gaming       ║   170 ║ 20% / 10% / 10% / 20% / 40%          ║
║ Productivity ║   146 ║ 10% / 30% / 15% / 15% / 30%          ║
║ Balanced     ║   119 ║ 20% / 20% / 20% / 20% / 20%          ║
╚══════════════╩═══════╩══════════════════════════════════════╝

Score of 100 = mid-range 2024 desktop baseline. Above 100 = better than baseline.
Gaming: skipped storage/GPU score 0 (not redistributed); FP64 excluded. Productivity/Balanced redistribute skips and include FP64.
""".strip("\n").split("\n")

# Ladder bar colors for this dual-Xeon (L1 32KB, L2 1MB, L3 ~25MB)
LADDER_BAR = {
    "4 KB": "gn", "8 KB": "gn", "16 KB": "gn", "32 KB": "gn",
    "64 KB": "yl", "128 KB": "yl", "256 KB": "yl", "512 KB": "yl", "1 MB": "yl",
    "2 MB": "or", "4 MB": "or", "8 MB": "or", "16 MB": "or",
    "32 MB": "rd", "64 MB": "rd", "128 MB": "rd",
}


def score_cls(n: int) -> str:
    if n >= 120:
        return "bg"
    if n >= 90:
        return "gn"
    if n >= 70:
        return "yl"
    if n >= 50:
        return "or"
    return "rd"


def esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace(" ", "&#160;")


def colorize(line: str) -> str:
    stripped = line.rstrip()
    if stripped.strip() in ("CPU", "Memory", "Storage") or stripped.strip().startswith("Memory Latency"):
        cls = {"CPU": "yl bw", "Memory": "bl bw", "Storage": "gn bw"}.get(stripped.strip(), "bw")
        pad = len(line) - len(line.lstrip(" "))
        return esc(" " * pad) + f'<tspan class="{cls}">{esc(stripped.strip())}</tspan>'

    if stripped.startswith("GPU:"):
        # "GPU: NVIDIA GeForce RTX 4090 (primary)"
        if "(primary)" in stripped:
            name, rest = stripped.split(" (primary)", 1)
            return f'<tspan class="yl bw">{esc(name)}</tspan>&#160;(primary){esc(rest)}'
        if "(secondary" in stripped:
            name, rest = stripped.split(" (secondary", 1)
            return f'<tspan class="yl bw">{esc(name)}</tspan>&#160;(secondary{esc(rest)}'
        return f'<tspan class="yl bw">{esc(stripped)}</tspan>'

    if stripped.strip() == "Composite Scores":
        pad = len(line) - len(line.lstrip(" "))
        return esc(" " * pad) + '<tspan class="bw">Composite&#160;Scores</tspan>'

    if stripped.startswith("  L1"):
        return '&#160;&#160;<tspan class="gn">L1</tspan>&#160;│&#160;<tspan class="yl">L2</tspan>&#160;│&#160;<tspan class="or">L3</tspan>&#160;│&#160;<tspan class="rd">RAM</tspan>'

    if stripped.startswith("ClearMark v1.0"):
        return '<tspan class="cy">ClearMark&#160;v1.0</tspan>&#160;—&#160;Transparent&#160;System&#160;Benchmark'

    if stripped.startswith("https://"):
        return f'<tspan class="dm">{esc(stripped)}</tspan>'

    if stripped.startswith("Score of 100") or stripped.startswith("Gaming: skipped"):
        return f'<tspan class="dm">{esc(stripped)}</tspan>'

    # Ladder data rows: color the bar
    if stripped.startswith("│ ") and " ns │" in stripped and "█" in stripped:
        label = stripped[2:13].strip()
        cls = LADDER_BAR.get(label, "rd")
        pre, bar_and_rest = stripped.split("│ ", 2)[0], stripped
        # split at bar
        i = stripped.index("█")
        j = stripped.rindex("█") + 1
        return esc(stripped[:i]) + f'<tspan class="{cls}">{esc(stripped[i:j])}</tspan>' + esc(stripped[j:])

    # Score column: "│    72 │" (3 seps) or composite "║   170 ║ weights ║" (4 seps)
    for sep in ("│", "║"):
        nsep = stripped.count(sep)
        if nsep >= 3 and "Score" not in stripped and "Profile" not in stripped and "Test" not in stripped:
            parts = stripped.rsplit(sep, 3 if nsep >= 4 else 2)
            for i in range(len(parts) - 1, 0, -1):
                token = parts[i].strip().replace(",", "")
                if token.lstrip("-").isdigit():
                    n = int(token)
                    cls = score_cls(n)
                    rebuilt = esc(parts[0])
                    for j in range(1, len(parts)):
                        rebuilt += sep
                        if j == i:
                            rebuilt += f'<tspan class="{cls}">{esc(parts[j])}</tspan>'
                        else:
                            rebuilt += esc(parts[j])
                    return rebuilt

    return esc(line)


def main() -> None:
    lh = 17
    top = 22
    height = top + lh * len(LINES) + 16
    width = 760
    out = []
    out.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}">')
    out.append("  <style>")
    out.append("    text { font-family: 'Cascadia Code','Consolas','SF Mono','Courier New',monospace; font-size: 13px; fill: #ccc; }")
    out.append("    .cy { fill: #56b6c2; font-weight: bold; }")
    out.append("    .dm { fill: #777; }")
    out.append("    .bw { font-weight: bold; }")
    out.append("    .yl { fill: #e5c07b; }")
    out.append("    .bl { fill: #61afef; }")
    out.append("    .gn { fill: #98c379; }")
    out.append("    .bg { fill: #50fa7b; font-weight: bold; }")
    out.append("    .or { fill: #d19a66; }")
    out.append("    .rd { fill: #e06c75; }")
    out.append("  </style>")
    out.append('  <rect width="100%" height="100%" rx="8" fill="#1e1e1e"/>')
    out.append('  <g xml:space="preserve">')
    y = top
    for line in LINES:
        inner = colorize(line) if line.strip() else ""
        if line.strip():
            out.append(f'    <text x="16" y="{y}">{inner}</text>')
        y += lh
    out.append("  </g>")
    out.append("</svg>")
    dest = Path(__file__).with_name("output.svg")
    dest.write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"wrote {dest} ({height}px, {len(LINES)} lines)")


if __name__ == "__main__":
    main()
