# PCasa third-party licensing audit (working review)

Status: **NOT APPROVED FOR GPL-3.0-ONLY RELEASE YET**.
This document records evidence, not legal advice or a claim of complete compliance.
Do not add an unqualified "all dependencies cleared" statement to About,
the installer, or a release until the gates below have passed.

## First-party licensing decision

- Proposed: GPL-3.0-only; copyright (c) 2026 Jens-Jürgen Schäfer.
- Add the full GPL 3 license and corresponding-source distribution instructions
  only after binary/third-party review.
- Build source must match each GitHub release tag and installer exactly.

## Direct components (from Directory.Packages.props)

| Component | Pinned version | Declared license / review |
| --- | --- | --- |
| Microsoft.Data.Sqlite | 10.0.12 | MIT; verify transitive SQLitePCLRaw and native SQLite notices |
| CefSharp.Wpf.NETCore | 152.0.100 | BSD family; verify CEF/Chromium included third-party notices and source attribution |
| NetVips | 3.2.0 | MIT; verify transitive dependencies |
| NetVips.Native.win-x64 | 8.18.7 | NuGet declares LGPL-3.0-or-later; verify all native DLLs, notices, linkage and replacement rights |
| MapLibre GL JS | 5.9.0 | BSD-3-Clause and bundled upstream third-party attributions; verify exact version |
| ExifTool | 13.59 | Separately downloaded Windows executable in published package; verify license and the bundled Perl runtime's terms |
| .NET 10 self-contained | runner SDK/runtime | Inspect redistributable Microsoft runtime components and associated notices |
| Inno Setup | runner Chocolatey version | Build-time installer compiler; record version and terms of generated setup EXE |

Other assets: original JPG canvas textures, Python-generated backgrounds, 
NuGet native dependencies, MapLibre style/tile attribution, any bundled runtime resources.

## Release gate -- all must be true

- [ ] Generate the full **publish/PCasa** file manifest (path, SHA256, originating package, package version, license).
- [ ] Enumerate all **transitive NuGet packages** and native CEF/libvips/SQLite/Perl components.
- [ ] Check each actual artifact's license and copyright notice (not just top-level package metadata).
- [ ] Include all required license texts and third-party notices in **both installer and portable ZIP**.
- [ ] For LGPL libraries: verify relinking/replacement rights, license/source availability and any applicable installation information; test this against the actual Windows distribution.
- [ ] For ExifTool and bundled Perl: confirm license and corresponding-source/notice requirements against the packaged executable.
- [ ] For Chromium/CEF: include the applicable license and third-party attribution manifest shipped with the exact engine.
- [ ] Confirm permission to distribute all original/third-party graphic assets.
- [ ] Publish PCasa's GPL-3.0-only LICENSE and the complete corresponding source and build scripts for every GPL binary release.
- [ ] Confirm that GitHub Release asset and source tag are immutable, version-matched and reproducible by CI.
- [ ] Have a final human legal/compliance review before calling the distribution cleared.

## Evidence consulted (2026-11-08)

- https://www.nuget.org/packages/NetVips.Native.win-x64/8.18.7
- https://www.nuget.org/packages/NetVips/3.2.0
- https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.12
- https://github.com/cefsharp/CefSharp
- https://github.com/kleisauke/libvips-packaging
- https://github.com/exiftool/exiftool
- https://www.gnu.org/licenses/gpl-faq.html

Important: Third-party packages retain their own licenses, even when PCasa
first-party source is released under GPL-3.0-only. Never relabel a third-party
binary as GPL merely because it ships with PCasa.
