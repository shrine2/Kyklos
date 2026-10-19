# Kyklos for Windows

A Windows take on Tangerine — grab a file, get a radial
wheel of formats, pick one, done. Conversions happen in place; there is no import step, no
project file, no window to clean up afterwards.

The original is a macOS/Apple-silicon app called Tangerine. This is a native Windows rebuild,
under its own name: C# / .NET 9, WinForms, a real tray application, no Electron and no runtime
to install.

---

## What it does

Trigger the wheel over any file — from Explorer, from a drop target, or from the clipboard —
and the ring of formats appears under the cursor. Hover a format to see what it produces and
how big it will be; click to convert. Unavailable formats are greyed out with a reason rather
than failing after the fact.

Three ways to get the wheel:

| Trigger | How | Notes |
| --- | --- | --- |
| **Drag trigger** | Hold `Ctrl+Alt` and start dragging a file in Explorer | An invisible full-screen drop surface arms itself; drag over it and the wheel opens |
| **Global hotkey** | `Ctrl+Alt+C` | Converts whatever is selected in Explorer, or the files on the clipboard |
| **Drop target** | The floating Kyklos disc (bottom-right by default) | Drag files onto it, or click it to use the current Explorer selection |

The drag trigger is configurable (`Ctrl+Shift`, `Alt+Shift`, `Shift`, `Alt`) because plain
*Shift*+drag in Explorer means "move". The hotkey is configurable too, and both live in
**Settings**.

## While it converts

The wheel is the progress display. Once you commit a format, the hub turns into a rotating
spinner, the line under the wheel counts the batch (`Converting 3 / 12`) and names the file
being worked on, and the wedges stop responding. When the batch finishes the wheel fades out
and disappears — a second conversion started straight afterwards never stacks a window on top
of the first, because no window is ever opened.

Cancelling:

- an **interactive** wheel (hotkey, drop target, tray menu) takes a click on the hub, or `Esc`;
- a **drag-driven** wheel stays click-through on purpose, so a long job cannot swallow clicks
  on whatever is underneath — cancel those with `Esc`.

If something fails, a tray balloon says how many; the per-file error text goes to
`%APPDATA%\Kyklos\conversions.log`.

## Formats

**Images** input: jpg, jpeg, jfif, png, webp, heic/heif, tif/tiff, bmp, gif, avif, ico, jxl,
tga, dds, psd, exr, hdr.
Output: **PNG, JPG, WebP, AVIF, TIFF, BMP, GIF, HEIC\*, PDF**.

**Video** input: mp4, m4v, mov, mkv, webm, avi, wmv, asf, mpg/mpeg, flv, ts, mts, m2ts, vob,
ogv, 3gp, divx, rm, rmvb.
Output: **MP4, MKV, MOV, WebM, AVI, WMV, GIF** — plus audio extraction to **MP3, M4A, WAV, FLAC**.

**Audio** input: mp3, m4a, m4b, wav, flac, ogg, oga, opus, aiff, aif, wma, aac, alac, ape,
mka, ac3, dts, amr, mp2, wv.
Output: **MP3, M4A, WAV, FLAC, OGG, OPUS, AIFF, WMA**.

**PDF** input: any `.pdf`.
Output: **PNG, JPG** (300 DPI page images), **TXT** (extracted text).

**Documents** input: docx, doc, docm, odt, rtf, txt, md, html, htm, epub, pptx, ppt, odp,
xlsx, xls, ods, xml, json, log, pages, numbers, key, wpd, wps.
Output: **PDF, TXT, DOCX, ODT, EPUB, HTML, RTF** — *requires LibreOffice (see below)*.

**Subtitles** input: srt, vtt. Output: **TXT, SRT, VTT**.

**Tables** input: csv, tsv. Output: **CSV, TSV**.

Subtitles and tables are done inside the app, so they still work when ffmpeg and LibreOffice
are both missing.

\* HEIC output needs the Microsoft Store **HEIF Image Extensions** package. Without it, HEIC
is greyed out with that reason.

Every target is checked against a live capability probe of the local `ffmpeg` at startup, so
the wheel only ever shows things this machine can actually produce. A file's own format is
never offered as a target, so the ring always means something.

### Tools, in place of the macOS frameworks

| Job | macOS original | This build |
| --- | --- | --- |
| Audio/video | AVFoundation | `ffmpeg` |
| Images | ImageIO | `ffmpeg`, with Windows WIC for HEIC/AVIF/JXL |
| Images → PDF | PDFKit | hand-rolled JPEG/DCTDecode PDF writer |
| PDF → images | PDFKit | WinRT `Windows.Data.Pdf` |
| PDF → text | PDFKit | PdfPig |
| Office / EPUB | `textutil` | headless LibreOffice (`soffice`) |

## Requirements

- Windows 10 2004 (build 19041) or newer, x64.
- **ffmpeg** on `PATH` or pointed at from Settings. The
  [Gyan full build](https://www.gyan.dev/ffmpeg/builds/) is what this was developed against:
  `winget install --id Gyan.FFmpeg -e`.
- **LibreOffice** — optional, and only needed for document conversions:
  `winget install --id TheDocumentFoundation.LibreOffice -e`. Without it those targets appear
  greyed out marked "needs LibreOffice".
- A checkout builds with the **.NET 9 SDK**; the published single-file build needs nothing
  installed at all.

## Build and run

```powershell
# develop
dotnet build "src\Kyklos\Kyklos.csproj"
& "src\Kyklos\bin\Debug\net9.0-windows10.0.19041.0\Kyklos.exe"

# one self-contained exe (~129 MB, no runtime required)
dotnet publish "src\Kyklos\Kyklos.csproj" -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist
```

Leave `-p:EnableCompressionInSingleFile=true` **off**. It would cut the exe to about 54 MB, but
Windows refuses to start the compressed bundle on any machine where **Smart App Control** is
enforcing — a bare *"An Application Control policy has blocked this file"*, with a
`Microsoft-Windows-CodeIntegrity` event naming the exe. Check with:

```powershell
(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState
# 0 = off, 1 = enforcing, 2 = evaluating
```

It is not a reputation problem that clears by itself: an uncompressed build of the same source
starts where a compressed one is blocked, every time. Smart App Control cannot be turned back on
once it is switched off, so leaving compression off is the cheaper trade.

The app is single-instance. Anything launched a second time — including the **Send to →
Convert with Kyklos** shortcut, which Settings can install for you — hands its file list to
the running tray over `WM_COPYDATA` and exits.

## Settings

Stored as JSON at `%APPDATA%\Kyklos\settings.json`; a crash log and the conversion failure log
land beside it. A fault raised inside a user-interface event goes to that crash log as well —
Kyklos says so and carries on rather than disappearing.

- **Output** — write beside the original, or into a subfolder (default `converted`).
- **If the name exists** — rename (default), overwrite, or skip.
- **Quality** — fast / balanced / best; feeds CRF, encoder preset and image quality.
- **Strip metadata** — drop EXIF/GPS and container tags on the way out.
- **Start with Windows**, plus the Send To shortcut and the drag-surface toggle.
- **Per-file overrides** from the wheel's tool ring: compress, trim/resize, extract audio,
  make GIF, to PDF.

Overwriting in place is handled by staging the output to a temporary path first and then
swapping it in, because some encoders (notably `ffmpeg`) refuse to write to their own input.

## Self-test

The binary carries an end-to-end harness that generates its own fixtures and converts them
through the real pipeline:

```powershell
Kyklos.exe --selftest "C:\temp\kyklos-selftest" all
Kyklos.exe --selftest "C:\temp\kyklos-selftest" video    # images|video|audio|pdf|text|docs|all
```

Quote any path containing spaces. It writes `selftest-report.txt` into the target folder and
sets its exit code to the number of failed checks.

Two more switches exist for checking the GUI itself:

```powershell
Kyklos.exe --smoketest "C:\temp\sample.png"
Kyklos.exe --uipreview "C:\temp\sample.png"
```

`--smoketest` boots the real tray, runs one genuine conversion through the very code path the
wheel uses, then walks the floating drop target through show/hide/show and clicks **Hide this
icon** in its own context menu, and finally exits the way **Exit** does. Every failure mode has
its own exit code, because a smoke test that can pass without testing anything is worse than no
test at all: `1` the drop target did not settle, `2` the conversion failed, `3` a UI-thread
exception was logged, `4` hiding the icon failed, `5` a phase threw. `--uipreview` opens the
wheel and drives it through a simulated batch so the spinner, the caption and the fade-out can
be looked at without converting anything; it is the quickest way to eyeball a layout change.

Both switches run ahead of the single-instance check, so they are safe to use while the tray is
up, and neither can block on a dialog — which makes them safe to run unattended.

## Project layout

```
src/Kyklos
  Program.cs              entry point, single-instance mutex, IPC hand-off, CLI switches
  TrayApp.cs              orchestrator: tray icon, hotkey, drag surface, wheel, conversions
  Core/
    Converter.cs          dispatch, in-place staging, batch runs
    FormatCatalog.cs      target tables, availability against the live probe
    Toolchain.cs          ffmpeg / soffice discovery and capability probe
    PdfTools.cs  WicImage.cs  OfficeTools.cs
    TextTools.cs          subtitle (SRT/VTT) and delimited (CSV/TSV) conversions
    Settings.cs  Paths.cs  SelfTest.cs
  Interop/
    NativeMethods.cs      Win32, incl. the teardown fix described below
    ModifierWatcher.cs    arms the drop surface on modifiers + pointer-down over the shell
    HotkeyWindow.cs  Hotkeys.cs  IpcWindow.cs  ClipboardFiles.cs
  Ui/
    RadialMenuForm.cs     the wheel; also draws the hub spinner and the fade-out
    DropSurfaceForm.cs  DropTargetForm.cs
    SettingsForm.cs  AdvancedDialog.cs  Theme.cs  Dark.cs
```

### Two notes worth keeping

**Why not intercept Shift+drag?** Explorer owns its OLE drag loop, and a modifier change
mid-drag can't be injected into it. Instead the app watches for the configured modifiers plus
a pointer-down over an Explorer surface and raises its own invisible full-screen drop surface
on top, so the drop lands on us. That's also why the default is `Ctrl+Alt` rather than `Shift`.

**Why `TerminateProcess` instead of a normal shutdown?** On AMD hardware a mix of WinRT, WIC
and GDI+ work makes `atidxx64.dll` fault during DLL detach, turning a clean exit into an access
violation *after* all output is written and all checks pass. `NativeMethods.HardExit` flushes
and then ends the process without running driver detach, which is safe here because everything
is already on disk. It is the difference between the self-test reporting `ALL CHECKS PASSED`
and the same run reporting an `0xC0000005`.

## Compared with the Mac original

The wheel geometry was matched against Leap Studio's own press screenshots of Tangerine 1.4
(the macOS build is App Store only, and needs macOS 13+ on Apple silicon, so there is no
binary to inspect here — the vendor's published spec and press images are the evidence):

- The first format is centred on 12 o'clock and the ring runs clockwise: JPG, PNG, WebP,
  HEIC, TIFF, AVIF, BMP, then PDF.
- A file's own format never appears on its own ring.
- The hub carries the size of the selection (macOS shows `2.2 MB`; this shows the same pill).
- Tool wedges are uppercase words only, as in the original: COMPRESS, RESIZE, NO META, PDF. A
  tool that is switched on is tinted rather than marked with a symbol.

Known differences, deliberately:

- **No AirDrop.** The macOS *Control* wheel is file actions (AirDrop, Float, Copy, Move, Copy
  Path, Terminal). There is no Windows equivalent of AirDrop, and the rest are Explorer's job.
- **Tools share the wheel** with formats instead of living on a separate Option/Command ring.
- **No macOS-only targets.** Pages, Numbers and Key documents, and PDF→PAGES/PPTX/XLSX, have
  no Windows converter.
- **Image → DOCX.** The macOS image wheel offers it; LibreOffice refuses `--convert-to docx`
  for a picture ("no export filter", it opens images in Draw), so it would need a hand-written
  OOXML writer.
- **Archives were out of scope** by choice — ZIP/TAR/GZIP are absent rather than half-done.

Everything else on the original's format list is present, and the Windows build adds targets
the original does not have: MP4→M4A/WAV/FLAC audio extraction, and WebP/AVIF/TIFF/BMP→GIF.
