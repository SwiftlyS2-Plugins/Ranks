using Microsoft.Extensions.Logging;
using Ranks.Contract;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Players;

namespace Ranks.Menus;

/// <summary>The module-contributed rows of core's menu (FR-CMD-04).</summary>
/// <remarks>
/// Core holds a delegate and a label, both declared in <c>Ranks.Contract</c>, so it still names no
/// module-defined type and references no module assembly. What it does take on is lifetime: a
/// module that unloads without calling <c>UnregisterMenuEntries</c> leaves a delegate pointing into
/// a collectible assembly load context, which is why <c>RanksModuleBase.Unregister</c> drops them
/// unconditionally rather than leaving it to an override.
/// </remarks>
internal sealed class MenuEntryRegistry
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<Registration> _entries = [];

    public MenuEntryRegistry(ILogger logger) => _logger = logger;

    private readonly record struct Registration(string ModuleId, RanksMenuEntry Entry);

    public void Add(string moduleId, RanksMenuEntry entry)
    {
        if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(entry.Section) || string.IsNullOrWhiteSpace(entry.Label))
        {
            _logger.LogWarning("Ranks: ignoring a menu entry with no module id, section or label.");
            return;
        }

        lock (_gate)
        {
            // Replace rather than append: the handshake re-runs on every load and config reload,
            // so an append would show the same row once per reload until the map changed.
            _entries.RemoveAll(r =>
                string.Equals(r.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Entry.Section, entry.Section, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Entry.Label, entry.Label, StringComparison.Ordinal));

            _entries.Add(new Registration(moduleId, entry));
        }
    }

    public void RemoveAll(string moduleId)
    {
        lock (_gate)
            _entries.RemoveAll(r => string.Equals(r.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Entries for one section, ordered, with the owning module id for error reporting.</summary>
    public IReadOnlyList<(string ModuleId, RanksMenuEntry Entry)> For(string section)
    {
        lock (_gate)
            return _entries
                .Where(r => string.Equals(r.Entry.Section, section, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Entry.Order)
                .ThenBy(r => r.Entry.Label, StringComparer.CurrentCulture)
                .Select(r => (r.ModuleId, r.Entry))
                .ToList();
    }

    /// <summary>Builds one entry's submenu, turning a module's failure into a greyed-out row.</summary>
    public IMenu? Build(string moduleId, RanksMenuEntry entry, IPlayer player)
    {
        try
        {
            return entry.Build(player);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: module '{ModuleId}' threw building its '{Label}' menu.", moduleId, entry.Label);
            return null;
        }
    }
}
