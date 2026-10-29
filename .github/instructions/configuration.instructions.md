---
description: 'Plugin configuration: binding, hot reload via ChangeToken, defaults, clamping, and the jsonc files. Trigger phrases: config, configuration, jsonc, hot reload, settings, binder, defaults.'
applyTo: '**/Config/*.cs, **/*.jsonc'
---

# Configuration

> Part of the Ranks instruction set. `§N` cross-references resolve through the table in [`copilot-instructions.md`](../copilot-instructions.md), which is always loaded; `docs/` references are to the separate requirements and implementation guides.

### Config Model

```csharp
namespace Ranks;

public class Config
{
    public DatabaseSettings   Database   { get; set; } = new();
    public RankSettings       Rank       { get; set; } = new();
    public ScoreboardSettings Scoreboard { get; set; } = new();
    public PointSettings      Points     { get; set; } = new();
    public VipSettings        Vip        { get; set; } = new();
    public GeneralSettings    General    { get; set; } = new();
}

public class GeneralSettings
{
    public bool DetailedLogging { get; set; } = false;   // gates Information + Debug, §4
    // …
}
```

Every setting needs a sensible default — the file is generated from the model,
so the defaults *are* the shipped config.

### Loading Configuration (canonical pattern)

**Bind `IOptionsMonitor<T>` and read through `.CurrentValue`. Never store
`IOptions<T>.Value`.** A `.Value` snapshot is frozen at `Load`, so the plugin
keeps serving stale settings after every reload — and it is silent when wrong.

```csharp
private void LoadConfiguration()
{
    const string fileName = "config.jsonc";
    const string section = "Ranks";

    Core.Configuration
        .InitializeJsonWithModel<Config>(fileName, section)   // creates only if absent
        .Configure(builder => builder.AddJsonFile(
            Core.Configuration.GetConfigPath(fileName),
            optional: false,
            reloadOnChange: true));

    ServiceCollection services = new();
    services.AddSwiftly(Core, addLogger: true, addConfiguration: true)
        .AddOptionsWithValidateOnStart<Config>()
        .BindConfiguration(section);

    // A local BuildServiceProvider() is fine here — this is not the ASP.NET
    // "don't build a second container" case.
    var provider = services.BuildServiceProvider();
    logger = provider.GetRequiredService<ILogger<Ranks>>();
    cfg = provider.GetRequiredService<IOptionsMonitor<Config>>();

    cfg.OnChange(_ => logger.LogWarning("Ranks: {File} reloaded.", fileName));
}
```

Reload notices are `LogWarning` and are **never** gated behind
`DetailedLogging` — an operator editing a live config needs the confirmation.

### Does your config actually reload? Three propagation idioms

Binding the monitor is only half of it. Whether a *read site* sees the new value
depends on how it holds the config:

| Idiom | Reloads? |
|---|---|
| Monitor read per access — `cfg.CurrentValue.Rank.MinPlayers` | yes |
| Lazy delegate — `Func<Config> get = () => cfg.CurrentValue;`, called per read | yes |
| **Swapped snapshot** — `private volatile Config _snapshot;` reassigned from `OnChange` | **yes, and it is the fastest** |
| **Constructor snapshot** — `private readonly Config _cfg;`, or extracted primitives like `_minPlayers = cfg.MinPlayers;` | **no — frozen at Load** |

The last one hides well: the plugin reloads, the log line prints, and the
service keeps using the old value.

**Rows three and four look alike and are opposites.** A field captured once and
never refreshed is frozen; a field **reassigned by `OnChange`** is current *and*
avoids a `ConcurrentDictionary` lookup per read, which matters on the award
path. Prefer it there, and read the reference once at the top of an operation
so a mid-award reload cannot mix old and new values. Implementation guide §3.2
has the full rule. `IOptions<T>.Value` is the one that is always wrong — no
change notification at all.

**Two things never reload, by design** — say so in the reload log rather than
pretending otherwise:

- `Database.Connection`, `Database.TablePrefix` and `Database.TableSuffix`.
  Changing them mid-session would point a live writer at a different table set.
- Event hooks and command bindings decided once at `Load` from a config toggle.

### Never write the watched file from a reload callback

`OnChange` fires on file change. Writing the file or calling
`IConfigurationRoot.Reload()` from inside the callback re-raises the token and
loops — and a re-entrancy flag does **not** suppress it. Seed, migrate and
sanitize on explicit load paths only.

This is one reason **core writes no config file at all** — every file under
`configs/` is hand-authored and only ever read. See the requirements doc,
§13 Configuration summary.

### ⚠️ One save notifies every bound config

All config files go into one `IConfigurationBuilder`, so they share one
`IConfigurationRoot` and one reload token. `BindConfiguration("Section")` binds
to that root, not to the file the section came from — so saving **any** watched
file raises `OnChange` on **every** `IOptionsMonitor<T>`.

NFR-08 requires the opposite: saving `points.jsonc` must re-apply only
`points.jsonc`. And one save fires `OnChange` two or three times anyway, because
editors truncate then write.

Both are fixed by comparing before applying — fingerprint the **bound model**
(not the file bytes, so reformats and comment edits are correctly ignored),
no-op when unchanged, and put every side effect after the guard. Full shape in
the implementation guide, §3.1.

### Multiple Config Files

`Ranks` splits config across `config.jsonc`, `points.jsonc`, `ranks.jsonc` and
`commands.jsonc`. Use one reusable builder that returns the monitor:

```csharp
private IOptionsMonitor<T> BuildConfigService<T>(string fileName, string sectionName)
    where T : class, new()
{
    Core.Configuration
        .InitializeJsonWithModel<T>(fileName, sectionName)
        .Configure(c => c.AddJsonFile(
            Core.Configuration.GetConfigPath(fileName),
            optional: false,
            reloadOnChange: true));

    ServiceCollection services = new();
    services.AddSwiftly(Core).AddOptions<T>().BindConfiguration(sectionName);
    return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<T>>();
}
```

### Other initializers

```csharp
Core.Configuration.InitializeWithTemplate("config.jsonc", "config.template.jsonc"); // packaged templates/
Core.Configuration.InitializeTomlWithModel<SomeConfig>("file.toml", "Section");
```

`Core.Configuration.BasePath` and `.BasePathExists` expose the config directory.
All setup methods return the service, so they chain.

---

