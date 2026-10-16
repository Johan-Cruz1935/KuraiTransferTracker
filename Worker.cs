using KuraiTransferTracker.Configuration;
using KuraiTransferTracker.Services;
using Microsoft.Extensions.Options;

namespace KuraiTransferTracker;

public sealed class Worker : BackgroundService
{
    private readonly TransferSyncService _syncService;
    private readonly TrackerOptions _options;
    private readonly ILogger<Worker> _logger;

    public Worker(
        TransferSyncService syncService,
        IOptions<TrackerOptions> options,
        ILogger<Worker> logger)
    {
        _syncService = syncService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Iniciando Agente de Traqueo de Traspasos Kurai (Intervalo de revisión: {Seconds}s)...",
            _options.PollingIntervalSeconds);

        try
        {
            await _syncService.InitializeLocalDatabaseAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al inicializar la base de datos local de respaldo.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _syncService.ExecuteSyncCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ocurrió un error durante el ciclo de sincronización de traspasos.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Agente de Traqueo de Traspasos Kurai detenido.");
    }
}
