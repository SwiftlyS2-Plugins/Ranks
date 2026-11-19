using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ranks.Api;
using Ranks.Config;
using Ranks.Contract;
using Ranks.Database;
using Ranks.Logging;
using Ranks.Ranking;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;

namespace Ranks.Players;

/// <summary>Owns when a record is loaded, when it is written, and when bulk work runs.</summary>
internal sealed class PlayerSessions
{
    /// <remarks>10s total, which covers a slow Steam handshake without holding a slot forever.</remarks>
    private const int AuthPollAttempts = 100;
    private const int AuthPollIntervalMs = 100;

    private readonly ISwiftlyCore _core;
    private readonly ConfigHost<RanksConfig> _config;
    private readonly PlayerStore _store;
    private readonly PlayerRegistry _registry;
    private readonly RankLadder _ladder;
    private readonly RanksEventHub _events;
    private readonly DetailLog _detail;
    private readonly ILogger _logger;

    private readonly Lock _saveGate = new();

    /// <summary>The last player-row write queued; the next one runs after it.</summary>
    private Task _saveTail = Task.CompletedTask;

    /// <summary>The batch pass a concurrent <see cref="SaveAllAsync"/> joins until it completes.</summary>
    private Task<int>? _pendingPass;

    /// <remarks>Both triggers reach Track before either load publishes; without this the row is
    /// read twice and PlayerDataLoaded is raised twice.</remarks>
    private readonly ConcurrentDictionary<ulong, byte> _loading = new();

    /// <remarks>In memory by design: a pass always runs at cold start, so a persisted timestamp
    /// could only ever suppress a run that is already the default behaviour.</remarks>
    private DateTime _lastMaintenance = DateTime.MinValue;

    /// <remarks>Written only on the game thread, by initialization and rollover; read from anywhere.</remarks>
    private long _activePeriod;

    private CancellationTokenSource? _saveTimer;

    public PlayerSessions(
        ISwiftlyCore core,
        ConfigHost<RanksConfig> config,
        PlayerStore store,
        PlayerRegistry registry,
        RankLadder ladder,
        RanksEventHub events,
        DetailLog detail,
        ILogger logger)
    {
        _core = core;
        _config = config;
        _store = store;
        _registry = registry;
        _ladder = ladder;
        _events = events;
        _detail = detail;
        _logger = logger;
    }

    /// <summary>True once the schema probe has run; false disables every write.</summary>
    public bool SchemaUsable { get; set; } = true;

    /// <summary>The period every resident record is in, and every new record starts in (FR-DB-10).</summary>
    public long ActivePeriod => Volatile.Read(ref _activePeriod);

    public bool PeriodsEnabled => CurrentGrid.IsEnabled;

    public DateTime? ActivePeriodEndsAt =>
        CurrentGrid is { IsEnabled: true } grid ? PeriodClock.EndOf(ActivePeriod, grid) : null;

    private PeriodGrid CurrentGrid => PeriodGrid.Of(_config.Current.Database);

    /// <summary>Settles the period before anyone is tracked. False when it cannot be known.</summary>
    /// <remarks>
    /// Resumes the newest period the data holds until it ends — with periods off, so switching them
    /// off never starts a period, and after <c>VirtualPurgeMode</c> or <c>VirtualPurgeDays</c>
    /// changed, so a period id never goes backwards and a running season is not cut short. A failure must not
    /// guess: every row on a later period than the guess would be closed and reset on its owner's
    /// next load, which is a virtual purge nobody asked for.
    /// </remarks>
    public bool TryInitializePeriod()
    {
        var grid = CurrentGrid;

        try
        {
            var latest = _store.LatestPeriod();
            var period = grid.IsEnabled ? PeriodClock.Due(latest, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), grid) : latest;

            Volatile.Write(ref _activePeriod, period);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: could not read the current period; nothing will be recorded until the next restart.");
            return false;
        }
    }

    /// <summary>Moves every resident record into the due period once a boundary has passed.</summary>
    /// <remarks>
    /// Game thread only — it rewrites records gameplay mutates. Runs at round end and on the save
    /// timer, so a server whose rounds never end still turns over within one save interval. With
    /// periods off nothing is ever due, which freezes the period rather than starting a new one.
    /// <para>
    /// Nothing is due before the active period's <c>PeriodClock.EndOf</c>, which after a schedule
    /// change is a boundary of the new grid; that keeps period ids rising, which every older-or-newer
    /// comparison here and in modules relies on.
    /// </para>
    /// </remarks>
    public void RollOverIfDue()
    {
        if (!SchemaUsable) return;

        var grid = CurrentGrid;
        if (!grid.IsEnabled) return;

        RollOverTo(PeriodClock.Due(ActivePeriod, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), grid));
    }

    /// <summary>Moves every resident record into <paramref name="due"/> when it is later than the active period.</summary>
    private void RollOverTo(long due)
    {
        var closed = ActivePeriod;
        if (due <= closed) return;

        Volatile.Write(ref _activePeriod, due);

        var startPoints = _config.Current.Rank.StartPoints;
        var rankIndex = _ladder.IndexFor(startPoints);
        var moved = 0;

        foreach (var record in _registry.All)
        {
            if (record.Period == due) continue;

            record.StartPeriod(due, startPoints, rankIndex);
            moved++;
        }

        _logger.LogInformation(
            "Ranks: period {Closed} closed and {Opened} opened; {Count} resident record(s) moved into it.",
            closed, due, moved);

        _events.RaisePeriodChanged(new RanksPeriodChangedArgs(closed, due));
        _ = SaveAllAsync();
    }

    public void Start()
    {
        _core.Event.OnClientSteamAuthorize += OnSteamAuthorized;
        _core.Event.OnClientSteamAuthorizeFail += OnSteamAuthorizeFailed;
        _core.Event.OnClientPutInServer += OnPutInServer;
        _core.Event.OnClientDisconnected += OnDisconnected;

        StartSaveTimer();
    }

    public void Stop()
    {
        _core.Event.OnClientSteamAuthorize -= OnSteamAuthorized;
        _core.Event.OnClientSteamAuthorizeFail -= OnSteamAuthorizeFailed;
        _core.Event.OnClientPutInServer -= OnPutInServer;
        _core.Event.OnClientDisconnected -= OnDisconnected;

        _saveTimer?.Cancel();
        _saveTimer = null;
    }

    private void StartSaveTimer()
    {
        var interval = _config.Current.Database.SaveIntervalSeconds;
        if (interval <= 0) return;

        // RepeatBySeconds fires its first callback immediately, so the flag check inside
        // SaveAllAsync is what stops a save racing the schema probe at boot.
        _saveTimer = _core.Scheduler.RepeatBySeconds(interval, () =>
        {
            RollOverIfDue();
            _ = SaveAllAsync();
        });
    }

    private void OnSteamAuthorized(IOnClientSteamAuthorizeEvent e)
    {
        if (_core.PlayerManager.GetPlayer(e.PlayerId) is { } player) Track(player);
    }

    /// <remarks>
    /// The trigger every other plugin here uses as its primary: authorize alone leaves no trace
    /// when it does not fire, and can land before GetPlayer resolves. Track is idempotent.
    /// </remarks>
    private void OnPutInServer(IOnClientPutInServerEvent e)
    {
        if (_core.PlayerManager.GetPlayer(e.PlayerId) is { } player) Track(player);
    }

    private void Track(IPlayer player)
    {
        if (player.IsFakeClient) return;

        var steamId = player.SteamID;
        var slot = player.Slot;

        // PutInServer commonly lands a few hundred ms before Steam auth finalizes.
        if (steamId == 0)
        {
            PollForAuthorization(slot);
            return;
        }

        var name = SafeName(player);

        // Already resident: a reconnect adopts the record rather than reloading it, which is what
        // stops a fast reconnect discarding points the pending save has not written yet.
        if (_registry.BySteamId(steamId) is { } resident)
        {
            resident.Name = name;
            _registry.Adopt(slot, resident);
            resident.StartPlaytime(DateTime.UtcNow);
            _events.RaisePlayerDataLoaded(Snapshot(resident));
            return;
        }

        if (!_loading.TryAdd(steamId, 0)) return;

        var period = ActivePeriod;
        var startPoints = _config.Current.Rank.StartPoints;

        Task.Run(() =>
        {
            try
            {
                var loaded = _store.Load(steamId, period, startPoints) ?? NewRecord(steamId, name, period);

                _core.Scheduler.NextTick(() =>
                {
                    try { Publish(steamId, name, loaded); }
                    finally { _loading.TryRemove(steamId, out _); }
                });
            }
            catch (Exception ex)
            {
                _loading.TryRemove(steamId, out _);

                // Deliberately leaves the player untracked. Substituting an empty record would let
                // the next absolute save wipe their real row (FR-DB-07).
                _logger.LogError(ex, "Ranks: failed to load record for {SteamId}; the player stays untracked.", steamId);
            }
        });
    }

    /// <remarks>Reads IPlayer on the game thread; doing it on the pool thread crashes the host.</remarks>
    private void PollForAuthorization(int slot)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < AuthPollAttempts; attempt++)
            {
                await Task.Delay(AuthPollIntervalMs).ConfigureAwait(false);

                // Not NextTickAsync<T>(Func<T>): SwiftlyS2's obsolete-API scanner misreads it as the async overload.
                var settled = false;
                await _core.Scheduler.NextTickAsync(() =>
                {
                    var player = _core.PlayerManager.GetPlayer(slot);

                    if (player is null || player.IsFakeClient) { settled = true; return; }
                    if (player.SteamID == 0) return;

                    Track(player);
                    settled = true;
                }).ConfigureAwait(false);

                if (settled) return;
            }

            _logger.LogWarning(
                "Ranks: slot {Slot} never finished Steam authorization; that player is not tracked and earns nothing this session.",
                slot);
        });
    }

    private void Publish(ulong steamId, string name, PlayerRecord loaded)
    {
        AlignPeriod(loaded);

        loaded.Name = name;
        loaded.LastSeen = DateTime.UtcNow;
        loaded.RankIndex = _ladder.IndexFor(loaded.Points);
        loaded.Dirty = true;

        var record = Seat(loaded);

        _detail.Write("Ranks: loaded {SteamId} with {Points} points.", steamId, record.Points);
        _events.RaisePlayerDataLoaded(Snapshot(record));
    }

    /// <summary>Publishes a loaded record, binding it to the slot its owner holds now and starting their playtime clock; once they have left, neither.</summary>
    /// <remarks>
    /// The owner may have left, or reconnected into another slot, while the load was in flight. On the slot they held
    /// when it began, the record would be credited with whatever that slot's next occupant does until their own load
    /// lands; and a clock started for someone already gone banks time forever, since no disconnect follows to stop it.
    /// </remarks>
    private PlayerRecord Seat(PlayerRecord loaded)
    {
        var slot = SlotOf(loaded.SteamId);
        var record = _registry.Adopt(slot, loaded);

        if (slot >= 0) record.StartPlaytime(DateTime.UtcNow);

        return record;
    }

    /// <summary>Brings a record loaded off-thread into the period the server is in now.</summary>
    /// <remarks>
    /// A newer stored period means another server sharing the database crossed the boundary first,
    /// so this one catches up rather than trailing it. An older one means a rollover landed while
    /// the load was in flight, and the record follows everyone else across.
    /// </remarks>
    private void AlignPeriod(PlayerRecord loaded)
    {
        if (loaded.Period > ActivePeriod) CatchUp(loaded.Period);
        if (loaded.Period == ActivePeriod) return;

        var startPoints = _config.Current.Rank.StartPoints;
        loaded.StartPeriod(ActivePeriod, startPoints, _ladder.IndexFor(startPoints));
    }

    /// <summary>Moves this server into a period another server sharing the database has started.</summary>
    /// <remarks>
    /// Even when its own schedule has not reached that period — a different mode, a clock a little
    /// behind, periods off — because the alternative moves the loaded record back, archiving the
    /// player's live season and restarting them in an older one (FR-DB-10).
    /// </remarks>
    private void CatchUp(long newer)
    {
        if (!SchemaUsable) return;

        var grid = CurrentGrid;
        var due = grid.IsEnabled ? PeriodClock.Due(ActivePeriod, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), grid) : ActivePeriod;

        RollOverTo(Math.Max(newer, due));
    }

    /// <remarks>
    /// Ungated at Warning: a player silently not being tracked is indistinguishable from a bug, so
    /// this is the only signal anyone gets (FR-DB-07).
    /// </remarks>
    private void OnSteamAuthorizeFailed(IOnClientSteamAuthorizeFailEvent e) =>
        _logger.LogWarning(
            "Ranks: Steam authorization failed for slot {Slot}; that player is not tracked and earns nothing this session.",
            e.PlayerId);

    /// <remarks>
    /// Must not gate on pawn validity — IsValid requires a pawn, which a disconnecting player no
    /// longer has, so the usual guard would skip every disconnect.
    /// </remarks>
    private void OnDisconnected(IOnClientDisconnectedEvent e)
    {
        var record = _registry.BySlot(e.PlayerId);
        _registry.ReleaseSlot(e.PlayerId);

        if (record is null) return;

        var now = DateTime.UtcNow;
        record.StopPlaytime(now);
        record.LastSeen = now;
        record.Dirty = true;

        _ = SavePlayerAsync(record.SteamId);
    }

    /// <summary>Starts tracking everyone already connected and authorized, for a load into a running server.</summary>
    /// <remarks>
    /// Their authorize event has long since fired, and waiting for another one would leave the whole server untracked.
    /// Game thread only: tracking reads each player's name.
    /// </remarks>
    public void AdoptConnectedPlayers()
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient && player.IsAuthorized)
                Track(player);
    }

    /// <summary>Writes one player. False when the SteamID is not tracked.</summary>
    /// <remarks>Queued behind every write already queued (FR-API-09; implementation guide §4.7).</remarks>
    public Task<bool> SavePlayerAsync(ulong steamId)
    {
        if (!SchemaUsable) return Task.FromResult(false);

        var record = _registry.BySteamId(steamId);
        if (record is null) return Task.FromResult(false);

        if (_core.IsGameThread) record.CreditPlaytime(DateTime.UtcNow);

        lock (_saveGate)
        {
            var save = PendingSave.Of(record);

            return Enqueue(() => WritePlayer(save));
        }
    }

    /// <summary>Writes every dirty record. Coalesces — a concurrent call joins the pass in flight.</summary>
    /// <remarks>
    /// A module calling this every round end on a 64-slot server must cost one pass, not 64
    /// (FR-API-09).
    /// </remarks>
    public Task<int> SaveAllAsync()
    {
        if (!SchemaUsable) return Task.FromResult(0);

        BankPlaytime();

        lock (_saveGate)
        {
            if (_pendingPass is { IsCompleted: false } pending) return pending;

            _pendingPass = EnqueuePass(settle: true);

            return _pendingPass;
        }
    }

    /// <summary>Banks every connected player's playtime so far, so the pass about to snapshot carries it.</summary>
    /// <remarks>
    /// Game thread only, where connect and disconnect move the same clocks. A module may call
    /// <see cref="SaveAllAsync"/> from anywhere; such a pass writes what was banked last.
    /// </remarks>
    private void BankPlaytime()
    {
        if (!_core.IsGameThread) return;

        var now = DateTime.UtcNow;

        foreach (var record in _registry.All)
            record.CreditPlaytime(now);
    }

    /// <summary>Snapshots every dirty record and queues one write of them.</summary>
    /// <remarks>
    /// Called under <see cref="_saveGate"/>. With nothing dirty nothing is queued: a record is clean only once a
    /// write of its current values has committed, and every write queued before that one committed ahead of it.
    /// </remarks>
    private Task<int> EnqueuePass(bool settle)
    {
        var pending = _registry.All
            .Where(record => record.Dirty)
            .Select(PendingSave.Of)
            .ToArray();

        return pending.Length == 0 ? Task.FromResult(0) : Enqueue(() => WritePass(pending, settle));
    }

    /// <summary>Runs <paramref name="write"/> on the thread pool once every write queued before it has finished.</summary>
    /// <remarks>
    /// The one queue every player-row write goes through, called under <see cref="_saveGate"/> after the snapshot
    /// (FR-API-09; implementation guide §4.7). Each write catches its own failure, and none breaks the chain for the
    /// writes behind it.
    /// </remarks>
    private Task<T> Enqueue<T>(Func<T> write)
    {
        var queued = _saveTail.ContinueWith(_ => write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _saveTail = queued;

        return queued;
    }

    /// <summary>Writes one player's snapshot, then has the game thread settle it once it committed.</summary>
    private bool WritePlayer(PendingSave save)
    {
        try
        {
            var outcome = _store.Save([save.Row]);
            ReportFailures(outcome);

            if (outcome.Committed.Count == 0) return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: failed to save {SteamId}.", save.Row.SteamId);
            return false;
        }

        SettleOnGameThread([save]);

        return true;
    }

    /// <summary>Writes one batch pass and returns how many records committed.</summary>
    /// <remarks>Those are settled on the game thread unless <paramref name="settle"/> is false, which only the unload pass asks for.</remarks>
    private int WritePass(PendingSave[] pending, bool settle)
    {
        SaveOutcome outcome;

        try
        {
            outcome = _store.Save(pending.Select(save => save.Row).ToArray());
            ReportFailures(outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: save pass failed for {Count} record(s); they stay dirty and retry.", pending.Length);
            return 0;
        }

        var committed = outcome.Committed.ToHashSet();

        if (settle) SettleOnGameThread(Array.FindAll(pending, save => committed.Contains(save.Record.SteamId)));

        _detail.Write("Ranks: saved {Count} record(s).", committed.Count);

        return committed.Count;
    }

    /// <summary>Settles committed writes on the next tick, where records, the registry and <c>PlayerDataSaved</c> belong.</summary>
    /// <remarks>
    /// Fire-and-forget (FR-API-09; implementation guide §4.7). A tick still queued when the plugin unloads is
    /// dropped with the registry it would have settled; posting one after that throws
    /// <see cref="ObjectDisposedException"/>, which means the same.
    /// </remarks>
    private void SettleOnGameThread(PendingSave[] committed)
    {
        if (committed.Length == 0) return;

        try
        {
            _core.Scheduler.NextTick(() => SettleAll(committed));
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SettleAll(PendingSave[] committed)
    {
        foreach (var save in committed)
            Settle(save);
    }

    /// <remarks>
    /// Game thread only: it raises <c>PlayerDataSaved</c> and reads the player list. Clears the dirty
    /// flag only when nothing changed since the snapshot. An award landing mid-save must stay dirty, or
    /// an absolute writer would persist the pre-award value and then never revisit it.
    /// <para>
    /// Only the record's newest queued write clears the flag or drops the record. An older one can find the record
    /// back on its own values after they moved and returned while the newer write waited; clearing there leaves the
    /// row on the newer write's values, and nothing ever rewrites it. Its archives and <c>PlayerDataSaved</c> settle
    /// either way.
    /// </para>
    /// <para>
    /// A clean record no connected player holds is dropped, whichever write committed it — the disconnect save, a
    /// module's save, or a pass. Kept, it waits for its player's return, who would then adopt it instead of their
    /// row, and on a database other servers share that row may hold newer figures.
    /// </para>
    /// </remarks>
    private void Settle(PendingSave save)
    {
        var record = save.Record;

        if (record.IsLatestSave(save.Stamp) && PlayerSnapshotRow.From(record) == save.Row) record.Dirty = false;

        record.SettleArchives(save.Row.Archives);

        _events.RaisePlayerDataSaved(Snapshot(record));

        if (!record.Dirty && record.IsLatestSave(save.Stamp) && !IsSeated(record)) _registry.Evict(record);
    }

    /// <summary>Whether the record's player is connected and bound to it.</summary>
    private bool IsSeated(PlayerRecord record) => _registry.BySlot(SlotOf(record.SteamId)) == record;

    /// <summary>The slot of the connected player a record keyed on <paramref name="steamId"/> belongs to, or -1.</summary>
    /// <remarks>Matched on <c>SteamID</c>, the id <see cref="Track"/> keyed the record on — under strict Steam authorization a client reads 0 there until Steam has confirmed it.</remarks>
    private int SlotOf(ulong steamId)
    {
        foreach (var player in _core.PlayerManager.GetAllPlayers())
            if (!player.IsFakeClient && player.SteamID == steamId)
                return player.Slot;

        return -1;
    }

    private void ReportFailures(SaveOutcome outcome)
    {
        foreach (var (steamId, error) in outcome.Failed)
            _logger.LogError(error, "Ranks: skipped {SteamId} in this save pass; it stays dirty and retries.", steamId);
    }

    /// <summary>Runs the purge, then tells modules the window is open.</summary>
    /// <remarks>
    /// <paramref name="force"/> is true only at server start. A load into a running server passes neither force nor
    /// an empty server, which is the point — a reload is how an operator deploys a fix mid-session, so a
    /// purge there would freeze a live server.
    /// </remarks>
    public async Task RunMaintenanceAsync(bool force)
    {
        if (!SchemaUsable) return;

        var config = _config.Current;

        if (!force)
        {
            var hours = config.Database.MaintenanceIntervalHours;
            if (hours <= 0) return;
            if (DateTime.UtcNow - _lastMaintenance < TimeSpan.FromHours(hours)) return;
            if (_core.PlayerManager.GetAllPlayers().Any(p => !p.IsFakeClient)) return;
        }

        try
        {
            await PurgeAsync(config.Database.PurgeDays).ConfigureAwait(false);

            // Set after the pass, so a failure retries on the next eligible round end rather than
            // being marked done.
            _lastMaintenance = DateTime.UtcNow;

            _core.Scheduler.NextTick(_events.RaiseMaintenanceDue);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: maintenance pass failed.");
        }
    }

    private async Task PurgeAsync(int purgeDays)
    {
        if (purgeDays <= 0) return;

        var purged = await Task.Run(() => _store.Purge(purgeDays)).ConfigureAwait(false);

        if (purged.Count == 0) return;

        _logger.LogInformation("Ranks: purged {Count} player record(s) unseen for {Days} day(s).", purged.Count, purgeDays);

        _core.Scheduler.NextTick(() =>
        {
            _registry.EvictAll(purged);
            _events.RaisePlayersPurged(purged);
        });
    }

    /// <summary>Blocking final flush with a bounded budget, for <c>Unload</c>.</summary>
    /// <remarks>
    /// Queues a pass of its own rather than joining one in flight, which would miss whatever changed after that
    /// pass was snapshotted, and waits for it behind every write queued before. It settles nothing: the registry
    /// dies with this instance.
    /// </remarks>
    public void FlushOnUnload(TimeSpan budget)
    {
        if (!SchemaUsable) return;

        try
        {
            if (!QueueFinalPass().Wait(budget))
                _logger.LogWarning("Ranks: final save did not finish within {Seconds}s; some records may be stale.", budget.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ranks: final save failed.");
        }
    }

    /// <summary>Banks playtime, then queues one unsettled pass of every dirty record.</summary>
    private Task<int> QueueFinalPass()
    {
        BankPlaytime();

        lock (_saveGate)
            return EnqueuePass(settle: false);
    }

    /// <summary>A record, the snapshot a write of it carries and that write's stamp, kept together so the settle can compare them.</summary>
    private readonly record struct PendingSave(PlayerRecord Record, PlayerSnapshotRow Row, long Stamp)
    {
        /// <summary>Snapshots <paramref name="record"/> and stamps the write about to carry it. Under <c>_saveGate</c> only.</summary>
        public static PendingSave Of(PlayerRecord record) => new(record, PlayerSnapshotRow.From(record), record.StampSave());
    }

    public RanksPlayerSnapshot Snapshot(PlayerRecord record)
    {
        var tier = _ladder.Tiers[Math.Clamp(record.RankIndex, 0, _ladder.Tiers.Count - 1)];

        return new RanksPlayerSnapshot(
            record.SteamId, record.Name, record.Points, tier.Name, tier.Tag, record.RankIndex,
            record.Kills, record.Deaths, record.Assists,
            record.RoundWins, record.RoundLosses, record.MatchWins, record.MatchLosses,
            record.BombsPlanted, record.BombsDefused, record.FirstSeen, record.LastSeen)
        {
            Headshots = record.Headshots,
            Hits = record.Hits,
            Misses = record.Misses,
            Playtime = record.LivePlaytime,
            Period = record.Period,
        };
    }

    private PlayerRecord NewRecord(ulong steamId, string name, long period) => new()
    {
        SteamId = steamId,
        Name = name,
        Period = period,
        Points = _config.Current.Rank.StartPoints,
        FirstSeen = DateTime.UtcNow,
        LastSeen = DateTime.UtcNow,
    };

    private string SafeName(IPlayer player)
    {
        try
        {
            var name = player.Name;

            return string.IsNullOrWhiteSpace(name) ? player.SteamID.ToString() : name;
        }
        catch
        {
            return player.SteamID.ToString();
        }
    }
}
