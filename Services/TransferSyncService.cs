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
        WriteIndented = false,
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
    /// Crea la base de datos local [KuraiTrackingDB] y sus 3 tablas de producción si no existen:
    /// 1. [dbo.TraqueoTraspasos]
    /// 2. [dbo.ConteoPorSucursal]
    /// 3. [dbo.PayloadsJsonTraspasos]
    /// </summary>
    public async Task InitializeLocalDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.MasterConnectionString))
        {
            try
            {
                await using var masterConn = new SqlConnection(_options.MasterConnectionString);
                await masterConn.OpenAsync(cancellationToken);
                string createDbSql = $@"
                    IF DB_ID(N'{_options.LocalDatabaseName}') IS NULL
                    BEGIN
                        CREATE DATABASE [{_options.LocalDatabaseName}];
                    END";

                await using var cmd = new SqlCommand(createDbSql, masterConn);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex)
            {
                _logger.LogDebug(
                    ex,
                    "Omitiendo verificación en master (usuario con permisos acotados a [{DbName}]).",
                    _options.LocalDatabaseName);
            }
        }

        await using (var localConn = new SqlConnection(_options.LocalBackupConnectionString))
        {
            await localConn.OpenAsync(cancellationToken);

            const string createTablesSql = @"
                IF OBJECT_ID(N'dbo.TraqueoTraspasos', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TraqueoTraspasos (
                        DocumentItemID      BIGINT         NOT NULL PRIMARY KEY,
                        DocumentID          BIGINT         NOT NULL,
                        ModuleID            INT            NOT NULL DEFAULT 204,
                        OrigenID            BIGINT         NOT NULL,
                        OrigenNombre        NVARCHAR(150)  NOT NULL,
                        DestinoID           BIGINT         NOT NULL,
                        DestinoNombre       NVARCHAR(150)  NOT NULL,
                        ProductID           BIGINT         NOT NULL,
                        ClaveProducto       NVARCHAR(60)   NOT NULL,
                        Cantidad            FLOAT          NOT NULL,
                        Unidad              NVARCHAR(50)   NOT NULL,
                        PrecioUnitario      FLOAT          NOT NULL CONSTRAINT DF_TraqueoTraspasos_PrecioUnitario DEFAULT (0),
                        ImporteTotal        FLOAT          NOT NULL CONSTRAINT DF_TraqueoTraspasos_ImporteTotal DEFAULT (0),
                        Folio               NVARCHAR(100)  NOT NULL,
                        Fecha               DATETIME2      NOT NULL,
                        UsuarioCreadorID    BIGINT         NOT NULL,
                        FechaRespaldo       DATETIME2      NOT NULL DEFAULT SYSDATETIME()
                    );

                    CREATE INDEX IX_TraqueoTraspasos_DocumentID ON dbo.TraqueoTraspasos(DocumentID);
                    CREATE INDEX IX_TraqueoTraspasos_DestinoID ON dbo.TraqueoTraspasos(DestinoID, DocumentID);
                END
                ELSE
                BEGIN
                    IF COL_LENGTH('dbo.TraqueoTraspasos', 'ModuleID') IS NULL
                    BEGIN
                        ALTER TABLE dbo.TraqueoTraspasos ADD ModuleID INT NOT NULL CONSTRAINT DF_TraqueoTraspasos_ModuleID DEFAULT (204);
                    END;

                    IF COL_LENGTH('dbo.TraqueoTraspasos', 'PrecioUnitario') IS NULL
                    BEGIN
                        ALTER TABLE dbo.TraqueoTraspasos ADD PrecioUnitario FLOAT NOT NULL CONSTRAINT DF_TraqueoTraspasos_PrecioUnitario DEFAULT (0);
                    END;

                    IF COL_LENGTH('dbo.TraqueoTraspasos', 'ImporteTotal') IS NULL
                    BEGIN
                        ALTER TABLE dbo.TraqueoTraspasos ADD ImporteTotal FLOAT NOT NULL CONSTRAINT DF_TraqueoTraspasos_ImporteTotal DEFAULT (0);
                    END;
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
                        ModuleID            INT            NOT NULL DEFAULT 204,
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

                    CREATE INDEX IX_PayloadsJson_Pendientes ON dbo.PayloadsJsonTraspasos(EnviadoApi, ModuleID, DocumentID ASC);
                    CREATE INDEX IX_PayloadsJson_DestinoID ON dbo.PayloadsJsonTraspasos(DestinoID, DocumentID DESC);
                END
                ELSE IF COL_LENGTH('dbo.PayloadsJsonTraspasos', 'ModuleID') IS NULL
                BEGIN
                    ALTER TABLE dbo.PayloadsJsonTraspasos ADD ModuleID INT NOT NULL CONSTRAINT DF_PayloadsJson_ModuleID DEFAULT (204);
                END;";

            await using var cmd = new SqlCommand(createTablesSql, localConn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Base de datos local [{DbName}] lista para operación.", _options.LocalDatabaseName);
    }

    /// <summary>
    /// Ciclo principal de producción:
    /// 1. Detecta y respalda nuevos documentos desde [KURAI2026] para los módulos:
    ///    - 204  (Movimiento Entre Almacenes - TAG)
    ///    - 1247 (Pedidos Sucursales - Noche)
    ///    - 1257 (Entregas/Remisiones CEDIS - Mañana)
    /// 2. Actualiza el marcador por sucursal y genera los payloads JSON con module_id.
    /// 3. Envía a la API cualquier documento pendiente (EnviadoApi = 0), separado por ModuleID.
    /// </summary>
    public async Task<int> ExecuteSyncCycleAsync(CancellationToken cancellationToken)
    {
        var bounds = await GetDocumentBoundsByModuleAsync(cancellationToken);
        var ultimosPorModulo = bounds.ToDictionary(k => k.Key, v => v.Value.MaxDocId);
        var primerosPorModulo = bounds.ToDictionary(k => k.Key, v => v.Value.MinDocId);

        // 1. Detección y respaldo de nuevos documentos hacia adelante (Tiempo real)
        List<TransferRecord> nuevosRegistros = await ReadNewTransfersFromKuraiAsync(ultimosPorModulo, cancellationToken);

        int nuevosTraspasosCount = 0;
        if (nuevosRegistros.Count > 0)
        {
            nuevosTraspasosCount = nuevosRegistros.Select(r => r.DocumentID).Distinct().Count();
            long nuevoMaxDocumentId = nuevosRegistros.Max(r => r.DocumentID);

            await SaveTransfersUpdateCountsAndStoreJsonAsync(nuevosRegistros, cancellationToken);

            _logger.LogInformation(
                "[Tiempo Real] Se respaldaron {TraspasosCount} documentos nuevos ({PartidasCount} partidas) de módulos [204, 1247, 1257]. Último DocumentID: {MaxId}.",
                nuevosTraspasosCount,
                nuevosRegistros.Count,
                nuevoMaxDocumentId);
        }
        else
        {
            long maxGlobal = ultimosPorModulo.Values.DefaultIfEmpty(0).Max();
            _logger.LogInformation(
                "Ciclo completado: Sin nuevos traspasos/pedidos en tiempo real (Último DocumentID respaldado: {UltimoId}).",
                maxGlobal);
        }

        // 2. Detección y respaldo de documentos históricos hacia atrás (Backfill Inteligente)
        int backfilledCount = await ExecuteBackfillHistoricalAsync(primerosPorModulo, cancellationToken);

        // 3. Enviar a la API todos los documentos pendientes (EnviadoApi = 0) en lotes separados por ModuleID
        await SendPendingTransfersToApiAsync(cancellationToken);

        return nuevosTraspasosCount + backfilledCount;
    }

    /// <summary>
    /// Construye el objeto DTO con el formato exacto de la API incluyendo module_id y sumando cantidades si un mismo product_id viene repetido en varias líneas:
    /// { "transfers": [ { "external_id": "...", "module_id": "1257", "business_date": "yyyy-MM-dd", "origin_warehouse_id": "1", "destination_warehouse_id": "10022", "items": [ ... ] } ] }
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
                    ModuleId = first.ModuleID.ToString(CultureInfo.InvariantCulture),
                    BusinessDate = first.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    OriginWarehouseId = first.OrigenID.ToString(CultureInfo.InvariantCulture),
                    DestinationWarehouseId = first.DestinoID.ToString(CultureInfo.InvariantCulture),
                    Items = group
                        .OrderBy(i => i.DocumentItemID)
                        .GroupBy(item => _options.UseProductKeyAsProductId && !string.IsNullOrWhiteSpace(item.ClaveProducto)
                            ? item.ClaveProducto
                            : item.ProductID.ToString(CultureInfo.InvariantCulture))
                        .Select(prodGroup =>
                        {
                            TransferRecord itemFirst = prodGroup.First();
                            double totalQty = prodGroup.Sum(x => x.Cantidad);
                            double unitPrice = itemFirst.PrecioUnitario;
                            double totalPrice = Math.Round(totalQty * unitPrice, 2);

                            return new TransferItemDto
                            {
                                ProductId = prodGroup.Key,
                                Quantity = totalQty.ToString("0.0000", CultureInfo.InvariantCulture),
                                UnitPrice = unitPrice.ToString("0.0000", CultureInfo.InvariantCulture),
                                TotalPrice = totalPrice.ToString("0.0000", CultureInfo.InvariantCulture)
                            };
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

    private record ModuleDocumentBounds(long MinDocId, long MaxDocId);

    private async Task<Dictionary<int, ModuleDocumentBounds>> GetDocumentBoundsByModuleAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ModuleDocumentBounds>
        {
            [204] = new(0L, 0L),
            [1247] = new(0L, 0L),
            [1257] = new(0L, 0L)
        };

        await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
        await localConn.OpenAsync(cancellationToken);

        const string sql = @"
            SELECT 
                ModuleID, 
                ISNULL(MIN(DocumentID), 0) AS MinDocID,
                ISNULL(MAX(DocumentID), 0) AS MaxDocID
            FROM dbo.TraqueoTraspasos
            GROUP BY ModuleID;";

        await using var cmd = new SqlCommand(sql, localConn);
        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int modId = reader.GetInt32(0);
            long minDocId = reader.GetInt64(1);
            long maxDocId = reader.GetInt64(2);
            result[modId] = new ModuleDocumentBounds(minDocId, maxDocId);
        }

        return result;
    }

    /// <summary>
    /// Relleno Inteligente (Backfill):
    /// Si StartDate es anterior al documento mínimo respaldado en [KuraiTrackingDB],
    /// busca los documentos históricos que faltan en [KURAI2026] (DocumentID < MinDocID) y los importa en lotes,
    /// sin borrar los existentes ni duplicar datos.
    /// </summary>
    private async Task<int> ExecuteBackfillHistoricalAsync(
        Dictionary<int, long> minPorModulo,
        CancellationToken cancellationToken)
    {
        int totalBackfilledDocs = 0;
        int[] modules = [204, 1247, 1257];

        foreach (int moduleId in modules)
        {
            long currentMinId = minPorModulo.GetValueOrDefault(moduleId, 0L);
            if (currentMinId <= 0)
            {
                // Si la tabla no tiene registros previos para este módulo, ReadNewTransfersFromKuraiAsync ya los lee hacia adelante.
                continue;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                List<TransferRecord> batch = await ReadHistoricalBatchFromKuraiAsync(moduleId, currentMinId, 250, cancellationToken);
                if (batch.Count == 0)
                {
                    break;
                }

                int batchDocsCount = batch.Select(r => r.DocumentID).Distinct().Count();
                long batchMinDocId = batch.Min(r => r.DocumentID);
                long batchMaxDocId = batch.Max(r => r.DocumentID);

                await SaveTransfersUpdateCountsAndStoreJsonAsync(batch, cancellationToken);

                totalBackfilledDocs += batchDocsCount;
                _logger.LogInformation(
                    "[Backfill Inteligente] Módulo {ModuleId}: Se importaron {DocsCount} documentos históricos ({ItemsCount} partidas, IDs {MinId}..{MaxId}).",
                    moduleId,
                    batchDocsCount,
                    batch.Count,
                    batchMinDocId,
                    batchMaxDocId);

                // El nuevo límite inferior para la siguiente iteración es el menor DocumentID importado
                currentMinId = batchMinDocId;
            }
        }

        return totalBackfilledDocs;
    }

    /// <summary>
    /// Extrae un lote de documentos históricos anteriores a 'beforeDocumentId' con fecha >= StartDate.
    /// </summary>
    private async Task<List<TransferRecord>> ReadHistoricalBatchFromKuraiAsync(
        int moduleId,
        long beforeDocumentId,
        int maxDocuments,
        CancellationToken cancellationToken)
    {
        const string selectQuery = @"
            WITH DocsCalculados AS (
                SELECT TOP (@BatchLimit)
                    d.DocumentID,
                    d.ModuleID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN d.DepotIDFrom 
                        ELSE 1 
                    END AS OrigenID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN d.DepotIDTo
                        WHEN d.ModuleID = 1247 AND d.DepotID > 1 THEN d.DepotID
                        WHEN d.ModuleID = 1257 AND ped.DepotID > 1 THEN ped.DepotID
                        ELSE CASE d.BusinessEntityID
                            WHEN 73  THEN 4
                            WHEN 72  THEN 5
                            WHEN 70  THEN 6
                            WHEN 74  THEN 7
                            WHEN 66  THEN 8
                            WHEN 69  THEN 9
                            WHEN 68  THEN 10
                            WHEN 71  THEN 18
                            WHEN 99  THEN 21
                            WHEN 109 THEN 10022
                            ELSE d.DepotID
                        END
                    END AS DestinoID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN ISNULL(d.FolioPrefix, '') + ISNULL(d.Folio, '')
                        WHEN d.ModuleID = 1257 THEN 'REM-' + ISNULL(d.FolioPrefix, '') + ISNULL(d.Folio, CAST(d.DocumentID AS NVARCHAR(20)))
                        WHEN d.ModuleID = 1247 AND ISNULL(d.FolioPrefix, '') <> '' THEN d.FolioPrefix + ISNULL(d.Folio, '')
                        ELSE 'P-SUC' + CAST(
                            CASE 
                                WHEN d.DepotID > 1 THEN d.DepotID
                                WHEN d.BusinessEntityID = 73  THEN 4
                                WHEN d.BusinessEntityID = 72  THEN 5
                                WHEN d.BusinessEntityID = 70  THEN 6
                                WHEN d.BusinessEntityID = 74  THEN 7
                                WHEN d.BusinessEntityID = 66  THEN 8
                                WHEN d.BusinessEntityID = 69  THEN 9
                                WHEN d.BusinessEntityID = 68  THEN 10
                                WHEN d.BusinessEntityID = 71  THEN 18
                                WHEN d.BusinessEntityID = 99  THEN 21
                                WHEN d.BusinessEntityID = 109 THEN 10022
                                ELSE d.DepotID
                            END AS NVARCHAR(20)
                        ) + '-' + ISNULL(d.Folio, CAST(d.DocumentID AS NVARCHAR(20)))
                    END AS Folio,
                    d.DateDocument          AS Fecha,
                    ISNULL(d.CreatedBy, 0)  AS UsuarioCreadorID
                FROM dbo.docDocument d WITH (NOLOCK)
                LEFT JOIN dbo.docDocument ped WITH (NOLOCK) 
                    ON d.SourceDocumentID = ped.DocumentID
                WHERE d.CancelledOn IS NULL
                  AND d.DeletedOn IS NULL
                  AND d.DateDocument >= @StartDate
                  AND d.ModuleID = @ModuleID
                  AND d.DocumentID < @BeforeDocumentId
                ORDER BY d.DocumentID DESC
            )
            SELECT 
                dc.DocumentID,
                di.DocumentItemID,
                dc.ModuleID,
                dc.OrigenID,
                ISNULL(dOrig.DepotName, '') AS OrigenNombre,
                dc.DestinoID,
                ISNULL(dDest.DepotName, '') AS DestinoNombre,
                ISNULL(di.ProductID, 0)     AS ProductID,
                ISNULL(di.ProductKey, '')   AS ClaveProducto,
                ISNULL(di.Quantity, 0)      AS Cantidad,
                ISNULL(di.Unit, '')         AS Unidad,
                CAST(ISNULL(u.Costo, 0) AS FLOAT) AS PrecioUnitario,
                CAST(ROUND(ISNULL(di.Quantity, 0) * ISNULL(u.Costo, 0), 2) AS FLOAT) AS ImporteTotal,
                dc.Folio,
                dc.Fecha,
                dc.UsuarioCreadorID
            FROM DocsCalculados dc
            INNER JOIN dbo.orgDepot dOrig WITH (NOLOCK) 
                ON dc.OrigenID = dOrig.DepotID
            INNER JOIN dbo.orgDepot dDest WITH (NOLOCK) 
                ON dc.DestinoID = dDest.DepotID
            INNER JOIN dbo.docDocumentItem di WITH (NOLOCK) 
                ON dc.DocumentID = di.DocumentID
            LEFT JOIN dbo._UltimoCostoProductos u WITH (NOLOCK) 
                ON di.ProductID = u.ProductID
            WHERE di.DeletedOn IS NULL
              AND di.Quantity > 0
              AND dc.DestinoID > 0
            ORDER BY dc.DocumentID ASC, di.DocumentItemID ASC;";

        var results = new List<TransferRecord>();

        await using var sourceConn = new SqlConnection(_options.SourceConnectionString);
        await sourceConn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(selectQuery, sourceConn)
        {
            CommandTimeout = 90
        };
        cmd.Parameters.Add(new SqlParameter("@ModuleID", SqlDbType.Int) { Value = moduleId });
        cmd.Parameters.Add(new SqlParameter("@BeforeDocumentId", SqlDbType.BigInt) { Value = beforeDocumentId });
        cmd.Parameters.Add(new SqlParameter("@BatchLimit", SqlDbType.Int) { Value = maxDocuments });
        cmd.Parameters.Add(new SqlParameter("@StartDate", SqlDbType.DateTime2) { Value = _options.StartDate });

        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TransferRecord
            {
                DocumentID = reader.GetInt64(0),
                DocumentItemID = reader.GetInt64(1),
                ModuleID = reader.GetInt32(2),
                OrigenID = reader.GetInt64(3),
                OrigenNombre = reader.GetString(4),
                DestinoID = reader.GetInt64(5),
                DestinoNombre = reader.GetString(6),
                ProductID = reader.GetInt64(7),
                ClaveProducto = reader.GetString(8),
                Cantidad = reader.GetDouble(9),
                Unidad = reader.GetString(10),
                PrecioUnitario = reader.GetDouble(11),
                ImporteTotal = reader.GetDouble(12),
                Folio = reader.GetString(13),
                Fecha = reader.GetDateTime(14),
                UsuarioCreadorID = reader.GetInt64(15)
            });
        }

        return results;
    }

    /// <summary>
    /// Consulta ESTRICTAMENTE DE SOLO LECTURA hacia [KURAI2026].
    /// Extrae:
    /// - ModuleID = 204  (Movimiento Entre Almacenes - TAG)
    /// - ModuleID = 1247 (Pedidos Sucursales - Noche)
    /// - ModuleID = 1257 (Entregas/Remisiones CEDIS - Mañana)
    /// </summary>
    private async Task<List<TransferRecord>> ReadNewTransfersFromKuraiAsync(
        Dictionary<int, long> ultimosPorModulo,
        CancellationToken cancellationToken)
    {
        const string selectQuery = @"
            WITH DocsCalculados AS (
                SELECT 
                    d.DocumentID,
                    d.ModuleID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN d.DepotIDFrom 
                        ELSE 1 
                    END AS OrigenID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN d.DepotIDTo
                        WHEN d.ModuleID = 1247 AND d.DepotID > 1 THEN d.DepotID
                        WHEN d.ModuleID = 1257 AND ped.DepotID > 1 THEN ped.DepotID
                        ELSE CASE d.BusinessEntityID
                            WHEN 73  THEN 4
                            WHEN 72  THEN 5
                            WHEN 70  THEN 6
                            WHEN 74  THEN 7
                            WHEN 66  THEN 8
                            WHEN 69  THEN 9
                            WHEN 68  THEN 10
                            WHEN 71  THEN 18
                            WHEN 99  THEN 21
                            WHEN 109 THEN 10022
                            ELSE d.DepotID
                        END
                    END AS DestinoID,
                    CASE 
                        WHEN d.ModuleID = 204 THEN ISNULL(d.FolioPrefix, '') + ISNULL(d.Folio, '')
                        WHEN d.ModuleID = 1257 THEN 'REM-' + ISNULL(d.FolioPrefix, '') + ISNULL(d.Folio, CAST(d.DocumentID AS NVARCHAR(20)))
                        WHEN d.ModuleID = 1247 AND ISNULL(d.FolioPrefix, '') <> '' THEN d.FolioPrefix + ISNULL(d.Folio, '')
                        ELSE 'P-SUC' + CAST(
                            CASE 
                                WHEN d.DepotID > 1 THEN d.DepotID
                                WHEN d.BusinessEntityID = 73  THEN 4
                                WHEN d.BusinessEntityID = 72  THEN 5
                                WHEN d.BusinessEntityID = 70  THEN 6
                                WHEN d.BusinessEntityID = 74  THEN 7
                                WHEN d.BusinessEntityID = 66  THEN 8
                                WHEN d.BusinessEntityID = 69  THEN 9
                                WHEN d.BusinessEntityID = 68  THEN 10
                                WHEN d.BusinessEntityID = 71  THEN 18
                                WHEN d.BusinessEntityID = 99  THEN 21
                                WHEN d.BusinessEntityID = 109 THEN 10022
                                ELSE d.DepotID
                            END AS NVARCHAR(20)
                        ) + '-' + ISNULL(d.Folio, CAST(d.DocumentID AS NVARCHAR(20)))
                    END AS Folio,
                    d.DateDocument          AS Fecha,
                    ISNULL(d.CreatedBy, 0)  AS UsuarioCreadorID
                FROM dbo.docDocument d WITH (NOLOCK)
                LEFT JOIN dbo.docDocument ped WITH (NOLOCK) 
                    ON d.SourceDocumentID = ped.DocumentID
                WHERE d.CancelledOn IS NULL
                  AND d.DeletedOn IS NULL
                  AND d.DateDocument >= @StartDate
                  AND (
                      (d.ModuleID = 204  AND d.DocumentID > @UltimoId204)
                      OR (d.ModuleID = 1247 AND d.DocumentID > @UltimoId1247)
                      OR (d.ModuleID = 1257 AND d.DocumentID > @UltimoId1257)
                  )
            )
            SELECT 
                dc.DocumentID,
                di.DocumentItemID,
                dc.ModuleID,
                dc.OrigenID,
                ISNULL(dOrig.DepotName, '') AS OrigenNombre,
                dc.DestinoID,
                ISNULL(dDest.DepotName, '') AS DestinoNombre,
                ISNULL(di.ProductID, 0)     AS ProductID,
                ISNULL(di.ProductKey, '')   AS ClaveProducto,
                ISNULL(di.Quantity, 0)      AS Cantidad,
                ISNULL(di.Unit, '')         AS Unidad,
                CAST(ISNULL(u.Costo, 0) AS FLOAT) AS PrecioUnitario,
                CAST(ROUND(ISNULL(di.Quantity, 0) * ISNULL(u.Costo, 0), 2) AS FLOAT) AS ImporteTotal,
                dc.Folio,
                dc.Fecha,
                dc.UsuarioCreadorID
            FROM DocsCalculados dc
            INNER JOIN dbo.orgDepot dOrig WITH (NOLOCK) 
                ON dc.OrigenID = dOrig.DepotID
            INNER JOIN dbo.orgDepot dDest WITH (NOLOCK) 
                ON dc.DestinoID = dDest.DepotID
            INNER JOIN dbo.docDocumentItem di WITH (NOLOCK) 
                ON dc.DocumentID = di.DocumentID
            LEFT JOIN dbo._UltimoCostoProductos u WITH (NOLOCK)
                ON di.ProductID = u.ProductID
            WHERE di.DeletedOn IS NULL
              AND di.Quantity > 0
              AND dc.DestinoID > 0
            ORDER BY dc.DocumentID ASC, di.DocumentItemID ASC;";

        var results = new List<TransferRecord>();

        await using var sourceConn = new SqlConnection(_options.SourceConnectionString);
        await sourceConn.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(selectQuery, sourceConn)
        {
            CommandTimeout = 60
        };
        cmd.Parameters.Add(new SqlParameter("@UltimoId204", SqlDbType.BigInt) { Value = ultimosPorModulo.GetValueOrDefault(204, 0L) });
        cmd.Parameters.Add(new SqlParameter("@UltimoId1247", SqlDbType.BigInt) { Value = ultimosPorModulo.GetValueOrDefault(1247, 0L) });
        cmd.Parameters.Add(new SqlParameter("@UltimoId1257", SqlDbType.BigInt) { Value = ultimosPorModulo.GetValueOrDefault(1257, 0L) });
        cmd.Parameters.Add(new SqlParameter("@StartDate", SqlDbType.DateTime2) { Value = _options.StartDate });

        await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TransferRecord
            {
                DocumentID = reader.GetInt64(0),
                DocumentItemID = reader.GetInt64(1),
                ModuleID = reader.GetInt32(2),
                OrigenID = reader.GetInt64(3),
                OrigenNombre = reader.GetString(4),
                DestinoID = reader.GetInt64(5),
                DestinoNombre = reader.GetString(6),
                ProductID = reader.GetInt64(7),
                ClaveProducto = reader.GetString(8),
                Cantidad = reader.GetDouble(9),
                Unidad = reader.GetString(10),
                PrecioUnitario = reader.GetDouble(11),
                ImporteTotal = reader.GetDouble(12),
                Folio = reader.GetString(13),
                Fecha = reader.GetDateTime(14),
                UsuarioCreadorID = reader.GetInt64(15)
            });
        }

        return results;
    }

    /// <summary>
    /// Guarda en una sola transacción:
    /// 1. Las partidas en [dbo.TraqueoTraspasos]
    /// 2. El conteo actualizado en [dbo.ConteoPorSucursal]
    /// 3. El payload JSON en [dbo.PayloadsJsonTraspasos]
    /// </summary>
    private async Task SaveTransfersUpdateCountsAndStoreJsonAsync(
        List<TransferRecord> newRecords,
        CancellationToken cancellationToken)
    {
        using var itemsTable = new DataTable();
        itemsTable.Columns.Add("DocumentItemID", typeof(long));
        itemsTable.Columns.Add("DocumentID", typeof(long));
        itemsTable.Columns.Add("ModuleID", typeof(int));
        itemsTable.Columns.Add("OrigenID", typeof(long));
        itemsTable.Columns.Add("OrigenNombre", typeof(string));
        itemsTable.Columns.Add("DestinoID", typeof(long));
        itemsTable.Columns.Add("DestinoNombre", typeof(string));
        itemsTable.Columns.Add("ProductID", typeof(long));
        itemsTable.Columns.Add("ClaveProducto", typeof(string));
        itemsTable.Columns.Add("Cantidad", typeof(double));
        itemsTable.Columns.Add("Unidad", typeof(string));
        itemsTable.Columns.Add("PrecioUnitario", typeof(double));
        itemsTable.Columns.Add("ImporteTotal", typeof(double));
        itemsTable.Columns.Add("Folio", typeof(string));
        itemsTable.Columns.Add("Fecha", typeof(DateTime));
        itemsTable.Columns.Add("UsuarioCreadorID", typeof(long));
        itemsTable.Columns.Add("FechaRespaldo", typeof(DateTime));

        DateTime now = DateTime.Now;
        foreach (TransferRecord r in newRecords)
        {
            itemsTable.Rows.Add(
                r.DocumentItemID,
                r.DocumentID,
                r.ModuleID,
                r.OrigenID,
                r.OrigenNombre,
                r.DestinoID,
                r.DestinoNombre,
                r.ProductID,
                r.ClaveProducto,
                r.Cantidad,
                r.Unidad,
                r.PrecioUnitario,
                r.ImporteTotal,
                r.Folio,
                r.Fecha,
                r.UsuarioCreadorID,
                now);
        }

        using var payloadTable = new DataTable();
        payloadTable.Columns.Add("DocumentID", typeof(long));
        payloadTable.Columns.Add("ModuleID", typeof(int));
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

        foreach (IGrouping<long, TransferRecord> docGroup in newRecords.GroupBy(r => r.DocumentID).OrderBy(g => g.Key))
        {
            TransferRecord first = docGroup.First();
            TransferBatchPayloadDto singlePayload = BuildTransferBatchPayload(docGroup);
            string jsonBody = JsonSerializer.Serialize(singlePayload, JsonOptions);

            payloadTable.Rows.Add(
                first.DocumentID,
                first.ModuleID,
                singlePayload.Transfers[0].ExternalId,
                first.OrigenID,
                first.DestinoID,
                first.DestinoNombre,
                singlePayload.Transfers[0].BusinessDate,
                singlePayload.Transfers[0].Items.Count,
                jsonBody,
                false,
                DBNull.Value,
                now,
                DBNull.Value);
        }

        await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
        await localConn.OpenAsync(cancellationToken);

        await using SqlTransaction transaction = (SqlTransaction)await localConn.BeginTransactionAsync(cancellationToken);
        try
        {
            using (var bulkItems = new SqlBulkCopy(localConn, SqlBulkCopyOptions.Default, transaction))
            {
                bulkItems.DestinationTableName = "dbo.TraqueoTraspasos";
                bulkItems.BatchSize = 2000;
                bulkItems.BulkCopyTimeout = 60;

                foreach (DataColumn col in itemsTable.Columns)
                {
                    bulkItems.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                }

                await bulkItems.WriteToServerAsync(itemsTable, cancellationToken);
            }

            using (var bulkPayloads = new SqlBulkCopy(localConn, SqlBulkCopyOptions.Default, transaction))
            {
                bulkPayloads.DestinationTableName = "dbo.PayloadsJsonTraspasos";
                bulkPayloads.BatchSize = 1000;
                bulkPayloads.BulkCopyTimeout = 60;

                foreach (DataColumn col in payloadTable.Columns)
                {
                    bulkPayloads.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                }

                await bulkPayloads.WriteToServerAsync(payloadTable, cancellationToken);
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

    /// <summary>
    /// Toma los documentos pendientes (EnviadoApi = 0) y los envía en lotes separados por ModuleID (204, 1247, 1257) al endpoint de la API.
    /// Si la API responde exitosamente (2xx), los marca como EnviadoApi = 1.
    /// </summary>
    private async Task SendPendingTransfersToApiAsync(CancellationToken cancellationToken)
    {
        string fullEndpointUrl = _options.GetFullEndpointUrl();
        if (string.IsNullOrWhiteSpace(fullEndpointUrl))
        {
            return;
        }

        int batchSize = Math.Max(1, _options.ApiBatchSize);
        int[] modulesOrder = [1247, 1257, 204];

        await using var localConn = new SqlConnection(_options.LocalBackupConnectionString);
        await localConn.OpenAsync(cancellationToken);

        foreach (int currentModuleId in modulesOrder)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string pendingRecordsSql = $@"
                    WITH PendingDocs AS (
                        SELECT TOP ({batchSize}) DocumentID
                        FROM dbo.PayloadsJsonTraspasos
                        WHERE EnviadoApi = 0
                          AND ModuleID = @ModuleID
                        ORDER BY DocumentID ASC
                    )
                    SELECT 
                        t.DocumentID,
                        t.DocumentItemID,
                        t.ModuleID,
                        t.OrigenID,
                        t.OrigenNombre,
                        t.DestinoID,
                        t.DestinoNombre,
                        t.ProductID,
                        t.ClaveProducto,
                        t.Cantidad,
                        t.Unidad,
                        t.PrecioUnitario,
                        t.ImporteTotal,
                        t.Folio,
                        t.Fecha,
                        t.UsuarioCreadorID
                    FROM dbo.TraqueoTraspasos t
                    INNER JOIN PendingDocs p ON t.DocumentID = p.DocumentID
                    WHERE t.Cantidad > 0
                    ORDER BY t.DocumentID ASC, t.DocumentItemID ASC;";

                var batchRecords = new List<TransferRecord>();
                await using (var cmd = new SqlCommand(pendingRecordsSql, localConn))
                {
                    cmd.Parameters.AddWithValue("@ModuleID", currentModuleId);
                    await using SqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        batchRecords.Add(new TransferRecord
                        {
                            DocumentID = reader.GetInt64(0),
                            DocumentItemID = reader.GetInt64(1),
                            ModuleID = reader.GetInt32(2),
                            OrigenID = reader.GetInt64(3),
                            OrigenNombre = reader.GetString(4),
                            DestinoID = reader.GetInt64(5),
                            DestinoNombre = reader.GetString(6),
                            ProductID = reader.GetInt64(7),
                            ClaveProducto = reader.GetString(8),
                            Cantidad = reader.GetDouble(9),
                            Unidad = reader.GetString(10),
                            PrecioUnitario = reader.GetDouble(11),
                            ImporteTotal = reader.GetDouble(12),
                            Folio = reader.GetString(13),
                            Fecha = reader.GetDateTime(14),
                            UsuarioCreadorID = reader.GetInt64(15)
                        });
                    }
                }

                if (batchRecords.Count == 0)
                {
                    break;
                }

                TransferBatchPayloadDto batchDto = BuildTransferBatchPayload(batchRecords);
                string jsonPayload = JsonSerializer.Serialize(batchDto, JsonOptions);

                string firstFolio = batchDto.Transfers.FirstOrDefault()?.ExternalId ?? "-";
                string lastFolio = batchDto.Transfers.LastOrDefault()?.ExternalId ?? "-";

                _logger.LogInformation(
                    "Enviando lote [ModuleID={ModuleID}] de {Count} documentos ({ItemCount} partidas, Folios: {FirstFolio}..{LastFolio}) a {Url} | Body JSON enviado: {JsonPayload}",
                    currentModuleId,
                    batchDto.Transfers.Count,
                    batchRecords.Count,
                    firstFolio,
                    lastFolio,
                    fullEndpointUrl,
                    jsonPayload);

                using var request = new HttpRequestMessage(HttpMethod.Post, fullEndpointUrl)
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(_options.AgentApiKey))
                {
                    request.Headers.TryAddWithoutValidation("X-API-Key", _options.AgentApiKey);
                    request.Headers.TryAddWithoutValidation("X-Agent-Key", _options.AgentApiKey);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AgentApiKey);
                }

                try
                {
                    using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
                    int statusCode = (int)response.StatusCode;
                    bool success = response.IsSuccessStatusCode;
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                    long minDocId = batchRecords.Min(r => r.DocumentID);
                    long maxDocId = batchRecords.Max(r => r.DocumentID);

                    const string updateStatusSql = @"
                        UPDATE dbo.PayloadsJsonTraspasos
                        SET EnviadoApi = @Enviado,
                            CodigoRespuestaHttp = @StatusCode,
                            FechaEnvioApi = SYSDATETIME()
                        WHERE EnviadoApi = 0
                          AND ModuleID = @ModuleID
                          AND DocumentID BETWEEN @MinDocId AND @MaxDocId;";

                    await using var updateCmd = new SqlCommand(updateStatusSql, localConn);
                    updateCmd.Parameters.AddWithValue("@Enviado", success);
                    updateCmd.Parameters.AddWithValue("@StatusCode", statusCode);
                    updateCmd.Parameters.AddWithValue("@ModuleID", currentModuleId);
                    updateCmd.Parameters.AddWithValue("@MinDocId", minDocId);
                    updateCmd.Parameters.AddWithValue("@MaxDocId", maxDocId);
                    await updateCmd.ExecuteNonQueryAsync(cancellationToken);

                    if (!success)
                    {
                        _logger.LogWarning(
                            "La API en {Url} respondió con HTTP {StatusCode} para el lote [ModuleID={ModuleID}]. Respuesta API: {ResponseBody}. Se reintentará en el próximo ciclo.",
                            fullEndpointUrl,
                            statusCode,
                            currentModuleId,
                            responseBody);
                        break;
                    }

                    _logger.LogInformation(
                        "Lote [ModuleID={ModuleID}] de {Count} documentos enviado exitosamente a {Url} (HTTP {StatusCode}) | Respuesta API: {ResponseBody}",
                        currentModuleId,
                        batchDto.Transfers.Count,
                        fullEndpointUrl,
                        statusCode,
                        responseBody);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "No se pudo conectar con la API en {Url} ({Message}) al enviar lote [ModuleID={ModuleID}]. Quedan pendientes para el próximo ciclo.",
                        fullEndpointUrl,
                        ex.Message,
                        currentModuleId);
                    return;
                }
            }
        }
    }
}
