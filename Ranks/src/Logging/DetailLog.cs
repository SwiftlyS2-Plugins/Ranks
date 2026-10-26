using Microsoft.Extensions.Logging;
using Ranks.Config;

namespace Ranks.Logging;

/// <summary>Narrates expected flow, only when the operator asked for it.</summary>
/// <remarks>
/// The gate reads the live snapshot rather than a value captured at construction, so flipping
/// <c>General.DetailedLogging</c> takes effect on the next call instead of the next restart.
/// Warnings and errors never route through here — they are never gated.
/// </remarks>
internal sealed class DetailLog(ILogger logger, ConfigHost<RanksConfig> config)
{
    public void Write(string template, params object?[] args)
    {
        if (!config.Current.General.DetailedLogging) return;

        logger.LogInformation(template, args);
    }
}
