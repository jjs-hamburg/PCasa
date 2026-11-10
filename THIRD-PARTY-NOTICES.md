# PCasa — third-party notices (Windows x64)

PCasa's original source code is Copyright (c) 2026 Jens-Jürgen Schäfer
and is licensed under GNU GPL version 3 (see `Licenses/PCasa-GPL-3.0.txt`).

This notice is an identification guide, **not** a replacement for the license
texts and attribution files required by individual redistributed components.
Third-party components retain their own licenses; the PCasa GPL does not
relicense those components.

| Component | Distribution / known license | Upstream |
| --- | --- | --- |
| Microsoft.Data.Sqlite 10.0.12 | MIT; includes transitive SQLitePCLRaw/native SQLite dependencies | https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12 |
| CefSharp.Wpf.NETCore 152.0.100 | BSD-family; CEF and Chromium third-party notices apply | https://github.com/cefsharp/CefSharp |
| NetVips 3.2.0 | MIT | https://github.com/kleisauke/net-vips |
| NetVips.Native.win-x64 8.18.7 | LGPL-3.0-or-later per NuGet; bundled native dependencies may carry additional licenses | https://www.nuget.org/packages/NetVips.Native.win-x64/8.18.7 |
| MapLibre GL JS 5.9.0 | BSD-3-Clause; detailed attributions in `Licenses/MapLibre-LICENSE.txt` | https://github.com/maplibre/maplibre-gl-js |
| ExifTool 13.59 | ExifTool's GPL/Artistic dual-licensing; Windows binary includes Perl and its additional third-party terms | https://exiftool.org/ |
| .NET 10 Desktop Runtime | Installed independently via Microsoft's official runtime installer, not redistributed in PCasa | https://dotnet.microsoft.com/download/dotnet/10.0 |

The installed directory includes `Licenses/CEF-LIBVIPS-GUIDE.txt` with the
exact Windows component versions, references to Chromium internal credits,
LGPL source-build locations, and practical shared-library replacement steps.
The original, version-locked libvips 8.18.7 licensing texts are now
included offline at `Licenses/Native/libvips-8.18.7/`. They include the
upstream dependency list, 29 named upstream components (including embedded
libnsgif), exact-version license/COPYING/NOTICE material, AOM patent terms,
and LLVM libc++/libunwind runtime license references. The source files
are committed to `license-baseline/libvips-8.18.7/` and checked against
the installed copies by SHA-256. Original rights documents, not only
license identifiers, are preserved.

The native libvips package's THIRD-PARTY-NOTICES.md names additional LGPL
and permissively licensed components; a notice list alone is not a substitute
for corresponding source and replacement/relinking obligations. The precise
LLVM toolchain revision for the dynamic DLL set remains to be verified.

Source and compliance information for LGPL components:

- libvips source code for the pinned 8.18.7 release:
  https://github.com/libvips/libvips/releases/tag/v8.18.7
- Windows binary packaging and build recipes for the same version:
  https://github.com/kleisauke/libvips-packaging/releases/tag/v8.18.7
- The installed native libraries reside next to PCasa in the published
  application directory; PCasa invokes native libvips through NetVips.
  End users must be allowed to replace LGPL libraries with a modified,
  ABI-compatible version. Packaging and signed-binary restrictions must
  not inhibit this.
- The package's upstream THIRD-PARTY-NOTICES.md, including dependent
  codec and native library licensing information, is copied to
  `Licenses/Packages/NetVips.Native.win-x64-8.18.7/`.

CEF/Chromium license files are copied from their exact restored NuGet
packages into `Licenses/Packages/`. Chromium additional credits are
available in the embedded browser at `about:credits`, with CEF
license guidance at https://chromiumembedded.github.io/cef/general_usage.html.

The full Apache-2.0 license text and copyright-bearing MIT texts for
Microsoft.Data.Sqlite and NetVips are included in `Licenses/` as
`Apache-2.0.txt`, `MIT-Microsoft.txt`, and `MIT-NetVips.txt`.
The complete LGPL-3.0 text is in `Licenses/LGPL-3.0.txt`.
The `Licenses/NuGet-license-inventory.csv` file records resolved NuGet
package versions, declared licenses and package copyright metadata.

The ExifTool 13.59 original license and upstream README are additionally
shipped at `Licenses/Native/exiftool-13.59/`. The original bundle still
carries `exiftool/exiftool_files/LICENSE` and the complete
`exiftool/exiftool_files/Licenses_Strawberry_Perl.zip` (153 files, including
63 license/notice-name entries), pinned to its audited SHA-256.
These files must remain present when distributing the executable.

**Known outstanding release compliance work:** For the exact published files,
verify and include the complete Chromium/CEF third-party notices and libvips
native codec/dependency licenses. Verify the LGPL source, relinking/replacement
conditions, ExifTool/Perl bundle conditions, and all transitive NuGet notices.
Do not describe a distribution as fully cleared until the checks in
`docs/third-party-license-audit.md` are completed.

This document intentionally does not assert legal clearance.
