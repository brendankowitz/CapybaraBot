using Microsoft.AspNetCore.SignalR;

namespace Fleet.Gateway.Realtime;

/// <summary>SignalR hub for live fleet state used by the PWA and TUI (FR-CLI-02, FR-CLI-05).</summary>
public sealed class FleetHub : Hub;
