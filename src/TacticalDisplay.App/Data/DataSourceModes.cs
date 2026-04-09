namespace TacticalDisplay.App.Data;

public static class DataSourceModes
{
    public const string Demo = "Demo";
    public const string Msfs = "MSFS";
    public const string XPlane12 = "XPlane 12";
    public const string XPlaneLegacy = "Xplane Legacy (XPUIPC)";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Demo;
        }

        return value.Trim() switch
        {
            "Demo" => Demo,
            "SimConnect" => Msfs,
            "MSFS" => Msfs,
            "XPlane12" => XPlane12,
            "XPlane 12" => XPlane12,
            "XPlaneWebApi" => XPlane12,
            "XPUIPC" => XPlaneLegacy,
            "XPlane" => XPlaneLegacy,
            "Xplane Legacy (XPUIPC)" => XPlaneLegacy,
            "Xplane Legacy (<1)" => XPlaneLegacy,
            "XPlane (XPUIPC)" => XPlaneLegacy,
            _ => Demo
        };
    }

    public static bool UsesSimulatorConnection(string? value) =>
        !string.Equals(Normalize(value), Demo, StringComparison.OrdinalIgnoreCase);

    public static bool IsMsfs(string? value) =>
        string.Equals(Normalize(value), Msfs, StringComparison.OrdinalIgnoreCase);

    public static bool IsXPlane12(string? value) =>
        string.Equals(Normalize(value), XPlane12, StringComparison.OrdinalIgnoreCase);

    public static bool IsXPlaneLegacy(string? value) =>
        string.Equals(Normalize(value), XPlaneLegacy, StringComparison.OrdinalIgnoreCase);

    public static bool IsAnyXPlane(string? value) =>
        IsXPlane12(value) || IsXPlaneLegacy(value);
}
