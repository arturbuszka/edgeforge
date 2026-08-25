using EdgeForge.Decision;
using Microsoft.AspNetCore.SignalR;

namespace EdgeForge.Api;

/// <summary>
/// What the server can call on a connected browser.
/// </summary>
/// <remarks>
/// A typed hub rather than the stringly-typed one, so the method name cannot
/// drift apart from the front-end's <c>connection.on(...)</c> without the
/// compiler noticing on this side at least.
/// </remarks>
public interface ILiveClient
{
    Task StatsUpdated(StatsSnapshot stats);
}

/// <summary>
/// Push-only live stats. The browser connects and listens; it never invokes
/// anything.
/// </summary>
/// <remarks>
/// No authentication, no groups, no per-connection state. This is a household
/// HUD on a private network, and the same rule that governs the rest of the API
/// applies here: counts and timestamps leave, never frames.
/// </remarks>
public sealed class LiveHub(PipelineHost pipeline) : Hub<ILiveClient>
{
    private readonly PipelineHost _pipeline =
        pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    /// <summary>
    /// Sends the current snapshot immediately on connect.
    /// </summary>
    /// <remarks>
    /// Without this a freshly loaded page shows blanks until the next push, which
    /// at the default interval is up to a second of empty HUD on every reload.
    /// The client seeds from /api/stats too; this closes the gap between that
    /// fetch and the first push.
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        await Clients.Caller.StatsUpdated(_pipeline.Current);
    }
}
