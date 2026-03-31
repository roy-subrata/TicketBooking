using BookingService.Data;
using BookingService.Data.Entities;
using BookingService.EndPoints;
using BookingService.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var writeConnectionString = builder.Configuration.GetConnectionString("Write")
    ?? throw new InvalidOperationException("Write connection string is not configured.");
var readReplicaOptions = builder.Configuration
    .GetSection("ReadReplicas")
    .Get<ReadReplicaOptions>()
    ?? new ReadReplicaOptions();

if (readReplicaOptions.Nodes.Count == 0)
{
    throw new InvalidOperationException("At least one read replica must be configured.");
}

builder.Services.Configure<ReadReplicaOptions>(builder.Configuration.GetSection("ReadReplicas"));

builder.Services.AddDbContext<WriteBookingDbContext>(options =>
    options
    .UseNpgsql(writeConnectionString)
    );

builder.Services.AddSingleton<IReadReplicaSelector, RoundRobinReadReplicaSelector>();
builder.Services.AddScoped<IReadBookingDbContextFactory, ReadBookingDbContextFactory>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await WaitForDatabaseTopologyAsync(app, writeConnectionString, readReplicaOptions.Nodes);
await ApplyMigrationsAsync(app);
await SeedBookingsAsync(app);
await WaitForReplicaToCatchUpAsync(app, readReplicaOptions.Nodes);

app.MapGet("/live", () =>
{
    return Results.Ok("Booking Service is live!");
});
app.MapBookingEndPoints();
app.Run();

static async Task ApplyMigrationsAsync(WebApplication app)
{
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteBookingDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseMigration");

        const int maxAttempts = 20;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await dbContext.Database.MigrateAsync();
                logger.LogInformation("Database migration completed successfully.");
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(ex, "Database migration attempt {Attempt} of {MaxAttempts} failed. Retrying in 5 seconds...", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}

static async Task SeedBookingsAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<WriteBookingDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeding");

    if (await dbContext.Bookings.AnyAsync())
    {
        logger.LogInformation("Booking seed data already exists.");
        return;
    }

    var now = DateTime.UtcNow;
    for (var i = 1; i <= 10; i++)
    {
        await dbContext.Bookings.AddAsync(new Booking
        {
            SeatNumber = i,
            Status = BookingStatus.Available,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    await dbContext.SaveChangesAsync();
    logger.LogInformation("Seeded {Count} bookings.", 100);
}

static async Task WaitForDatabaseTopologyAsync(
    WebApplication app,
    string writeConnectionString,
    IReadOnlyCollection<ReadReplicaNodeOptions> readReplicas)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");

    await WaitForConditionAsync(
        logger,
        "primary database to accept writes",
        async cancellationToken =>
        {
            await using var connection = new NpgsqlConnection(writeConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT NOT pg_is_in_recovery()", connection);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is true;
        });

    foreach (var replica in readReplicas)
    {
        await WaitForConditionAsync(
            logger,
            $"replica {replica.Name} to accept reads",
            async cancellationToken =>
            {
                await using var connection = new NpgsqlConnection(replica.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = new NpgsqlCommand("SELECT pg_is_in_recovery()", connection);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result is true;
            });
    }
}

static async Task WaitForReplicaToCatchUpAsync(
    WebApplication app,
    IReadOnlyCollection<ReadReplicaNodeOptions> readReplicas)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ReplicaSync");
    string? latestMigration;

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteBookingDbContext>();
        latestMigration = (await dbContext.Database.GetAppliedMigrationsAsync()).LastOrDefault();
    }

    if (string.IsNullOrWhiteSpace(latestMigration))
    {
        logger.LogInformation("No applied migrations found on primary. Skipping replica catch-up wait.");
        return;
    }

    foreach (var replica in readReplicas)
    {
        await WaitForConditionAsync(
            logger,
            $"replica {replica.Name} to replay migration {latestMigration}",
            async cancellationToken =>
            {
                await using var connection = new NpgsqlConnection(replica.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM "__EFMigrationsHistory"
                        WHERE "MigrationId" = @migrationId
                    )
                    """,
                    connection);
                command.Parameters.AddWithValue("migrationId", latestMigration);

                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result is true;
            });
    }
}

static async Task WaitForConditionAsync(
    ILogger logger,
    string description,
    Func<CancellationToken, Task<bool>> condition,
    int maxAttempts = 60,
    int delaySeconds = 5)
{
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            if (await condition(cancellationTokenSource.Token))
            {
                logger.LogInformation("Wait completed: {Description}.", description);
                return;
            }

            logger.LogInformation("Wait check {Attempt}/{MaxAttempts} for {Description} is not ready yet.", attempt, maxAttempts, description);
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Wait check {Attempt}/{MaxAttempts} for {Description} failed. Retrying in {DelaySeconds} seconds...", attempt, maxAttempts, description, delaySeconds);
        }

        if (attempt == maxAttempts)
        {
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
    }

    throw new TimeoutException($"Timed out waiting for {description}.");
}
