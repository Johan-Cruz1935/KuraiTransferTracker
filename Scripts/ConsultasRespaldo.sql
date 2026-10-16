-- =============================================================================
-- CONSULTAS PARA REVISAR LA BASE DE DATOS LOCAL DE RESPALDO [KuraiTrackingDB]
-- =============================================================================

USE [KuraiTrackingDB];
GO

-- 1. Ver el marcador / conteo automático de traspasos por cada Sucursal
SELECT 
    SucursalID,
    SucursalNombre,
    TotalTraspasos,
    TotalPartidas,
    UltimoDocumentID,
    UltimoFolio,
    UltimaFechaTraspaso,
    FechaActualizacion
FROM dbo.ConteoPorSucursal
ORDER BY TotalTraspasos DESC;
GO

-- 2. Ver los Bodies JSON listos para enviar / enviados de cada traspaso
SELECT TOP 20
    DocumentID,
    ExternalID,
    OrigenID,
    DestinoID,
    DestinoNombre,
    FechaNegocio,
    TotalItems,
    JsonBody,
    EnviadoApi,
    CodigoRespuestaHttp,
    FechaGeneracion
FROM dbo.PayloadsJsonTraspasos
ORDER BY DocumentID DESC;
GO

-- 3. Ver los traspasos respaldados de una sucursal en específico (Ej: Agua Dulce = ID 4)
SELECT TOP 100
    DocumentID,
    DocumentItemID,
    Folio,
    Fecha,
    OrigenID,
    OrigenNombre,
    DestinoID,
    DestinoNombre,
    ProductID,
    ClaveProducto,
    Cantidad,
    Unidad,
    UsuarioCreadorID,
    FechaRespaldo
FROM dbo.TraqueoTraspasos
WHERE DestinoID = 4 -- 4 = Agua Dulce
ORDER BY DocumentID DESC, DocumentItemID ASC;
GO
