namespace KuraiTransferTracker.Configuration;

public sealed class TrackerOptions
{
    public const string SectionName = "TrackerSettings";

    /// <summary>
    /// Cadena de conexión de SOLO LECTURA hacia la base de datos de producción [KURAI2026].
    /// </summary>
    public string SourceConnectionString { get; set; } =
        "Server=localhost;Database=KURAI2026;Trusted_Connection=True;TrustServerCertificate=True;ApplicationIntent=ReadOnly;";

    /// <summary>
    /// Cadena de conexión a [master] para verificar/crear la base de datos local de respaldo al arrancar.
    /// </summary>
    public string MasterConnectionString { get; set; } =
        "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>
    /// Nombre de la base de datos local donde se respaldarán los traspasos y el conteo por sucursal.
    /// </summary>
    public string LocalDatabaseName { get; set; } = "KuraiTrackingDB";

    /// <summary>
    /// Cadena de conexión hacia la base de datos local de respaldo [KuraiTrackingDB].
    /// </summary>
    public string LocalBackupConnectionString { get; set; } =
        "Server=localhost;Database=KuraiTrackingDB;Trusted_Connection=True;TrustServerCertificate=True;";

    /// <summary>
    /// Intervalo en segundos para revisar si hay nuevos traspasos en [KURAI2026].
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Si es true, "external_id" usa el Folio (ej. "TAG2093"); si es false, usa el DocumentID (ej. "58968").
    /// </summary>
    public bool UseFolioAsExternalId { get; set; } = true;

    /// <summary>
    /// Si es false, "product_id" usa ProductID (ej. "125"); si es true, usa ClaveProducto (ej. "15096").
    /// </summary>
    public bool UseProductKeyAsProductId { get; set; } = false;

    /// <summary>
    /// URL base de la API receptora (ej. "http://192.168.1.252:8000").
    /// </summary>
    public string ApiBaseUrl { get; set; } = "http://192.168.1.252:8000";

    /// <summary>
    /// Ruta del endpoint de sincronización (ej. "/daily-sales/sync").
    /// </summary>
    public string SyncEndpoint { get; set; } = "/daily-sales/sync";

    /// <summary>
    /// Clave del agente para autenticación en la API receptora.
    /// </summary>
    public string AgentApiKey { get; set; } = "replace_with_a_long_random_agent_key";

    /// <summary>
    /// Fecha inicial a partir de la cual se extraen los traspasos (ej. "2026-08-01T00:00:00").
    /// </summary>
    public DateTime StartDate { get; set; } = new DateTime(2026, 8, 1, 0, 0, 0);

    /// <summary>
    /// Cantidad máxima de traspasos por lote al enviar por HTTP POST a la API.
    /// </summary>
    public int ApiBatchSize { get; set; } = 100;

    /// <summary>
    /// Construye la URL completa combinando ApiBaseUrl y SyncEndpoint.
    /// </summary>
    public string GetFullEndpointUrl()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl))
        {
            return string.Empty;
        }

        string baseUrl = ApiBaseUrl.TrimEnd('/');
        string endpoint = string.IsNullOrWhiteSpace(SyncEndpoint)
            ? string.Empty
            : "/" + SyncEndpoint.TrimStart('/');

        return baseUrl + endpoint;
    }
}
