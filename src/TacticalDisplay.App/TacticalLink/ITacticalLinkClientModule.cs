using TacticalDisplay.Core.Models;

namespace TacticalDisplay.App.TacticalLink;

/// <summary>A statically registered consumer of a TacticalLink module event stream.</summary>
internal interface ITacticalLinkClientModule
{
    string Module { get; }
    int ProtocolVersion { get; }
    void HandleEvent(TacticalLinkModuleEvent message);
}
