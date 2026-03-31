using BookingService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BookingService.Infrastructure;

public sealed class ReadReplicaOptions
{
    public List<ReadReplicaNodeOptions> Nodes { get; init; } = [];
}

public sealed class ReadReplicaNodeOptions
{
    public string Name { get; init; } = string.Empty;

    public string ConnectionString { get; init; } = string.Empty;
}

public sealed record ReadReplicaSelection(string Name, string ConnectionString, bool IsFallbackToPrimary);

public interface IReadReplicaSelector
{
    Task<ReadReplicaSelection> SelectAsync(CancellationToken cancellationToken = default);
}

public sealed class RoundRobinReadReplicaSelector : IReadReplicaSelector
{
    private readonly IReadOnlyList<ReadReplicaNodeOptions> _replicas;
    private readonly string _writeConnectionString;
    private readonly ILogger<RoundRobinReadReplicaSelector> _logger;
    private int _nextIndex = -1;

    public RoundRobinReadReplicaSelector(
        IOptions<ReadReplicaOptions> options,
        IConfiguration configuration,
        ILogger<RoundRobinReadReplicaSelector> logger)
    {
        _replicas = options.Value.Nodes
            .Where(replica => !string.IsNullOrWhiteSpace(replica.ConnectionString))
            .ToArray();
        _writeConnectionString = configuration.GetConnectionString("Write")
            ?? throw new InvalidOperationException("Write connection string is not configured.");
        _logger = logger;
    }

    public async Task<ReadReplicaSelection> SelectAsync(CancellationToken cancellationToken = default)
    {
        if (_replicas.Count == 0)
        {
            _logger.LogWarning("No read replicas are configured. Falling back to primary.");
            return new ReadReplicaSelection("primary-fallback", _writeConnectionString, true);
        }

        // Round-robin is a good first production strategy because it spreads
        // reads evenly without making the endpoint know anything about topology.
        var startIndex = Math.Abs(Interlocked.Increment(ref _nextIndex)) % _replicas.Count;

        for (var offset = 0; offset < _replicas.Count; offset++)
        {
            var replica = _replicas[(startIndex + offset) % _replicas.Count];

            if (await IsReplicaHealthyAsync(replica.ConnectionString, cancellationToken))
            {
                return new ReadReplicaSelection(replica.Name, replica.ConnectionString, false);
            }
        }

        // In production we usually degrade to primary reads instead of failing
        // the entire request when every replica is temporarily unhealthy.
        _logger.LogWarning("All read replicas are unhealthy. Falling back to primary.");
        return new ReadReplicaSelection("primary-fallback", _writeConnectionString, true);
    }

    private static async Task<bool> IsReplicaHealthyAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is true;
        }
        catch
        {
            return false;
        }
    }
}

public interface IReadBookingDbContextFactory
{
    Task<ReadBookingDbContextHandle> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class ReadBookingDbContextFactory : IReadBookingDbContextFactory
{
    private readonly IReadReplicaSelector _selector;

    public ReadBookingDbContextFactory(IReadReplicaSelector selector)
    {
        _selector = selector;
    }

    public async Task<ReadBookingDbContextHandle> CreateAsync(CancellationToken cancellationToken = default)
    {
        var selection = await _selector.SelectAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<ReadBookingDbContext>()
            .UseNpgsql(selection.ConnectionString)
            .Options;

        return new ReadBookingDbContextHandle(new ReadBookingDbContext(options), selection);
    }
}

public sealed class ReadBookingDbContextHandle : IAsyncDisposable
{
    public ReadBookingDbContextHandle(ReadBookingDbContext dbContext, ReadReplicaSelection selection)
    {
        DbContext = dbContext;
        Selection = selection;
    }

    public ReadBookingDbContext DbContext { get; }

    public ReadReplicaSelection Selection { get; }

    public ValueTask DisposeAsync() => DbContext.DisposeAsync();
}
