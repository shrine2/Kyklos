# Third-party notices

Kyklos is MIT-licensed. The pieces it did not write are listed here, in two groups:
the byte-embedded dependency and the external programs it drives.

## Bundled with the exe

| Component | Use | License |
| --- | --- | --- |
| [PdfPig](https://github.com/UglyToad/PdfPig) (NuGet 0.1.16) | PDF → text | Apache License 2.0 |

Everything else inside the exe is Kyklos itself or Microsoft's .NET 9 runtime, and the
Windows APIs it calls (WinForms, WIC, WinRT `Windows.Graphics.Imaging` and
`Windows.Data.Pdf`, GDI+) are parts of Windows.

## External programs, never bundled

These live on the user's machine, installed by the user's own choice with their own
installer, licensing and updates. Kyklos launches them as separate processes; their code is
not copied, modified or shipped. Availability is probed and offered accordingly.

| Program | Used for | Suggested install | License |
| --- | --- | --- | --- |
| [ffmpeg](https://ffmpeg.org/) | audio / video conversion, AVIF | `winget install Gyan.FFmpeg` | LGPL/GPL depending on build |
| [LibreOffice](https://www.libreoffice.org/) | Office, ODF and EPUB documents | `winget install TheDocumentFoundation.LibreOffice` | MPL 2.0 |

The Gyan builds of ffmpeg are recommended because that is what development runs against;
any ffmpeg on `PATH` works, and its own license applies.
