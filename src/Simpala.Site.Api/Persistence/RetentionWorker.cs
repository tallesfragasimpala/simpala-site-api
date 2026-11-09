using Microsoft.Extensions.Options;
using Simpala.Site.Api.Configuration;

namespace Simpala.Site.Api.Persistence;

public sealed class RetentionWorker(ContactStore store, IOptions<ContactOptions> options,
    TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        try
        {
            do
            {
                var now = clock.GetUtcNow();
                try
                {
                    var removed = store.Purge(now.AddDays(-options.Value.RetentionDays), now);
                    if (removed > 0) logger.LogInformation("Removidos {Count} registros pela retenção", removed);
                }
                catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or IOException)
                {
                    // Do not log exception bodies, database paths or submitted data. Retry next interval.
                    logger.LogWarning("Não foi possível executar a retenção. Uma nova tentativa ocorrerá no próximo intervalo.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
