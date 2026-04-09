namespace TacticalDisplay.App.Controls;

public sealed class ScopeLabelMovedEventArgs : EventArgs
{
    public ScopeLabelMovedEventArgs(string targetId, double offsetX, double offsetY)
    {
        TargetId = targetId;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public string TargetId { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
}
