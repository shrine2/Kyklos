# Tangerine for Windows

A Windows take on Tangerine — grab a file, get a radial
wheel of formats, pick one, done. Conversions happen in place; there is no import step, no
project file, no window to clean up afterwards.

The original is a macOS/Apple-silicon app. This is a native Windows rebuild: C# / .NET 9,
WinForms, a real tray application, no Electron and no runtime to install.

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
| **Drop target** | The floating tangerine icon (bottom-right by default) | Drag files onto it, or click it to use the current Explorer selection |

The drag trigger is configurable (`Ctrl+Shift`, `Alt+Shift`, `Shift`, `Alt`) because plain
*Shift*+drag in Explorer means "move". The hotkey is configurable too, and both live in
**Settings**.

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
xlsx, xls, ods, csv, xml, json, log, pages, numbers, key, wpd, wps.
Output: **PDF, TXT, DOCX, ODT, EPUB, HTML, RTF** — *requires LibreOffice (see below)*.

\* HEIC output needs the Microsoft Store **HEIF Image Extensions** package. Without it, HEIC
is greyed out with that reason.

Every target is checked against a live capability probe of the local `ffmpeg` at startup, so
the wheel only ever shows things this machine can actually produce.

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
dotnet build "src\Tangerine\Tangerine.csproj"
& "src\Tangerine\bin\Debug\net9.0-windows10.0.19041.0\Tangerine.exe"

# one self-contained exe (~54 MB, no runtime required)
dotnet publish "src\Tangerine\Tangerine.csproj" -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o dist
```

The app is single-instance. Anything launched a second time — including the **Send to →
Convert with Tangerine** shortcut, which Settings can install for you — hands its file list to
the running tray over `WM_COPYDATA` and exits.

## Settings

Stored as JSON at `%APPDATA%\TangerineWin\settings.json`; a crash log lands beside it.

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
Tangerine.exe --selftest "C:\temp\tangerine-selftest" all
Tangerine.exe --selftest "C:\temp\tangerine-selftest" video    # images|video|audio|pdf|docs|all
```

Quote any path containing spaces. It writes `selftest-report.txt` into the target folder and
sets its exit code to the number of failed checks. `--smoketest` boots the tray UI, then shuts
it down the way the Exit menu item does — useful for verifying the GUI starts and tears down
cleanly.

## Project layout

```
src/Tangerine
  Program.cs              entry point, single-instance mutex, IPC hand-off, --selftest/--smoketest
  TrayApp.cs              orchestrator: tray icon, hotkey, drag surface, wheel, conversions
  Core/
    Converter.cs          dispatch, in-place staging, batch runs
    FormatCatalog.cs      target tables, availability against the live probe
    Toolchain.cs          ffmpeg / soffice discovery and capability probe
    PdfTools.cs  WicImage.cs  OfficeTools.cs
    Settings.cs  Paths.cs  SelfTest.cs
  Interop/
    NativeMethods.cs      Win32, incl. the teardown fix described below
    ModifierWatcher.cs    arms the drop surface on modifiers + pointer-down over the shell
    HotkeyWindow.cs  Hotkeys.cs  IpcWindow.cs  ClipboardFiles.cs
  Ui/
    RadialMenuForm.cs  DropSurfaceForm.cs  DropTargetForm.cs
    SettingsForm.cs  AdvancedDialog.cs  ProgressForm.cs  Theme.cs  Dark.cs
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
