using KuraiTransferTracker;
using KuraiTransferTracker.Configuration;
using KuraiTransferTracker.Services;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Habilita la ejecución nativa como Servicio de Windows (compatible con sc.exe y consola)
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "KuraiTransferTracker";
});

// Configuración desde appsettings.json
builder.Services.Configure<TrackerOptions>(
    builder.Configuration.GetSection(TrackerOptions.SectionName));

// Registro de HttpClient, servicio de sincronización y el Worker en segundo plano
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<TransferSyncService>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();

// Modo opcional "--run-once" para pruebas rápidas desde terminal
if (args.Contains("--run-once", StringComparer.OrdinalIgnoreCase))
{
    var syncService = host.Services.GetRequiredService<TransferSyncService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await syncService.InitializeLocalDatabaseAsync(cts.Token);
    await syncService.ExecuteSyncCycleAsync(cts.Token);
    return;
}

await host.RunAsync();
