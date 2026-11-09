"""Inspect the exact released libvips Windows variants, never infer linkage from filenames."""
import csv
import pathlib
import sys
import pefile

root = pathlib.Path(sys.argv[1])
rows = []
summary = ["# libvips 8.18.7 Windows x64 PE evidence", "",
           "Variants obtained from upstream version-pinned release assets and SHA-256 verified by workflow.",
           "PE import table inspection reveals dynamic dependencies only; it does not independently establish every static archive linked into the image.",
           ""]
for variant in ("static", "dynamic"):
    folder = root / variant
    dlls = sorted(folder.rglob("*.dll"))
    if not dlls:
        raise SystemExit(f"Missing DLLs for {variant}")
    vips = [p for p in dlls if p.name.lower() == "libvips-42.dll"]
    if len(vips) != 1:
        raise SystemExit(f"Expected one libvips-42.dll for {variant}; found {len(vips)}")
    summary += [f"## {variant}: {len(dlls)} DLLs", ""]
    for dll in dlls:
        pe = pefile.PE(str(dll), fast_load=True)
        try:
            pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                                                   pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"]])
            imports = set()
            for entry in getattr(pe, "DIRECTORY_ENTRY_IMPORT", []):
                imports.add(entry.dll.decode("utf-8", errors="replace").lower())
            for entry in getattr(pe, "DIRECTORY_ENTRY_DELAY_IMPORT", []):
                imports.add(entry.dll.decode("utf-8", errors="replace").lower())
            relative = dll.relative_to(folder).as_posix()
            rows.append({"variant": variant, "dll": relative, "size": dll.stat().st_size,
                         "imports": ";".join(sorted(imports))})
            if dll == vips[0]:
                summary += [f"libvips-42.dll: {dll.stat().st_size} bytes",
                            "Direct PE imports: " + (", ".join(sorted(imports)) or "(none)"), ""]
        finally:
            pe.close()
    summary += ["Distributed DLL names: " + ", ".join(sorted({p.name for p in dlls})), ""]
with (root / "comparison.csv").open("w", newline="", encoding="utf-8") as f:
    writer = csv.DictWriter(f, fieldnames=["variant", "dll", "size", "imports"])
    writer.writeheader()
    writer.writerows(rows)
(root / "summary.md").write_text("\n".join(summary), encoding="utf-8")
