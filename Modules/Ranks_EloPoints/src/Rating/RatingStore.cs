using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Ranks.EloPoints.Match;
using SwiftlyS2.Shared;

namespace Ranks.EloPoints.Rating;

/// <summary>Reads and writes the authoritative rating. Off-thread, never touches a live session.</summary>
/// <remarks>
/// The upsert replaces <c>rating</c> rather than accumulating it, the opposite of
/// Ranks_WeaponStats: that module holds session deltas with no read on connect, while this one
/// reads the current value, computes a new one and writes it.
///
/// The rating table holds each player's current-period row only. A row left on an older period is
/// that period's final rating, and moves to the history table the first time a read or a write for
/// a later period touches its player (FR-DB-10).
///
/// Every database operation runs on one queue, in the order the game thread asked for it. That is
/// what keeps a reset between the settle before it and the settle after it, and what stops a read
/// closing a row while a write for the same player is still in flight.
/// </remarks>
internal sealed class RatingStore
{
    private readonly ISwiftlyCore _core;
    private readonly ILogger _logger;
    private readonly Lock _queueGate = new();

    /// <summary>Last known rating per player and the period it belongs to, so a resync can run without a read.</summary>
    private readonly ConcurrentDictionary<ulong, CachedRating> _cache = new();

    /// <summary>Changes a failed transaction left behind, applied again ahead of the next ones.</summary>
    /// <remarks>Touched only from queued work, and queued work never runs two at once.</remarks>
    private readonly List<RatingChange> _retry = [];

    /// <summary>How many failures of its own statements drop a reset or purge from the retry list.</summary>
    /// <remarks>
    /// Unbounded, one the database always refuses — no DELETE permission, say — holds every later rating back for the
    /// rest of the load. Only its own statements count, unless the driver marks the failure transient.
    /// </remarks>
    private const int ClearAttempts = 3;

    private Task _queueTail = Task.CompletedTask;
    private string _connectionName = "default";
    private string _table = "ranks_elopoints";
    private string _history = "ranks_elopoints_history";
    private bool _mysql;

    public RatingStore(ISwiftlyCore core, ILogger logger)
    {
        _core = core;
        _logger = logger;
    }

    public bool IsReady { get; private set; }

    /// <summary>Binds the names core resolved. Until this runs the store refuses to touch the database.</summary>
    public void Configure(string connectionName, string table, string history)
    {
        _connectionName = connectionName;
        _table = table;
        _history = history;
        _mysql = _core.Database.GetConnectionInfo(connectionName).Driver?.Trim().ToLowerInvariant() is "mysql" or "mariadb";
        IsReady = true;
    }

    /// <summary>The rating last read, settled or adjusted for this player in <paramref name="period"/>, when there is one.</summary>
    public bool TryGetCached(ulong steamId, long period, out double rating)
    {
        var known = _cache.TryGetValue(steamId, out var cached) && cached.Period == period ? cached.Rating : null;
        rating = known ?? 0;

        return known.HasValue;
    }

    /// <summary>Drops every cached rating: after a rollover they all belong to the period that closed.</summary>
    public void ForgetAll() => _cache.Clear();

    /// <summary>Reads a player's rating for <paramref name="period"/>: the stored one, none when they hold none there yet, or the failure.</summary>
    /// <remarks>
    /// Off the game thread: a blocking database read at a round boundary is a stall. A read still
    /// in flight when a round ends leaves the session unresolved, and an unresolved player is left
    /// out of the team average rather than being averaged in at a guessed value.
    /// </remarks>
    public Task<RatingRead> ReadAsync(ulong steamId, long period)
    {
        if (!IsReady) return Task.FromResult(RatingRead.None);

        if (_cache.TryGetValue(steamId, out var cached) && cached.Period == period) return Task.FromResult(RatingRead.Of(cached.Rating));

        return Enqueue(() => ReadOrClose(steamId, period));
    }

    /// <summary>Queues settled ratings for writing, each into the period it was settled in.</summary>
    /// <remarks>
    /// A failed batch stays queued ahead of the next one rather than merging back into the live
    /// sessions, which belong to the game thread.
    /// </remarks>
    public Task CommitAsync(List<RatingWrite> batch)
    {
        if (!IsReady || batch.Count == 0) return Task.CompletedTask;

        foreach (var write in batch) _cache[write.SteamId] = new CachedRating(write.Period, write.Rating);

        var changes = batch.Select(RatingChange.Settled).ToArray();

        return Enqueue(() => Flush(changes));
    }

    /// <summary>Wipes a player's rating for <paramref name="period"/>, behind every write already queued.</summary>
    /// <remarks>
    /// Run at once instead, the delete could land before a write still in flight from the last settle,
    /// which would put the pre-reset rating straight back. The cache answers "none" from here on, so a
    /// read cannot find the old row while the delete waits its turn.
    /// </remarks>
    public Task ResetAsync(ulong steamId, long period)
    {
        if (!IsReady) return Task.CompletedTask;

        _cache[steamId] = new CachedRating(period, null);

        return Enqueue(() => Flush([new RatingChange.Reset(steamId, period)]));
    }

    /// <summary>Writes an admin's change to a player's rating for <paramref name="period"/>, behind every write already queued.</summary>
    /// <remarks>
    /// Counts no match: <c>matches</c> and <c>abandons</c> stay as they are, or start at zero on a new row.
    /// Cached at once, so the next resync agrees with it and a read still in flight cannot replace it.
    /// </remarks>
    public Task AdjustAsync(ulong steamId, long period, double rating)
    {
        if (!IsReady) return Task.CompletedTask;

        _cache[steamId] = new CachedRating(period, rating);

        return Enqueue(() => Flush([RatingChange.Adjusted(new RatingWrite(steamId, period, rating, Abandoned: false))]));
    }

    /// <summary>Deletes every row, current and past, for purged players.</summary>
    /// <remarks>
    /// <c>PlayersPurged</c> is the only notice this module will ever get — both tables are keyed on
    /// SteamID64 with nothing joining them back to core, so nothing else sweeps them.
    /// </remarks>
    public Task PurgeAsync(IReadOnlyList<ulong> steamIds)
    {
        if (!IsReady || steamIds.Count == 0) return Task.CompletedTask;

        var ids = steamIds.ToHashSet();
        foreach (var id in ids) _cache.TryRemove(id, out _);

        return Enqueue(() => Purge(ids));
    }

    /// <summary>Runs <paramref name="work"/> on the thread pool once everything queued before it has finished.</summary>
    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_queueGate)
        {
            var queued = _queueTail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _queueTail = queued;

            return queued;
        }
    }

    private Task Enqueue(Action work) => Enqueue(() =>
    {
        work();
        return true;
    });

    /// <summary>Returns the stored rating, or none: for a row it closes on an older period, or while the player's purge awaits a retry.</summary>
    /// <remarks>
    /// An older row is that period's final rating, so it moves to history and the player starts this
    /// period unrated. A row on a newer period comes back as it is: another server sharing the
    /// database crossed the boundary first, and this one is about to follow. A failure comes back as one, unlogged:
    /// the caller decides what a retry is worth reporting.
    /// </remarks>
    private RatingRead ReadOrClose(ulong steamId, long period)
    {
        if (PurgePending(steamId)) return RatingRead.None;

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();

            if (ReadRow(connection, null, steamId) is not { } row) return RatingRead.None;

            if (row.Period < period)
            {
                using var transaction = connection.BeginTransaction();
                Archive(connection, transaction, steamId, row.Period);
                transaction.Commit();

                return RatingRead.None;
            }

            Remember(steamId, period, row.Rating);

            return RatingRead.Of(row.Rating);
        }
        catch (Exception ex)
        {
            return RatingRead.Failed(ex);
        }
    }

    /// <summary>Caches a rating read from the table, unless something newer for that period got there first.</summary>
    /// <remarks>
    /// A settle, a reset or an admin's adjustment on the game thread may land while the read is in
    /// flight, and each one knows more than the row this read saw.
    /// </remarks>
    private void Remember(ulong steamId, long period, double rating)
    {
        var read = new CachedRating(period, rating);
        _cache.AddOrUpdate(steamId, read, (_, existing) => existing.Period == period ? existing : read);
    }

    /// <summary>Whether a purge of <paramref name="steamId"/> that failed is still waiting in the retry list.</summary>
    /// <remarks>The player's old rows stay until it lands, and must not come back as their rating meanwhile.</remarks>
    private bool PurgePending(ulong steamId) =>
        _retry.Exists(change => change is RatingChange.Purge purge && purge.SteamIds.Contains(steamId));

    /// <summary>Applies whatever a failed transaction left behind, then <paramref name="changes"/>, in one transaction.</summary>
    /// <remarks>
    /// Never reordered: a reset queued between two settles has to stay between them, or one of them
    /// restores the rating it wiped, until <see cref="GiveUpOn"/> drops it. A data-loss path, so the failure is never
    /// gated behind DetailedLogging.
    /// </remarks>
    private void Flush(IEnumerable<RatingChange> changes)
    {
        _retry.AddRange(changes);

        if (_retry.Count == 0) return;

        RatingChange? applying = null;

        try
        {
            using var connection = _core.Database.GetConnection(_connectionName);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (var change in _retry)
            {
                applying = change;
                Apply(connection, transaction, change);
            }

            applying = null;
            transaction.Commit();
            _retry.Clear();
        }
        catch (Exception ex)
        {
            if (applying is { } failed && GiveUpOn(failed, ex)) Flush([]);
            else _logger.LogError(ex, "Ranks_EloPoints: writing {Count} rating change(s) failed; they are buffered for retry.", _retry.Count);
        }
    }

    /// <summary>
    /// Counts a failure of a reset's or purge's own statements that the driver does not mark transient, and drops the
    /// change at <see cref="ClearAttempts"/> of them.
    /// </summary>
    /// <returns>True when it was dropped, so the changes queued behind it are written at once.</returns>
    /// <remarks>A rating is never given up on: it is kept until it is written.</remarks>
    private bool GiveUpOn(RatingChange failed, Exception error)
    {
        if (failed is RatingChange.Rated || IsTransient(error) || ++failed.Failures < ClearAttempts) return false;

        _retry.RemoveAll(change => ReferenceEquals(change, failed));
        ReportDropped(failed, error);

        return true;
    }

    /// <summary>Whether the driver marks <paramref name="error"/>, or an exception it wraps, as transient: worth retrying unchanged.</summary>
    /// <remarks>
    /// Npgsql marks lost connections, deadlocks, and lock and command timeouts; MySqlConnector deadlocks and InnoDB
    /// lock-wait timeouts but no lost connection or command timeout; System.Data.SQLite nothing, so every failure counts.
    /// </remarks>
    private static bool IsTransient(Exception? error) =>
        error is not null && (error is DbException { IsTransient: true } || IsTransient(error.InnerException));

    /// <remarks>Ungated: what the change would have deleted now stays.</remarks>
    private void ReportDropped(RatingChange dropped, Exception error)
    {
        switch (dropped)
        {
            case RatingChange.Reset reset:
                _logger.LogError(
                    error,
                    "Ranks_EloPoints: gave up resetting the rating of {SteamId} after {Attempts} failed attempts; " +
                    "the stored one stays.",
                    reset.SteamId, dropped.Failures);
                break;

            case RatingChange.Purge purge:
                _logger.LogError(
                    error,
                    "Ranks_EloPoints: gave up deleting the ratings of {Count} purged player(s) after {Attempts} " +
                    "failed attempts; their rows stay.",
                    purge.SteamIds.Count, dropped.Failures);
                break;
        }
    }

    private void Apply(IDbConnection connection, IDbTransaction transaction, RatingChange change)
    {
        switch (change)
        {
            case RatingChange.Rated rated:
                Write(connection, transaction, rated.Write, rated.CountsMatch);
                break;

            case RatingChange.Reset reset:
                Wipe(connection, transaction, reset.SteamId, reset.Period);
                break;

            case RatingChange.Purge purge:
                Purge(connection, transaction, purge.SteamIds);
                break;
        }
    }

    /// <summary>Writes one rating into the period it was settled or adjusted in.</summary>
    /// <remarks>
    /// FR-DB-10. A row on an older period is closed first. A rating whose period has already closed
    /// for the player — a retry that crossed a rollover — goes to that period's history row instead,
    /// so it neither overwrites the current period nor re-opens a closed one.
    /// </remarks>
    private void Write(IDbConnection connection, IDbTransaction transaction, RatingWrite write, bool countsMatch)
    {
        var held = ReadRow(connection, transaction, write.SteamId)?.Period;

        if (held is { } stale && stale < write.Period) Archive(connection, transaction, write.SteamId, stale);

        var closed = held is { } stored ? stored > write.Period : HistoryHolds(connection, transaction, write);

        if (closed) Upsert(connection, transaction, _history, "steam_id, period", write, countsMatch);
        else Upsert(connection, transaction, _table, "steam_id", write, countsMatch);
    }

    /// <summary>Upserts one rating: <c>rating</c> is replaced, the two counters accumulate.</summary>
    /// <remarks>
    /// VALUES() on MySQL rather than 8.0.20's "AS new" row alias, which MariaDB does not support at
    /// all; elsewhere the counters are qualified with the table name, or the reference is ambiguous
    /// against EXCLUDED. The update leaves <c>period</c> alone: the row it can hit already holds this
    /// write's period, because <see cref="Write"/> closed or bypassed any other. An adjustment adds 0
    /// to both counters.
    /// </remarks>
    private void Upsert(IDbConnection connection, IDbTransaction transaction, string table, string key, RatingWrite write, bool countsMatch)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _mysql
            ? $"""
               INSERT INTO {table} (steam_id, period, rating, matches, abandons, updated_at)
               VALUES (@steam_id, @period, @rating, @matches, @abandons, @updated_at)
               ON DUPLICATE KEY UPDATE
                   rating = VALUES(rating),
                   matches = matches + VALUES(matches),
                   abandons = abandons + VALUES(abandons),
                   updated_at = VALUES(updated_at)
               """
            : $"""
               INSERT INTO {table} (steam_id, period, rating, matches, abandons, updated_at)
               VALUES (@steam_id, @period, @rating, @matches, @abandons, @updated_at)
               ON CONFLICT ({key}) DO UPDATE SET
                   rating = EXCLUDED.rating,
                   matches = {table}.matches + EXCLUDED.matches,
                   abandons = {table}.abandons + EXCLUDED.abandons,
                   updated_at = EXCLUDED.updated_at
               """;

        Add(command, "@steam_id", (long)write.SteamId);
        Add(command, "@period", write.Period);
        Add(command, "@rating", (decimal)write.Rating);
        Add(command, "@matches", countsMatch ? 1 : 0);
        Add(command, "@abandons", write.Abandoned ? 1 : 0);
        Add(command, "@updated_at", DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    /// <summary>Moves a player's row off a closed period: copied to history, then deleted.</summary>
    /// <remarks>
    /// Both statements are guarded on the stale period, so a second server closing the same row at
    /// once copies nothing and deletes nothing. A history row already there for that period is that
    /// same row, closed first by another server sharing the database, so the copy skips it.
    /// </remarks>
    private void Archive(IDbConnection connection, IDbTransaction transaction, ulong steamId, long stale)
    {
        const string Columns = "steam_id, period, rating, matches, abandons, updated_at";

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = _mysql
                ? $"INSERT IGNORE INTO {_history} ({Columns}) SELECT {Columns} FROM {_table} " +
                  "WHERE steam_id = @steam_id AND period = @stale"
                : $"INSERT INTO {_history} ({Columns}) SELECT {Columns} FROM {_table} " +
                  "WHERE steam_id = @steam_id AND period = @stale ON CONFLICT (steam_id, period) DO NOTHING";
            Add(copy, "@steam_id", (long)steamId);
            Add(copy, "@stale", stale);
            copy.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = $"DELETE FROM {_table} WHERE steam_id = @steam_id AND period = @stale";
        Add(delete, "@steam_id", (long)steamId);
        Add(delete, "@stale", stale);
        delete.ExecuteNonQuery();
    }

    /// <summary>Deletes a player's rating for one period, from whichever table holds it.</summary>
    /// <remarks>
    /// The period was current when the player was reset. History only comes into it when that period
    /// closed while this delete was waiting on a retry, and a read had already moved the row there.
    /// </remarks>
    private void Wipe(IDbConnection connection, IDbTransaction transaction, ulong steamId, long period)
    {
        Delete(connection, transaction, _table, steamId, period);
        Delete(connection, transaction, _history, steamId, period);
    }

    /// <summary>Drops what the purged players still have waiting, then deletes their rows through the retry list like any other change.</summary>
    /// <remarks>
    /// A purge that fails stays in the retry list ahead of every change queued after it, so a rating written for one of
    /// them since survives its retry.
    /// </remarks>
    private void Purge(HashSet<ulong> steamIds)
    {
        _retry.RemoveAll(change => IsFor(change, steamIds));

        Flush([new RatingChange.Purge(steamIds)]);
    }

    /// <summary>Deletes every row, current and past, of the purged players.</summary>
    private void Purge(IDbConnection connection, IDbTransaction transaction, HashSet<ulong> steamIds)
    {
        foreach (var steamId in steamIds)
        {
            Delete(connection, transaction, _table, steamId, null);
            Delete(connection, transaction, _history, steamId, null);
        }
    }

    /// <summary>Whether <paramref name="change"/> writes or wipes the rating of one of <paramref name="steamIds"/>.</summary>
    private static bool IsFor(RatingChange change, HashSet<ulong> steamIds) => change switch
    {
        RatingChange.Rated rated => steamIds.Contains(rated.Write.SteamId),
        RatingChange.Reset reset => steamIds.Contains(reset.SteamId),
        _ => false,
    };

    /// <summary>The player's row in the rating table, whatever period it holds, or null when there is none.</summary>
    private (long Period, double Rating)? ReadRow(IDbConnection connection, IDbTransaction? transaction, ulong steamId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT period, rating FROM {_table} WHERE steam_id = @steam_id";
        Add(command, "@steam_id", (long)steamId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    /// <summary>True when the write's period has already closed for its player and sits in history.</summary>
    private bool HistoryHolds(IDbConnection connection, IDbTransaction transaction, RatingWrite write)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {_history} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)write.SteamId);
        Add(command, "@period", write.Period);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <remarks>Without <paramref name="period"/> every row the player holds in <paramref name="table"/> goes.</remarks>
    private static void Delete(IDbConnection connection, IDbTransaction transaction, string table, ulong steamId, long? period)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = period is null
            ? $"DELETE FROM {table} WHERE steam_id = @steam_id"
            : $"DELETE FROM {table} WHERE steam_id = @steam_id AND period = @period";
        Add(command, "@steam_id", (long)steamId);

        if (period is { } only) Add(command, "@period", only);

        command.ExecuteNonQuery();
    }

    private static void Add(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>A rating and the period it belongs to. A null rating means the player is known to hold none there.</summary>
    private readonly record struct CachedRating(long Period, double? Rating);

    /// <summary>One queued change: a rating to write, a reset of one player's period, or a purge.</summary>
    /// <remarks>The retry list holds these in queue order, so a failed reset or purge is retried where it was queued.</remarks>
    private abstract record RatingChange
    {
        /// <summary>How many times this change's own statements have failed, transient failures aside; counted for a reset or a purge only.</summary>
        public int Failures { get; set; }

        public static RatingChange Settled(RatingWrite write) => new Rated(write, CountsMatch: true);

        public static RatingChange Adjusted(RatingWrite write) => new Rated(write, CountsMatch: false);

        /// <summary>A rating to write into the period it was settled or adjusted in.</summary>
        /// <param name="CountsMatch">False for an admin's adjustment, which moves the rating without a match being played.</param>
        public sealed record Rated(RatingWrite Write, bool CountsMatch) : RatingChange;

        /// <summary>A core reset of one player's period.</summary>
        public sealed record Reset(ulong SteamId, long Period) : RatingChange;

        /// <summary>Purged players, deleted from both tables.</summary>
        public sealed record Purge(HashSet<ulong> SteamIds) : RatingChange;
    }
}

/// <summary>What a read of a player's stored rating came back with: a rating, none, or the <see cref="Error"/> that stopped it.</summary>
/// <remarks>
/// A failure is kept apart from finding no row. Taken for one, it starts a player who holds a rating from core's
/// rounded points for good, and their next match end writes that over the rating the read never reached.
/// </remarks>
internal readonly record struct RatingRead(double? Rating, Exception? Error)
{
    public static RatingRead None => default;

    public static RatingRead Of(double? rating) => new(rating, null);

    public static RatingRead Failed(Exception error) => new(null, error);
}
