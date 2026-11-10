# PCasa fixed native license baseline — libvips 8.18.7

This directory holds **checked-in original licensing texts** for the
version-pinned Windows x64 libvips image stack. Unlike links fetched on
each CI build, the files remain unchanged until explicitly reviewed.
They are copied into installed `Licenses/Native/libvips-8.18.7/` by
`src/PCasa.App/PCasa.App.csproj` and hash-compared to the checked-in
baseline during release inspection.

The definitive dependency version list is
`UPSTREAM-BUILD-README.md` from
https://github.com/libvips/build-win64-mxe/tree/v8.18.7.
The upstream redistributor's notices are preserved as
`UPSTREAM-THIRD-PARTY-NOTICES.md` from
https://github.com/kleisauke/libvips-packaging/tree/v8.18.7.
The two distributions are related, but they are **not automatically
identical**: identify the exact DLL bytes used by each installer.

## Frozen original license / attribution sources

| Component | Version | Original repository and ref | Original text |
|---|---|---|---|
| aom | 3.15.1 | https://github.com/libsdl-org/aom/v3.15.1 | LICENSE, PATENTS |
| cairo | 1.18.6 | https://github.com/sailfishos-mirror/cairo/1.18.6 | COPYING, COPYING-MPL-1.1 |
| cgif | 0.5.4 | https://github.com/dloebl/cgif/v0.5.4 | LICENSE |
| expat | 2.8.5 | https://github.com/libexpat/libexpat/R_2_8_5 | COPYING |
| fontconfig | 2.18.3 | https://github.com/fontconfig/fontconfig/2.18.3 | COPYING |
| freetype | 2.14.3 | https://github.com/freetype/freetype/VER-2-14-3 | LICENSE.TXT, docs/FTL.TXT |
| fribidi | 1.0.17 | https://github.com/fribidi/fribidi/v1.0.17 | COPYING |
| glib | 2.90.0 | https://github.com/GNOME/glib/2.90.0 | COPYING |
| harfbuzz | 14.5.0 | https://github.com/harfbuzz/harfbuzz/14.5.0 | COPYING |
| highway | 1.4.0 | https://github.com/google/highway/1.4.0 | LICENSE |
| lcms | 2.19.1 | https://github.com/mm2/Little-CMS/lcms2.19 | LICENSE |
| libarchive | 3.8.9 | https://github.com/libarchive/libarchive/v3.8.9 | COPYING |
| libexif | 0.6.26 | https://github.com/libexif/libexif/v0.6.26 | COPYING |
| libffi | 3.8.0 | https://github.com/libffi/libffi/v3.8.0 | LICENSE |
| libheif | 1.23.5 | https://github.com/strukturag/libheif/v1.23.5 | COPYING |
| libimagequant | 2.4.1 | https://github.com/lovell/libimagequant/v2.4.1 | COPYRIGHT |
| libnsgif | embedded in libvips | https://github.com/libvips/libvips/v8.18.7 | libvips/foreign/libnsgif/COPYING |
| libpng | 1.6.58 | https://github.com/pnggroup/libpng/v1.6.58 | LICENSE |
| librsvg | 2.63.2 | https://github.com/GNOME/librsvg/2.63.2 | COPYING.LIB |
| libtiff | 4.7.2 | https://github.com/libsdl-org/libtiff/v4.7.2 | LICENSE.md |
| libultrahdr | 2.0.2 | https://github.com/google/libultrahdr/v2.0.2 | LICENSE, LICENSE-APACHE, LICENSE-MIT |
| libvips | 8.18.7 | https://github.com/libvips/libvips/v8.18.7 | LICENSE |
| libwebp | 1.6.0 | https://github.com/webmproject/libwebp/v1.6.0 | COPYING |
| libxml2 | 2.15.4 | https://github.com/gnome/libxml2/v2.15.4 | Copyright |
| mozjpeg | 0826579 | https://github.com/mozilla/mozjpeg/08265790774cd0714832c9e675522acbe5581437 | LICENSE.md |
| pango | 1.58.2 | https://github.com/GNOME/pango/1.58.2 | COPYING |
| pixman | 0.46.4 | https://github.com/sailfishos-mirror/pixman/pixman-0.46.4 | COPYING |
| proxy-libintl | 0.5 | https://github.com/fridc/proxy-libintl/0.5 | COPYING |
| zlib-ng | 2.3.3 | https://github.com/zlib-ng/zlib-ng/2.3.3 | LICENSE.md |

Exact full original texts (including some multi-license alternatives)
are in `components/`; do not replace them with simplified SPDX labels.
Mirrors are used where the canonical upstream GitLab/Google-hosted
repository is not accessible through the code archival integration.
The version-tagged sources should be checked against the official
upstream distribution and any version-specific additions before
unconditional public reuse.

## LGPL / source and user modification

A dynamic libvips build with 38 native DLLs was tested against
NetVips 3.2.0 on Windows x64. The package currently being *released*
from `main` uses the **static native NuGet distribution**, not this
dynamically linked alternative. Do not mix their manifests or claim
that the static bundle's LGPL issue is closed by merely including
these texts.

Full corresponding sources, any local modifications, the specific
build configuration, and the ability to replace every LGPL library
remain obligations that cannot be proven by a text-only inventory.
The pinned dynamic upstream package is
https://github.com/libvips/build-win64-mxe/releases/tag/v8.18.7
(`vips-dev-x64-web-8.18.7.zip`;
SHA256 `3a122eb3d588690008216f786338e2f9329dba3c3c2768b5933cf7299d549241`).
The corresponding build sources are at the same tag.
This document is not a source-code delivery nor a legal clearance.

## Separate components outside this native bundle

ExifTool 13.59 ships its upstream
`exiftool_files/LICENSE` and
`exiftool_files/Licenses_Strawberry_Perl.zip` unchanged in the PCasa
distribution. CEF/Chromium's internally generated credits and exact
NuGet notices are handled by the existing distribution process.
Do not duplicate the Windows Perl runtime or infer its full list
of third parties from this native libvips table.
