namespace TacticalDisplay.Core.Models;

public enum ManualAffiliation
{
    Neutral,
    Friendly,
    Enemy
}

public sealed class ManualTargetMetadata
{
    public string Id { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public ManualAffiliation Affiliation { get; set; } = ManualAffiliation.Neutral;
}
