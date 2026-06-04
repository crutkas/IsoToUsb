# IsoToUsb

A very basic Windows-only tool that turns a Windows install ISO into a bootable USB drive — drop the ISO, pick the drive, hit **Start**.

## Status
🚧 Functional MVP. The core pipeline (mount ➜ repartition ➜ copy ➜ split ➜ verify) is wired up and unit-tested. Live USB writes have not yet been smoke-tested by the author — try on a spare stick first.

## Goals
- **One screen.** Drag-and-drop an ISO, pick a USB drive, go.
- **Strong defaults.** UEFI-only target, GPT, single FAT32 partition labeled `WIN_USB`.
- **Safe.** Only USB / removable drives can be selected — internal disks are filtered out.
- **MIT-licensed.** Pure first-party code; no GPL components bundled.

## Requirements
- Windows 10 (1809) or later
- Administrator rights (UAC prompts only when you click **Start** — the UI itself runs unelevated)
- A target machine that boots in **UEFI** mode
- USB stick ≥ 8 GB (Windows install media doesn't fit smaller)

## Non-goals (v1)
- Linux ISOs / hybrid ISO9660 (Rufus/Ventoy style raw writes)
- Legacy BIOS / MBR
- Persistence partitions
- Internal-disk targets

## How it works
The app is split into two processes so the UI keeps OS shell features (file
picker, drag-drop) that Windows blocks for elevated processes:

1. **UI process** runs as `asInvoker` (standard user). You drag the ISO,
   pick the USB drive, click **Start**.
2. Clicking Start re-launches the same exe with `--worker` via
   `ShellExecuteEx(verb="runas")` — UAC prompts here, the **worker** runs
   elevated, and parent/worker talk over a same-user named pipe
   (tab-delimited progress lines: `P\tStage\tPercent\tMessage`, `L\t…`,
   `R\tTotal\tFailures`, `E\tType\tMessage`, parent→worker `CANCEL`).
3. Worker mounts the ISO read-only via `AttachVirtualDisk`.
4. Validates it's a Windows install ISO (`sources\boot.wim` present).
5. Repartitions the target USB via `diskpart.exe`: `clean` → `convert mbr` →
   `convert gpt` → `create partition primary` → `format fs=fat32 quick` →
   `assign`. (Pure-WMI repartitioning loses a race with Windows automount.)
6. Copies every file from the ISO.
7. If `sources\install.wim` is larger than 4 GB (FAT32's per-file limit), uses
   in-box `dism.exe /Split-Image` to split it into `install.swm` chunks —
   Windows Setup natively understands this.
8. Sample-verifies 20 random files with SHA-256.

## Known limitations
- **UEFI only.** Don't expect this image to boot on legacy-BIOS-only hardware.
- **Windows install ISOs only.** Linux distros need raw-image writes — out of scope.
- **No persistence / multi-boot.** One ISO per stick, FAT32 only.

## Build
```powershell
dotnet build .\IsoToUsb.slnx
```

## Test
```powershell
dotnet test .\IsoToUsb.slnx
```

## Run (from source)
```powershell
dotnet run --project .\src\IsoToUsb
```
The UI launches without a UAC prompt; UAC only appears when you click **Start**
(the elevated worker child process is launched then).

## Publish (single-file)
```powershell
dotnet publish .\src\IsoToUsb\IsoToUsb.csproj -c Release -p:PublishProfile=win-x64
```
Output: a single self-contained `IsoToUsb.exe` (~84 MB) at
`src\IsoToUsb\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\IsoToUsb.exe`.
Single-file Brotli compression is on; the `.exe` self-extracts to
`%LOCALAPPDATA%\Temp\.net\IsoToUsb\…` on first run.

> **On size — what's in there.** We reference the WinAppSDK 2.1 packages
> à-la-carte (`WinUI` + `Runtime` + `InteractiveExperiences`), skipping the
> umbrella `Microsoft.WindowsAppSDK` metapackage so we don't drag in
> `Microsoft.WindowsAppSDK.AI` (`onnxruntime.dll`, `DirectML.dll` — ~39 MB
> of ML runtime for Phi Silica / OCR APIs we don't use) or `Widgets`.
> The remaining bulk is the WinUI 3 XAML runtime (`Microsoft.ui.xaml.dll`,
> `Microsoft.UI.Xaml.Controls.dll`, DWM/DirectComposition interop) plus the
> self-contained .NET 10 runtime.
>
> **Why not NativeAOT?** `System.Management` (WMI disk enumeration) calls
> `WbemDefPath` / `WbemObjectTextSrc` constructors that the ILC stubs out,
> causing `MissingMethodException` at runtime even with `TrimmerRootAssembly`.
> Until either we replace WMI with direct P/Invoke or `System.Management`
> gets AOT-friendly, `<PublishAot>` stays off.

## Reference material (license-compatible)
This project only references documentation and MIT/permissively licensed code:
- Microsoft Learn — `AttachVirtualDisk`, `MSFT_Disk` / `MSFT_Volume` CIM classes, DISM `/Split-Image`
- [microsoft/CsWin32](https://github.com/microsoft/CsWin32) (MIT) — P/Invoke source generator
- [microsoft/WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) (MIT) — WinUI 3 framework

GPL-licensed prior art (Rufus, Ventoy, WoeUSB) was intentionally **not** consulted to keep this codebase MIT-clean.

## License
[MIT](./LICENSE)
