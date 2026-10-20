# PixView

A lightweight image viewer in the spirit of Picasa Photo Viewer: viewing only, no editing.
A single `PixView.exe` with no dependencies (WPF on .NET Framework 4.x, which ships with every Windows 10/11).

## Building

```sh
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output: `bin\PixView.exe`. No SDK is needed: the build uses the `csc.exe` that comes with Windows.

## Making it the default viewer

1. Put `PixView.exe` where it will stay (registration remembers the path).
2. Run `PixView.exe --register`. It registers for the current user (no admin rights needed) and opens Settings → Default apps.
3. Choose PixView there for the file types you want (or use "Open with → Always" in Explorer).

Windows 10/11 does not let programs make themselves the default app silently, so the last step is always yours.

The easier way: on first launch the viewer asks whether to become the default (unless it already opens jpg/png). You can do it later in **Settings** (the ⚙ button at the top). Settings list every supported format, popular ones first, and tick the ones PixView already opens. They also have "Make default…" and "Remove from “Open with”" buttons and the language choice. Settings are stored in `HKCU\Software\PixView\Settings`.

To remove the registration: `PixView.exe --unregister`.

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
