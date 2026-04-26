# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the tools

**Python tool** (single-target ping):
```
python ping_tool.py
```

**C# tool** (multi-device monitor) — compile then run:
```
csc PingTool.cs /target:winexe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.dll /win32icon:app.ico /out:PingTool.exe
PingTool.exe
```
`csc` ships with the .NET SDK (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) or can be invoked via `dotnet-script`. The compiled `PingTool.exe` is git-ignored.

## Architecture

There are two independent implementations that share the same Catppuccin Mocha colour palette but serve different use cases:

### `ping_tool.py` — single-target utility
- Pure Python, tkinter UI, no dependencies beyond stdlib.
- Calls the Windows `ping` CLI via `subprocess` with flags `-n` (count), `-w` (timeout ms), `-l` (payload size bytes) and parses stdout with regex to extract RTT and TTL.
- A background `threading.Thread` runs the ping loop; all UI updates go through `root.after(0, ...)` to stay on the main thread.
- A live stats bar (sent / received / loss / avg RTT) updates after every reply.

### `PingTool.cs` — multi-device network monitor
Single file, two `Form` classes:

**`PingTool`** (main window)
- Holds a `List<DeviceStats>` — one entry per monitored IP.
- Each device gets its own background `Thread` running `PingLoop`, which uses `System.Net.NetworkInformation.Ping` (not subprocess). The 50 ms sleep-tick inside the loop allows the stop flag to be noticed quickly without blocking.
- A `System.Windows.Forms.Timer` at 500 ms calls `RefreshGrid()` to push stats to the `DataGridView` under a per-device lock.
- `SetControlState(bool ready)` disables/enables timeout, interval, and count controls while monitoring is running.
- Window size and maximised state are persisted to `settings.cfg` on close and restored on load.

**`NetworkScanForm`** (opened from "Network Scan" button)
- Accepts a CIDR range (up to /16, max 65 534 hosts).
- `RunScan` fans out up to 64 parallel threads, each claiming every N-th IP from the list. Results arrive back on the UI thread via `Invoke`.
- After all threads finish, `SortResultsByIP` does an in-place sort on the grid rows by the 3rd and 4th octets.
- MAC addresses are resolved via `SendARP` (P/Invoke into `iphlpapi.dll`) only for hosts that responded.
- Checked rows can be sent directly to the main monitor window via the `addCallback` delegate.

### Shared conventions
- All colours are defined as `static readonly Color` constants named after their role (`ColGreen`, `ColRed`, `FgText`, `BgDark`, etc.) using the Catppuccin Mocha palette — keep new UI elements consistent with these.
- `MakeLabel` / `MakeButton` / `MakeCellStyle` are local factory helpers in each form; there is intentionally no shared base class.
- `settings.cfg` (plain `key=value`) is read and written by the C# tool only; the Python tool has no persistence.
