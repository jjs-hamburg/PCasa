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
available in the embedded browser at `chrome://credits`, with CEF
license guidance at https://chromiumembedded.github.io/cef/general_usage.html.

The ExifTool bundle carries its upstream `exiftool/exiftool_files/LICENSE`
and `exiftool/exiftool_files/Licenses_Strawberry_Perl.zip`.
These files must remain present when distributing the executable.

**Known outstanding release compliance work:** For the exact published files,
verify and include the complete Chromium/CEF third-party notices and libvips
native codec/dependency licenses. Verify the LGPL source, relinking/replacement
conditions, ExifTool/Perl bundle conditions, and all transitive NuGet notices.
Do not describe a distribution as fully cleared until the checks in
`docs/third-party-license-audit.md` are completed.

This document intentionally does not assert legal clearance.
