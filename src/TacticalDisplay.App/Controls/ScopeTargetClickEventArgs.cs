using System.Windows.Input;

namespace TacticalDisplay.App.Controls;

public sealed class ScopeTargetClickEventArgs : EventArgs
{
    public ScopeTargetClickEventArgs(string targetId, MouseButton button)
    {
        TargetId = targetId;
        Button = button;
    }

    public string TargetId { get; }
    public MouseButton Button { get; }
}
