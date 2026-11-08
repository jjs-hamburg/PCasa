using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Single authority for the durable catalog, scheduler journal and thumbnail
/// caches. Portable/legacy and per-user installs retain the existing location.
/// A machine-wide installer writes a marker beside the application executable;
/// both standard and elevated users then use the same ProgramData catalog.
/// Personal UI preferences remain in LocalAppData and are never shared.
/// </summary>
public static class PcasaDataPaths
{
    public static bool IsAllUsersInstallation =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "PCasa.all-users"));

    public static string SharedDataRoot => Path.Combine(
        Environment.GetFolderPath(IsAllUsersInstallation
            ? Environment.SpecialFolder.CommonApplicationData
            : Environment.SpecialFolder.LocalApplicationData), "PCasa");
}
