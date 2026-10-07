using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.Data;

public interface IAarPoseSource
{
    bool AarSamplingEnabled { get; set; }
    event EventHandler<OwnshipState>? AarPoseSampled;
}
