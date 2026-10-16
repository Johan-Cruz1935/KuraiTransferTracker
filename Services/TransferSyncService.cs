using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using KuraiTransferTracker.Configuration;
using KuraiTransferTracker.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KuraiTransferTracker.Services;

public sealed class TransferSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TrackerOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TransferSyncService> _logger;

    public TransferSyncService(
        IOptions<TrackerOptions> options,
        HttpClient httpClient,
        ILogger<TransferSyncService> logger)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Crea la base de datos local [KuraiTrackingDB] y las tablas:
    /// 1. [dbo.TraqueoTraspasos]
    /// 2. [dbo.ConteoPorSucursal]
    /// 3. [dbo.PayloadsJsonTraspasos] (Guarda el body JSON exacto generado por traspaso y su estatus de envío)
    /// </summary>
    public async Task InitializeLocalDatabaseAsync(CancellationToken cancellationToken)
    {
        // 1. Verificar o crear la base de datos local en SQL Server
        await using (var masterConn = new SqlConnection(_options.MasterConnectionString))
        {
            await masterConn.OpenAsync(cancellationToken);
            string createDbSql = $@"
                IF DB_ID(N'{_options.LocalDatabaseName}') IS NULL
                BEGIN
                    CREATE DATABASE [{_options.LocalDatabaseName}];
                END";

            await using var cmd = new SqlCommand(createDbSql, masterConn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. Verificar o crear las tablas en la BD local
        await using (var localConn = new SqlConnection(_options.LocalBackupConnectionString))
        {
            await localConn.OpenAsync(cancellationToken);

            const string createTablesSql = @"
                IF OBJECT_ID(N'dbo.TraqueoTraspasos', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TraqueoTraspasos (
                        DocumentItemID      BIGINT         NOT NULL PRIMARY KEY,
                        DocumentID          BIGINT         NOT NULL,
                        OrigenID            BIGINT         NOT NULL,
                        OrigenNombre        NVARCHAR(150)  NOT NULL,
                        DestinoID           BIGINT         NOT NULL,
                        DestinoNombre       NVARCHAR(150)  NOT NULL,
                        ProductID           BIGINT         NOT NULL,
                        ClaveProducto       NVARCHAR(60)   NOT NULL,
                        Cantidad            FLOAT          NOT NULL,
                        Unidad              NVARCHAR(50)   NOT NULL,
                        Folio               NVARCHAR(100)  NOT NULL,
                        Fecha               DATETIME2      NOT NULL,
                        UsuarioCreadorID    BIGINT         NOT NULL,
                        FechaRespaldo       DATETIME2      NOT NULL DEFAULT SYSDATETIME()
                    );

                    CREATE INDEX IX_TraqueoTraspasos_DocumentID ON dbo.TraqueoTraspasos(DocumentID);
                    CREATE INDEX IX_TraqueoTraspasos_DestinoID ON dbo.TraqueoTraspasos(DestinoID, DocumentID);
                END;

                IF OBJECT_ID(N'dbo.ConteoPorSucursal', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.ConteoPorSucursal (
                        SucursalID          BIGINT         NOT NULL PRIMARY KEY,
                        SucursalNombre      NVARCHAR(150)  NOT NULL,
                        TotalTraspasos      INT            NOT NULL,
                        TotalPartidas       INT            NOT NULL,
                        UltimoDocumentID    BIGINT         NOT NULL,
                        UltimoFolio         NVARCHAR(100)  NOT NULL,
                        UltimaFechaTraspaso DATETIME2      NOT NULL,
                        FechaActualizacion  DATETIME2      NOT NULL DEFAULT SYSDATETIME()
                    );
                END;

                IF OBJECT_ID(N'dbo.PayloadsJsonTraspasos', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.PayloadsJsonTraspasos (
                        DocumentID          BIGINT         NOT NULL PRIMARY KEY,
                        ExternalID          NVARCHAR(100)  NOT NULL,
                        OrigenID            BIGINT         NOT NULL,
                        DestinoID           BIGINT         NOT NULL,
                        DestinoNombre       NVARCHAR(150)  NOT NULL,
                        FechaNegocio        NVARCHAR(20)   NOT NULL,
                        TotalItems          INT            NOT NULL,
                        JsonBody            NVARCHAR(MAX)  NOT NULL,
                        EnviadoApi          BIT            NOT NULL DEFAULT 0,
                        CodigoRespuestaHttp INT            NULL,
                        FechaGeneracion     DATETIME2      NOT NULL DEFAULT SYSDATETIME(),
                        FechaEnvioApi       DATETIME2      NULL
                    );

                    CREATE INDEX IX_PayloadsJson_DestinoID ON dbo.PayloadsJsonTraspasos(DestinoID, DocumentID DESC);
                END;";

            await using var cmd = new SqlCommand(createTablesSql, localConn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.JsonOutputDirectory))
        {
            Directory.CreateDirectory(_options.JsonOutputDirectory);
        }

        _logger.LogInformation("Base de datos local [{DbName}] y tablas verificadas correctamente.", _options.LocalDatabaseName);
    }

    /// <summary>
    /// Ejecuta un ciclo de detección incremental:
    /// 1. Obtiene el último DocumentID respaldado en la BD local.
    /// 2. Consulta en [KURAI2026] (SOLO LECTURA) si existen nuevos traspasos.
    /// 3. Si existen, los guarda, actualiza el conteo por sucursal, genera el Body JSON y lo envía/guarda.
    /// </summary>
    public async Task<int> ExecuteSyncCycleAsync(CancellationToken cancellationToken)
    {
        long ultimoDocumentId = await GetLastBackedUpDocumentIdAsync(cancellationToken);

        List<TransferRecord> nuevosRegistros = await ReadNewTransfersFromKuraiAsync(ultimoDocumentId, cancellationToken);

        if (nuevosRegistros.Count > 0)
        {
            int nuevosTraspasosUnicos = nuevosRegistros.Select(r => r.DocumentID).Distinct().Count();
            long nuevoMaxDocumentId = nuevosRegistros.Max(r => r.DocumentID);

            await SaveTransfersAndUpdateBranchCountsAsync(nuevosRegistros, cancellationToken);
            await GenerateSaveAndSendJsonPayloadsAsync(nuevosRegistros, cancellationToken);

            _logger.LogInformation(
                "Se respaldaron y procesaron {TraspasosCount} traspasos nuevos ({PartidasCount} partidas). Nuevo Último DocumentID: {MaxId}.",
                nuevosTraspasosUnicos,
                nuevosRegistros.Count,
                nuevoMaxDocumentId);

            return nuevosTraspasosUnicos;
        }

        // Si ya había traspasos en TraqueoTraspasos pero aún no estaban en PayloadsJsonTraspasos, los genera automáticamente
        int backfilled = await EnsureMissingJsonPayloadsGeneratedAsync(cancellationToken);
        if (backfilled == 0)
        {
            _logger.LogInformation(
                "Sin nuevos traspasos detectados (Último DocumentID respaldado: {UltimoId}).",
                ultimoDocumentId);
        }

        return backfilled;
    }

    /// <summary>
    /// Construye el objeto DTO con el formato exacto solicitado por tu colega:
    /// { "transfers": [ { "external_id": "...", "business_date": "yyyy-MM-dd", "origin_warehouse_id": "1", "destination_warehouse_id": "8", "items": [ ... ] } ] }
    /// </summary>
    public TransferBatchPayloadDto BuildTransferBatchPayload(IEnumerable<TransferRecord> records)
    {
        var transfers = records
            .GroupBy(r => r.DocumentID)
            .OrderBy(g => g.Key)
            .Select(group =>
            {
                TransferRecord first = group.First();
                string externalId = _options.UseFolioAsExternalId && !string.IsNullOrWhiteSpace(first.Folio)
                    ? first.Folio
                    : first.DocumentID.ToString(CultureInfo.InvariantCulture);

                return new TransferDto
                {
                    ExternalId = externalId,
                    BusinessDate = first.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    OriginWarehouseId = first.OrigenID.ToString(CultureInfo.InvariantCulture),
                    DestinationWarehouseId = first.DestinoID.ToString(CultureInfo.InvariantCulture),
                    Items = group
                        .OrderBy(i => i.DocumentItemID)
                        .Select(item => new TransferItemDto
                        {
                            ProductId = _options.UseProductKeyAsProductId && !string.IsNullOrWhiteSpace(item.ClaveProducto)
                                ? item.ClaveProducto
                                : item.ProductID.ToString(CultureInfo.InvariantCulture),
                            Quantity = item.Cantidad.ToString("0.0000", CultureInfo.InvariantCulture)
                        })
                        .ToList()
                };
            })
            .ToList();

        return new TransferBatchPayloadDto
        {
            Transfers = transfers
        };
    }

    /// <summary>
    /// Genera, guarda en BD local, escribe archivo JSON en disco y (si hay ApiEndpointUrl configurada) envía por HTTP POST.
    /// </summary>
    private async Task GenerateSaveAndSendJsonPayloadsAsync(
        List<TransferRecord> records,
        CancellationToken cancellationToken)
    {
        // 1. Guardar cada traspaso con su JSON individual en dbo.PayloadsJsonTraspasos
        using var payloadTable = new DataTable();
        payloadTable.Columns.Add("DocumentID", typeof(long));
        payloadTable.Columns.Add("ExternalID", typeof(string));
        payloadTable.Columns.Add("OrigenID", typeof(long));
        payloadTable.Columns.Add("DestinoID", typeof(long));
        payloadTable.Columns.Add("DestinoNombre", typeof(string));
        payloadTable.Columns.Add("FechaNegocio", typeof(string));
        payloadTable.Columns.Add("TotalItems", typeof(int));
        payloadTable.Columns.Add("JsonBody", typeof(string));
        payloadTable.Columns.Add("EnviadoApi", typeof(bool));
        payloadTable.Columns.Add("CodigoRespuestaHttp", typeof(int));
        payloadTable.Columns.Add("FechaGeneracion", typeof(DateTime));
        payloadTable.Columns.Add("FechaEnvioApi", typeof(DateTime));

        DateTime now = DateTime.Now;

        foreach (IGrouping<long, TransferRecord> docGroup in records.GroupBy(r => r.DocumentID).OrderBy(g => g.Key))
        {
            TransferRecord first = docGroup.First();
            TransferBatchPayloadDto singlePayload = BuildTransferBatchPayload(docGroup);
            string jsonBody = JsonSerializer.Serialize(singlePayload, JsonOptions);

            string externalId = singlePayload.Transfers[0].ExternalId;
            string businessDate = singlePayload.Transfers[0].BusinessDate;

            payloadTable.Rows.Add(
                first.DocumentID,
                externalId,
                first.OrigenID,
                first.DestinoID,
                first.DestinoNombre,
                businessDate,
                docGroup.Count(),
                jsonBody,
                false,
                DBNull.Value,
                now,
                DBNull.Value);
        }

        await using (var localConn = new SqlConnection(_options.LocalBackupConnectionString))
        {
            await localConn.OpenAsync(cancellationToken);
            using var bulkCopy = new SqlBulkCopy(localConn)
            {
                DestinationTableName = "dbo.PayloadsJsonTraspasos",
                BatchSize = 1000,
                BulkCopyTimeout = 60
            };

            foreach (DataColumn col in payloadTable.Columns)
            {
                bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            }

            await bulkCopy.WriteToServerAsync(payloadTable, cancellationToken);
        }

        // 2. Guardar archivos JSON en la carpeta Payloads (un archivo global de últimos traspasos y uno por sucursal)
        if (!string.IsNullOrWhiteSpace(_options.JsonOutputDirectory))
        {
            Directory.CreateDirectory(_options.JsonOutputDirectory);

            // A) Archivo con los últimos 5 traspasos generales como muestra lista para usar
            var recentDocs = records
                .GroupBy(r => r.DocumentID)
                .OrderByDescending(g => g.Key)
                .Take(5)
                .SelectMany(g => g)
                .ToList();

            TransferBatchPayloadDto sampleBatch = BuildTransferBatchPayload(recentDocs);
            string sampleBatchJson = JsonSerializer.Serialize(sampleBatch, JsonOptions);
            string latestFilePath = Path.Combine(_options.JsonOutputDirectory, "ultimos_traspasos_body.json");
            await File.WriteAllTextAsync(latestFilePath, sampleBatchJson, Encoding.UTF8, cancellationToken);

            // B) Archivos JSON separados por sucursal (ej. sucursal_4_Agua_Dulce.json, sucursal_8_Tera.json)
            foreach (IGrouping<long, TransferRecord> branchGroup in records.GroupBy(r => r.DestinoID))
            {
                TransferRecord firstBranch = branchGroup.First();
                string safeName = string.Concat(firstBranch.DestinoNombre.Split(Path.GetInvalidFileNameChars()))
                    .Replace(' ', '_');

                var latestBranchDocs = branchGroup
                    .GroupBy(r => r.DocumentID)
                    .OrderByDescending(g => g.Key)
                    .Take(10)
                    .SelectMany(g => g)
                    .ToList();

                TransferBatchPayloadDto branchPayload = BuildTransferBatchPayload(latestBranchDocs);
                string branchJson = JsonSerializer.Serialize(branchPayload, JsonOptions);
                string branchFilePath = Path.Combine(
                    _options.JsonOutputDirectory,
                    $"sucursal_{firstBranch.DestinoID}_{safeName}.json");

                await File.WriteAllTextAsync(branchFilePath, branchJson, Encoding.UTF8, cancellationToken);
            }
        }

        // 3. Si hay una URL de API configurada (ApiEndpointUrl), enviar el lote por HTTP POST
        if (!string.IsNullOrWhiteSpace(_options.ApiEndpointUrl))
        {
            await SendBatchToApiAsync(records, cancellationToken);
        }
    }

    /// <summary>
    /// Envía el JSON por HTTP POST al endpoint configurado en ApiEndpointUrl y actualiza el estatus en BD local.
    /// </summary>
    private async Task SendBatchToApiAsync(List<TransferRecord> records, CancellationToken cancellationToken)
    {
        TransferBatchPayloadDto batchDto = BuildTransferBatchPayload(records);
        string jsonPayload = JsonSerializer.Serialize(batchDto, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApiEndpointUrl)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiBearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiBearerToken);
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            int statusCode = (int)response.StatusCode;
            bool success = response.IsSuccessStatusCode;

            // Actualizar estatus de envío en la tabla local PayloadsJsonTraspasos
            await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
            await localConn.OpenAsync(cancellationToken);

            long minDocId = records.Min(r => r.DocumentID);
            long maxDocId = records.Max(r => r.DocumentID);

            const string updateStatusSql = @"
                UPDATE dbo.PayloadsJsonTraspasos
                SET EnviadoApi = @Enviado,
                    CodigoRespuestaHttp = @StatusCode,
                    FechaEnvioApi = SYSDATETIME()
                WHERE DocumentID BETWEEN @MinDocId AND @MaxDocId;";

            await using var cmd = new SqlCommand(updateStatusSql, localConn);
            cmd.Parameters.AddWithValue("@Enviado", success);
            cmd.Parameters.AddWithValue("@StatusCode", statusCode);
            cmd.Parameters.AddWithValue("@MinDocId", minDocId);
            cmd.Parameters.AddWithValue("@MaxDocId", maxDocId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation(
                "Body JSON enviado a {Url} con código HTTP {StatusCode}.",
                _options.ApiEndpointUrl,
                statusCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enviar el Body JSON al endpoint {Url}.", _options.ApiEndpointUrl);
        }
    }

    /// <summary>
    /// Si existen registros en TraqueoTraspasos que aún no tienen su JSON en PayloadsJsonTraspasos, los genera.
    /// </summary>
    private async Task<int> EnsureMissingJsonPayloadsGeneratedAsync(CancellationToken cancellationToken)
    {
        const string missingQuery = @"
            SELECT 
                t.DocumentID,
                t.DocumentItemID,
                t.OrigenID,
                t.OrigenNombre,
                t.DestinoID,
                t.DestinoNombre,
                t.ProductID,
                t.ClaveProducto,
                t.Cantidad,
                t.Unidad,
                t.Folio,
                t.Fecha,
                t.UsuarioCreadorID
            FROM dbo.TraqueoTraspasos t
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.PayloadsJsonTraspasos p WHERE p.DocumentID = t.DocumentID
            )
            ORDER BY t.DocumentID ASC, t.DocumentItemID ASC;";

        var missingRecords = new List<TransferRecord>();

        await using (var localConn = new SqlConnection(_options.LocalBackupConnectionString))
        {
            await localConn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand(missingQuery, localConn);
            await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                missingRecords.Add(new TransferRecord
                {
                    DocumentID = reader.GetInt64(0),
                    DocumentItemID = reader.GetInt64(1),
                    OrigenID = reader.GetInt64(2),
                    OrigenNombre = reader.GetString(3),
                    DestinoID = reader.GetInt64(4),
                    DestinoNombre = reader.GetString(5),
                    ProductID = reader.GetInt64(6),
                    ClaveProducto = reader.GetString(7),
                    Cantidad = reader.GetDouble(8),
                    Unidad = reader.GetString(9),
                    Folio = reader.GetString(10),
                    Fecha = reader.GetDateTime(11),
                    UsuarioCreadorID = reader.GetInt64(12)
                });
            }
        }

        if (missingRecords.Count == 0)
        {
            return 0;
        }

        await GenerateSaveAndSendJsonPayloadsAsync(missingRecords, cancellationToken);
        int docCount = missingRecords.Select(r => r.DocumentID).Distinct().Count();
        _logger.LogInformation(
            "Se generaron {Count} bodies JSON para los traspasos almacenados en [dbo.PayloadsJsonTraspasos] y en la carpeta Payloads.",
            docCount);

        return docCount;
    }

    private async Task<long> GetLastBackedUpDocumentIdAsync(CancellationToken cancellationToken)
    {
        await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
        await localConn.OpenAsync(cancellationToken);

        const string sql = "SELECT ISNULL(MAX(DocumentID), 0) FROM dbo.TraqueoTraspasos;";
        await using var cmd = new SqlCommand(sql, localConn);
        object? result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result ?? 0L);
    }

    private async Task<List<TransferRecord>> ReadNewTransfersFromKuraiAsync(
        long ultimoDocumentId,
        CancellationToken cancellationToken)
    {
        const string selectQuery = @"
            SELECT 
                d.DocumentID,                                          -- ID del documento de traspaso
                di.DocumentItemID,                                     -- ID de la partida / renglón
                d.DepotIDFrom                       AS OrigenID,       -- ID del Almacén/Sucursal Origen
                ISNULL(dOrig.DepotName, '')         AS OrigenNombre,
                d.DepotIDTo                         AS DestinoID,      -- ID del Almacén/Sucursal Destino
                ISNULL(dDest.DepotName, '')         AS DestinoNombre,
                ISNULL(di.ProductID, 0)             AS ProductID,      -- ID del Producto
                ISNULL(di.ProductKey, '')           AS ClaveProducto,
                ISNULL(di.Quantity, 0)              AS Cantidad,
                ISNULL(di.Unit, '')                 AS Unidad,
                ISNULL(d.FolioPrefix, '') + ISNULL(d.Folio, '') AS Folio,
                d.DateDocument                      AS Fecha,
                ISNULL(d.CreatedBy, 0)              AS UsuarioCreadorID
            FROM dbo.docDocument d WITH (NOLOCK)
            INNER JOIN dbo.orgDepot dOrig WITH (NOLOCK) 
                ON d.DepotIDFrom = dOrig.DepotID
            INNER JOIN dbo.orgDepot dDest WITH (NOLOCK) 
                ON d.DepotIDTo = dDest.DepotID
            INNER JOIN dbo.docDocumentItem di WITH (NOLOCK) 
                ON d.DocumentID = di.DocumentID
            WHERE d.ModuleID = 204                                     -- Movimiento Entre Almacenes
              AND d.CancelledOn IS NULL
              AND d.DeletedOn IS NULL
              AND di.DeletedOn IS NULL
              AND d.DocumentID > @UltimoDocumentID
            ORDER BY d.DocumentID ASC, di.DocumentItemID ASC;";

        var results = new List<TransferRecord>();

        await using var sourceConn = new SqlConnection(_options.SourceConnectionString);
        await sourceConn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(selectQuery, sourceConn)
        {
            CommandTimeout = 60
        };
        cmd.Parameters.Add(new SqlParameter("@UltimoDocumentID", SqlDbType.BigInt) { Value = ultimoDocumentId });

        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TransferRecord
            {
                DocumentID = reader.GetInt64(0),
                DocumentItemID = reader.GetInt64(1),
                OrigenID = reader.GetInt64(2),
                OrigenNombre = reader.GetString(3),
                DestinoID = reader.GetInt64(4),
                DestinoNombre = reader.GetString(5),
                ProductID = reader.GetInt64(6),
                ClaveProducto = reader.GetString(7),
                Cantidad = reader.GetDouble(8),
                Unidad = reader.GetString(9),
                Folio = reader.GetString(10),
                Fecha = reader.GetDateTime(11),
                UsuarioCreadorID = reader.GetInt64(12)
            });
        }

        return results;
    }

    private async Task SaveTransfersAndUpdateBranchCountsAsync(
        List<TransferRecord> newRecords,
        CancellationToken cancellationToken)
    {
        using var dataTable = new DataTable();
        dataTable.Columns.Add("DocumentItemID", typeof(long));
        dataTable.Columns.Add("DocumentID", typeof(long));
        dataTable.Columns.Add("OrigenID", typeof(long));
        dataTable.Columns.Add("OrigenNombre", typeof(string));
        dataTable.Columns.Add("DestinoID", typeof(long));
        dataTable.Columns.Add("DestinoNombre", typeof(string));
        dataTable.Columns.Add("ProductID", typeof(long));
        dataTable.Columns.Add("ClaveProducto", typeof(string));
        dataTable.Columns.Add("Cantidad", typeof(double));
        dataTable.Columns.Add("Unidad", typeof(string));
        dataTable.Columns.Add("Folio", typeof(string));
        dataTable.Columns.Add("Fecha", typeof(DateTime));
        dataTable.Columns.Add("UsuarioCreadorID", typeof(long));
        dataTable.Columns.Add("FechaRespaldo", typeof(DateTime));

        DateTime now = DateTime.Now;
        foreach (TransferRecord r in newRecords)
        {
            dataTable.Rows.Add(
                r.DocumentItemID,
                r.DocumentID,
                r.OrigenID,
                r.OrigenNombre,
                r.DestinoID,
                r.DestinoNombre,
                r.ProductID,
                r.ClaveProducto,
                r.Cantidad,
                r.Unidad,
                r.Folio,
                r.Fecha,
                r.UsuarioCreadorID,
                now);
        }

        await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
        await localConn.OpenAsync(cancellationToken);

        await using SqlTransaction transaction = (SqlTransaction)await localConn.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var bulkCopy = new SqlBulkCopy(localConn, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = "dbo.TraqueoTraspasos";
                bulkCopy.BatchSize = 2000;
                bulkCopy.BulkCopyTimeout = 60;

                foreach (DataColumn col in dataTable.Columns)
                {
                    bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                }

                await bulkCopy.WriteToServerAsync(dataTable, cancellationToken);
            }

            const string updateBranchCountsSql = @"
                MERGE dbo.ConteoPorSucursal AS target
                USING (
                    SELECT 
                        t.DestinoID                                     AS SucursalID,
                        MAX(t.DestinoNombre)                            AS SucursalNombre,
                        COUNT(DISTINCT t.DocumentID)                    AS TotalTraspasos,
                        COUNT(t.DocumentItemID)                         AS TotalPartidas,
                        MAX(t.DocumentID)                               AS UltimoDocumentID,
                        (
                            SELECT TOP 1 sub.Folio 
                            FROM dbo.TraqueoTraspasos sub 
                            WHERE sub.DestinoID = t.DestinoID 
                            ORDER BY sub.DocumentID DESC
                        )                                               AS UltimoFolio,
                        MAX(t.Fecha)                                    AS UltimaFechaTraspaso
                    FROM dbo.TraqueoTraspasos t
                    GROUP BY t.DestinoID
                ) AS source
                ON target.SucursalID = source.SucursalID
                WHEN MATCHED THEN
                    UPDATE SET 
                        target.SucursalNombre      = source.SucursalNombre,
                        target.TotalTraspasos      = source.TotalTraspasos,
                        target.TotalPartidas       = source.TotalPartidas,
                        target.UltimoDocumentID    = source.UltimoDocumentID,
                        target.UltimoFolio         = source.UltimoFolio,
                        target.UltimaFechaTraspaso = source.UltimaFechaTraspaso,
                        target.FechaActualizacion  = SYSDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (
                        SucursalID,
                        SucursalNombre,
                        TotalTraspasos,
                        TotalPartidas,
                        UltimoDocumentID,
                        UltimoFolio,
                        UltimaFechaTraspaso,
                        FechaActualizacion
                    )
                    VALUES (
                        source.SucursalID,
                        source.SucursalNombre,
                        source.TotalTraspasos,
                        source.TotalPartidas,
                        source.UltimoDocumentID,
                        source.UltimoFolio,
                        source.UltimaFechaTraspaso,
                        SYSDATETIME()
                    );";

            await using (var updateCmd = new SqlCommand(updateBranchCountsSql, localConn, transaction))
            {
                await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
