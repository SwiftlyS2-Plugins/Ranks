using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace Ranks.Players;

/// <summary>Every chat line core sends, prefixed.</summary>
/// <remarks>
/// A helper rather than baking the prefix into each translation value: a translator editing one
/// string would otherwise drop it silently, and the stats block would repeat it per line.
/// </remarks>
internal static class Chat
{
    public static void Send(ISwiftlyCore core, IPlayer player, string key, params object[] args) =>
        player.SendChat(core.Localizer["prefix"] + core.Localizer[key, args]);

    /// <summary>Sends an already-resolved line, still prefixed.</summary>
    public static void SendRaw(ISwiftlyCore core, IPlayer player, string message) =>
        player.SendChat(core.Localizer["prefix"] + message);
}
