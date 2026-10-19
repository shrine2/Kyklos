# Changelog

All notable changes to Kyklos. Format follows [Keep a Changelog](https://keepachangelog.com/).

## 1.0.0 - 2026-10-19

First public release.

- **The wheel** over any file — from Explorer (`Ctrl+Alt` drag or `Ctrl+Alt+C`), from the
  floating drop target, or from the Send to menu. Pick a format wedge and the file converts
  in place; first run offers one hint balloon and never again.
- **Images**: JPG, JPEG, JFIF, PNG, WebP, HEIC/HEIF (Windows codec via WIC), TIFF, BMP, GIF,
  AVIF, ICO, JXL, TGA, DDS, PSD, EXR, HDR read. Targets PNG, JPG, WebP, AVIF, TIFF, BMP, GIF,
  HEIC, PDF. EXIF orientation is applied, lossy quality fixed at 92. Compression, resize and
  no-metadata tools sit on the wheel itself.
- **Documents** (needs LibreOffice): DOCX, DOC, DOCM, ODT, RTF, TXT, MD, HTML, EPUB in; out to
  PDF, TXT, DOCX, ODT, EPUB, HTML, RTF — and PDF goes to text through PdfPig.
- **Video / audio** (needs ffmpeg): MP4, MOV, MKV, WebM, AVI, WMV, TS, FLAC, WAV, MP3,
  OPUS and more.
- **Formats are probed, not promised**: every target is checked against what the machine can
  actually produce, and unavailable ones are greyed out with the reason.
- **A single 54 MB exe**, self-contained, per-user install, no admin rights, MIT licensed.

MIT — see [LICENSE](LICENSE).
