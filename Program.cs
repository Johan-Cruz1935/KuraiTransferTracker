using KuraiTransferTracker;
using KuraiTransferTracker.Configuration;
using KuraiTransferTracker.Logging;
using KuraiTransferTracker.Services;

// Asegura que cuando corra como Servicio de Windows busque appsettings.yml y la carpeta logs/ junto al ejecutable
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Carga la configuración desde appsettings.yml
builder.Configuration.AddYamlFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.yml"),
    optional: false,
    reloadOnChange: true);

// Habilita el guardado de trazabilidad diaria en la carpeta "logs/" junto al ejecutable
string logsFolderPath = Path.Combine(AppContext.BaseDirectory, "logs");
builder.Logging.AddProvider(new DailyFileLoggerProvider(logsFolderPath));

// Habilita la ejecución nativa como Servicio de Windows
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "KuraiTransferTracker";
});

// Configuración desde la sección TrackerSettings de appsettings.yml
builder.Services.Configure<TrackerOptions>(
    builder.Configuration.GetSection(TrackerOptions.SectionName));

// Registro de HttpClient, servicio de sincronización y el Worker en segundo plano
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<TransferSyncService>();
builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();

// Modo opcional "--run-once" para ejecutar un ciclo único manual
if (args.Contains("--run-once", StringComparer.OrdinalIgnoreCase))
{
    var syncService = host.Services.GetRequiredService<TransferSyncService>();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    await syncService.InitializeLocalDatabaseAsync(cts.Token);
    await syncService.ExecuteSyncCycleAsync(cts.Token);
    return;
}

await host.RunAsync();
