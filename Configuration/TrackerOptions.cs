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
    /// Carpeta local donde se guardan los archivos JSON generados por cada lote de traspasos.
    /// </summary>
    public string JsonOutputDirectory { get; set; } = @"c:\Proyecto3\KuraiTransferTracker\Payloads";

    /// <summary>
    /// URL del endpoint API (POST) para enviar automáticamente el body JSON cuando haya un endpoint activo.
    /// Si está vacío, solo guarda el JSON en la base de datos local y en la carpeta Payloads.
    /// </summary>
    public string ApiEndpointUrl { get; set; } = "";

    /// <summary>
    /// Token Bearer o API Key opcional para autenticación al enviar el JSON.
    /// </summary>
    public string ApiBearerToken { get; set; } = "";
}
