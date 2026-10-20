# PixView — fast photo viewer for Windows, in the spirit of Picasa Photo Viewer

[![Latest release](https://img.shields.io/github/v/release/Profesor08/pixview)](https://github.com/Profesor08/pixview/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/Profesor08/pixview/total)](https://github.com/Profesor08/pixview/releases)
[![License: GPL-3.0](https://img.shields.io/github/license/Profesor08/pixview)](LICENSE)
![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)

Double-click a photo and it opens **instantly over your dimmed desktop** — the way Picasa Photo Viewer did. No library, no import, no editing, no ads, no account. Just your pictures.

PixView is a free, open-source image viewer for Windows 10 and 11: a single ~300 KB exe that opens HEIC, AVIF, WebP, JPEG XL, camera RAW, animated GIF/APNG and 35+ other formats.

**[⬇ Download PixView.exe](https://github.com/Profesor08/pixview/releases/latest/download/PixView.exe)** — no installation needed.

## Why PixView

- **Opens in a blink.** A tiny native exe: no Electron, no runtime to download, no splash screen.
- **Picasa-style viewing.** The photo floats over the dimmed desktop — or fills the whole screen. Esc closes it.
- **Filmstrip** of the whole folder at the bottom: drag it, scroll it, page with the arrow keys.
- **Smooth zoom** towards the cursor, fit to screen / 1:1, pan, rotate, slideshow.
- **Modern formats:** HEIC/HEIF (iPhone photos), AVIF, WebP, JPEG XL, camera RAW (CR2, CR3, NEF, ARW, DNG…), animated GIF, APNG, WebP and AVIF, multi-page TIFF.
- **Rare formats too,** with its own decoders: TGA, QOI, Radiance HDR, PCX, PBM/PGM/PPM/PAM, ICNS, EMF/WMF.
- **EXIF details:** camera, exposure, aperture, focal length, date taken.
- **Tells you what's missing:** if a format needs a free Microsoft Store codec, PixView names it.
- **13 languages** and keyboard shortcuts for everything.
- **Private and portable:** works offline, sends nothing anywhere, needs no admin rights.

A lightweight alternative to Windows Photo Viewer and the Photos app.

## Install

**Download:** grab [`PixView.exe`](https://github.com/Profesor08/pixview/releases/latest/download/PixView.exe) from the [latest release](https://github.com/Profesor08/pixview/releases/latest), put it in a folder where it will stay (for example `C:\Programs\PixView`) and run it.

**winget:**

```sh
winget install Profesor08.PixView
```

Then type `pixview` in a terminal or the Run box (Win+R) to start it.

## Making it the default viewer

On first launch PixView offers to become your default image viewer. Say yes, and Windows opens Settings → Default apps, where you confirm with one click. You can do it later in **Settings** (the ⚙ button at the top): it lists every supported format, popular ones first, and ticks the ones PixView already opens. Settings also have "Make default…" and "Remove from “Open with”" buttons and the language choice.

Windows 10/11 does not let programs make themselves the default app silently, so the last click is always yours.

From the command line:

- `PixView.exe --register` adds PixView to "Open with" for the current user (no admin rights needed) and opens Settings → Default apps. Registration remembers where the exe is, so run it again if you move the exe.
- `PixView.exe --unregister` removes it.

Settings are stored in `HKCU\Software\PixView\Settings`.

## Languages

English, русский, українська, Deutsch, français, español, italiano, português (Brasil), polski, Türkçe, 中文 (简体), 日本語, 한국어.

The language follows the Windows display language (English if it is not in the list) unless one is picked in Settings. To force one: `PixView.exe --lang=de photo.jpg` or the `PIXVIEW_LANG=de` environment variable. Numbers and dates follow the Windows regional format. All UI text lives in [Strings.cs](src/Strings.cs); a new language is one more block of `key=text` lines.

## Formats

Some formats are decoded by Windows (WIC), others by the viewer's own decoders ([Decoders.cs](src/Decoders.cs)).

| Format | Requires |
|---|---|
| JPG, PNG, APNG (animated), GIF (animated), BMP, TIFF (multi-page), ICO, CUR, DDS, JPEG XR | nothing |
| TGA, PCX, PBM/PGM/PPM/PNM/PAM, QOI, HDR (Radiance, tone-mapped), ICNS, EMF/EMF+/WMF | nothing (own decoders) |
| WebP (including animation) | "WebP Image Extensions" |
| HEIC / HEIF | "HEIF Image Extensions" |
| AVIF, animated AVIF | "AV1 Video Extension" + "HEIF Image Extensions" |
| JPEG XL | "JPEG XL Image Extension" |
| Camera RAW (CR2, CR3, NEF, ARW, DNG …) | "Raw Image Extension" |

All extensions are free in the Microsoft Store. Any other codec installed on the system is picked up automatically, and when a codec is missing the viewer says which extension to install. EXIF orientation is respected.

Limitations: animated AVIF with 10/12 bits per channel shows only its first frame (the system decoder hands over frames in the wrong format); transparency in animated AVIF is not supported; ICNS files whose icons are stored as JPEG 2000 do not open (Windows has no JPEG 2000 codec).

## Controls

| Action | Keys / mouse |
|---|---|
| Next / previous | → ←, D / A, PgDn / PgUp, Space / Backspace, mouse side buttons, Ctrl+wheel |
| First / last | Home / End |
| Pages of a multi-page TIFF | Ctrl+PgDn / Ctrl+PgUp, toolbar buttons |
| Zoom | mouse wheel (towards the cursor), + / −, ↑ ↓ / W S |
| Fit to screen / 1:1 | 0 / 1; double-click or Enter toggles |
| Pan | drag with the mouse |
| Scroll the filmstrip | drag it, mouse wheel over it, arrows at its ends |
| Rotate (view only) | R / L (Shift+R) |
| Slideshow | F5 |
| Details (EXIF) | I |
| Hide the bars | Tab |
| Photo-sized window / full screen | drag the top bar; double-click it, F11 or the ❐ button toggles |
| Minimize | button at the top right |
| Dimmed / black background | B |
| Copy image | Ctrl+C |
| Show in folder | Ctrl+E |
| Open file | Ctrl+O |
| Close | Esc; in full screen also a click beside the photo (with the details panel open, that click closes only the panel) |

## FAQ

### How do I open HEIC (iPhone) photos on Windows 10 / 11?

Install PixView and the free [HEIF Image Extensions](https://apps.microsoft.com/detail/9pmmsr1cgpwg) from the Microsoft Store, then double-click any `.heic` file. PixView tells you if the extension is missing.

### How do I view AVIF, WebP or JPEG XL images on Windows?

PixView opens them, including animated WebP and AVIF. Windows needs the free codecs from the Microsoft Store: WebP Image Extensions, AV1 Video Extension + HEIF Image Extensions for AVIF, JPEG XL Image Extension for `.jxl`.

### Can I open camera RAW files (CR2, CR3, NEF, ARW, DNG, RAF, ORF, RW2)?

Yes, with the free Raw Image Extension from the Microsoft Store. PixView shows the full image and EXIF details: camera, exposure, aperture, focal length.

### Is there a replacement for Picasa Photo Viewer?

Picasa was discontinued in 2016. PixView brings back its viewer: the photo opens over the dimmed desktop, with a filmstrip, zoom and slideshow. It does not manage albums or edit photos.

### Can I get the old Windows Photo Viewer back in Windows 11?

PixView is a modern take on it: just as quick to open, but it also handles HEIC, AVIF, WebP, JPEG XL, RAW and animations, which the old viewer never could.

### Is PixView free? Does it have ads or telemetry?

Free and open source (GPL-3.0). No ads, no account, no telemetry: it never connects to the internet.

### Does it need installation or admin rights?

No. It is a single portable exe. Making it the default viewer is per user and needs no admin rights either.

## All supported file extensions

`.jpg` `.jpeg` `.jpe` `.jfif` `.png` `.apng` `.gif` `.bmp` `.dib` `.tif` `.tiff` `.webp` `.heic` `.heif` `.hif` `.avif` `.avifs` `.jxl` `.jxr` `.wdp` `.hdp` `.ico` `.cur` `.dds` `.tga` `.pcx` `.qoi` `.hdr` `.pbm` `.pgm` `.ppm` `.pnm` `.pam` `.emf` `.wmf` `.icns`, and with the Raw Image Extension: `.dng` `.cr2` `.cr3` `.crw` `.nef` `.nrw` `.arw` `.srf` `.sr2` `.raf` `.orf` `.rw2` `.pef` `.srw` `.3fr` `.erf` `.kdc` `.mrw` `.x3f` and other camera RAW formats. Any other WIC codec installed on the system is picked up automatically.

## Building

```sh
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `bin\PixView.exe`. No SDK is needed: the build uses the `csc.exe` that comes with Windows (WPF on .NET Framework 4.x, which ships with every Windows 10/11). Pushing a `v*` tag builds the exe on GitHub Actions and publishes a release.

## License

Copyright © 2026 Profesor08

PixView is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](LICENSE) or (at your option) any later version. It comes with no warranty.
