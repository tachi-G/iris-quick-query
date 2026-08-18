using Microsoft.Win32;

namespace IrisQuickQuery.Infrastructure.Database;

public sealed record OdbcDriverInfo(string Name, bool Is64Bit);
public sealed record OdbcDsnInfo(string Name, string Driver, bool Is64Bit, bool IsSystem);
public sealed record OdbcEnvironmentReport(IReadOnlyList<OdbcDriverInfo> Drivers, IReadOnlyList<OdbcDsnInfo> DataSources)
{
    public bool Has64BitIrisDriver => Drivers.Any(x => x.Is64Bit && x.Name.Contains("IRIS ODBC", StringComparison.OrdinalIgnoreCase));
}

public static class OdbcEnvironmentInspector
{
    public static OdbcEnvironmentReport Inspect()
    {
        var drivers = new List<OdbcDriverInfo>();
        var sources = new List<OdbcDsnInfo>();
        ReadDrivers(RegistryView.Registry64, true, drivers);
        ReadDrivers(RegistryView.Registry32, false, drivers);
        ReadDsns(RegistryHive.LocalMachine, RegistryView.Registry64, true, true, sources);
        ReadDsns(RegistryHive.CurrentUser, RegistryView.Registry64, true, false, sources);
        ReadDsns(RegistryHive.LocalMachine, RegistryView.Registry32, false, true, sources);
        ReadDsns(RegistryHive.CurrentUser, RegistryView.Registry32, false, false, sources);
        return new OdbcEnvironmentReport(drivers.Distinct().OrderBy(x => x.Name).ToArray(), sources.Distinct().OrderBy(x => x.Name).ToArray());
    }

    private static void ReadDrivers(RegistryView view, bool is64, ICollection<OdbcDriverInfo> result)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
        if (key is null) return;
        foreach (var name in key.GetValueNames().Where(x => string.Equals(key.GetValue(x)?.ToString(), "Installed", StringComparison.OrdinalIgnoreCase)))
            result.Add(new OdbcDriverInfo(name, is64));
    }

    private static void ReadDsns(RegistryHive hive, RegistryView view, bool is64, bool isSystem, ICollection<OdbcDsnInfo> result)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
        if (key is null) return;
        foreach (var name in key.GetValueNames())
            result.Add(new OdbcDsnInfo(name, key.GetValue(name)?.ToString() ?? string.Empty, is64, isSystem));
    }
}
