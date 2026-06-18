// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using Npgsql;

namespace ApplyTrack.Api.Data;

/// <summary>
/// A <see cref="DbConnection"/> decorator that retries a transient failure while
/// <em>opening</em> the physical connection, then delegates everything else to the
/// wrapped <see cref="NpgsqlConnection"/>.
///
/// Why: opening a pooled connection resolves the DB host every time the pool has to
/// grow, and under container DNS (podman/aardvark-dns) that resolve intermittently
/// fails with <c>SocketException (EAGAIN) "Resource temporarily unavailable"</c>,
/// surfacing as an <see cref="NpgsqlException"/> that 500'd whatever request happened
/// to trigger the cold open (the magic-link endpoint, most visibly). A bounded retry
/// with short backoff rides out the blip — the next resolve succeeds in milliseconds.
///
/// Sits transparently under Dapper: Dapper calls Open/OpenAsync on this decorator, so
/// every repo inherits the resiliency with no per-call change. Only the open is
/// retried; commands are not (re-running a half-applied write is not safe to assume).
/// </summary>
public sealed class ResilientDbConnection(NpgsqlConnection inner, ILogger<ResilientDbConnection> log)
    : DbConnection
{
    // 5 attempts over ~50+150+350+750ms covers a DNS/connect blip without hanging a
    // request; a genuinely-down DB still fails fast-ish and bubbles up as before.
    private static readonly int[] BackoffMs = [50, 150, 350, 750];

    private readonly NpgsqlConnection _inner = inner;

    public override void Open() => Retry(() => _inner.Open());

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _inner.OpenAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < BackoffMs.Length && IsTransient(ex))
            {
                log.LogWarning(ex,
                    "Transient failure opening the DB connection (attempt {Attempt}/{Max}); "
                    + "retrying in {Delay}ms", attempt + 1, BackoffMs.Length + 1, BackoffMs[attempt]);
                await Task.Delay(BackoffMs[attempt], cancellationToken);
            }
        }
    }

    private void Retry(Action open)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                open();
                return;
            }
            catch (Exception ex) when (attempt < BackoffMs.Length && IsTransient(ex))
            {
                log.LogWarning(ex,
                    "Transient failure opening the DB connection (attempt {Attempt}/{Max}); "
                    + "retrying in {Delay}ms", attempt + 1, BackoffMs.Length + 1, BackoffMs[attempt]);
                Thread.Sleep(BackoffMs[attempt]);
            }
        }
    }

    /// <summary>
    /// A network/connect hiccup is worth retrying; a server-side error (bad password,
    /// missing table) is not — honour Npgsql's own <c>IsTransient</c> for those.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        PostgresException pg => pg.IsTransient,
        NpgsqlException ne => ne.IsTransient || ne.InnerException is SocketException,
        SocketException => true,
        _ => false,
    };

    // ---- straight delegation to the wrapped connection ----

    [AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value;
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override Task CloseAsync() => _inner.CloseAsync();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
