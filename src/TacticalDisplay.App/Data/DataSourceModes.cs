namespace TacticalDisplay.App.Data;

public static class DataSourceModes
{
    public const string Demo = "Demo";
    public const string Msfs = "MSFS";
    public const string XPlane = "XPlane";

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
            "XPUIPC" => XPlane,
            "XPlane" => XPlane,
            _ => Demo
        };
    }

    public static bool UsesSimulatorConnection(string? value) =>
        !string.Equals(Normalize(value), Demo, StringComparison.OrdinalIgnoreCase);

    public static bool IsMsfs(string? value) =>
        string.Equals(Normalize(value), Msfs, StringComparison.OrdinalIgnoreCase);

    public static bool IsXPlane(string? value) =>
        string.Equals(Normalize(value), XPlane, StringComparison.OrdinalIgnoreCase);
}
