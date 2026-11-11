namespace FactoryApp.Domain;

public static class FailureCodes
{
    public const string Leak = "LEAK";
    public const string Overpressure = "OVERPRESSURE";
    public const string SensorFault = "SENSOR_FAULT";
    public const string Timeout = "TIMEOUT";
    public const string Other = "OTHER";

    public static IReadOnlyList<string> All { get; } = [Leak, Overpressure, SensorFault, Timeout, Other];

    public static bool IsValid(string? code) =>
        !string.IsNullOrEmpty(code) && All.Contains(code, StringComparer.Ordinal);
}
