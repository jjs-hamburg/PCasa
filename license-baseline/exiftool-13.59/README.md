# ExifTool 13.59 / Strawberry Perl — fixed original rights material

PCasa uses the **unmodified ExifTool 13.59 Windows x64 bundle** from
https://exiftool.org/ and its packaged portable Strawberry Perl.
This directory freezes the upstream 13.59 `LICENSE` and `README`
from https://github.com/exiftool/exiftool/tree/13.59.

The complete **original Strawberry Perl license archive** is *already
distributed* by PCasa beside ExifTool, at:
`exiftool/exiftool_files/Licenses_Strawberry_Perl.zip`.
The original ExifTool package's `exiftool_files/LICENSE` is also
distributed unchanged. Neither should be replaced by an abbreviated summary.

## Frozen verified binary and archive evidence

- Source Windows release: `exiftool-13.59_64.zip`, SHA-256
  `44b512b25af500724ba579d0a53c8fc5851628b692dd5e5d94ae4a15c2cba9ec`
  (verified by `.github/scripts/install-exiftool.ps1`).
- License ZIP inside actual Windows payload:
  `Licenses_Strawberry_Perl.zip`, 481025 bytes, SHA-256
  `8bd5fc8dc0ef3b96881ddad0974b79e86f4043f76ab318a730b084adf0890bd0`.
- Original license ZIP inventory: 153 non-directory files,
  including 63 entries matching license, COPYING, NOTICE or Artistic.
- That archive includes `perl/Artistic` and `perl/Copying`
  as well as the notices for GCC toolchain, FreeType, fontconfig,
  harfbuzz, fribidi, libwebp, libtiff, libxml2, OpenSSL and
  other Strawberry Perl components.
- The exact archive entries, sizes and per-member SHA-256 are
  captured as `strawberry-perl-archive-files.csv` in the GitHub
  `Third-party license evidence` workflow artifact.
- Official corresponding ExifTool 13.59 source:
  https://github.com/exiftool/exiftool/tree/13.59

The zip itself is held unchanged as part of the ExifTool binary payload,
not duplicated in Git history. CI must refuse any unexpected ZIP
replacement rather than silently using an unrelated Strawberry Perl
version. The bundled license archive is not proof that all toolchain
components are incorporated into the running executable; don't infer
redistributed binary components from the archive directory alone.

The exact ExifTool/Perl license clauses and the matching *distributed*
binary modules still determine the compliance obligations. This folder
preserves evidence and documents what has actually been verified.
