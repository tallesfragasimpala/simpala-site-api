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
                var removed = store.Purge(now.AddDays(-options.Value.RetentionDays), now);
                if (removed > 0) logger.LogInformation("Removidos {Count} registros de simulação pela retenção", removed);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
