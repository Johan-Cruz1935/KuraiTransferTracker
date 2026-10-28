-- =============================================================================
-- SCRIPT COMPLETO DE PRODUCCIÓN: BASE DE DATOS Y USUARIOS DE SEGURIDAD
-- Proyecto: KuraiTransferTracker (Servicio Windows de Traqueo de Traspasos)
-- =============================================================================

USE [master];
GO

-- =============================================================================
-- 1. CREAR LA BASE DE DATOS LOCAL DE RESPALDO [KuraiTrackingDB]
-- =============================================================================
IF DB_ID(N'KuraiTrackingDB') IS NULL
BEGIN
    CREATE DATABASE [KuraiTrackingDB];
    PRINT 'Base de datos [KuraiTrackingDB] creada exitosamente.';
END
ELSE
BEGIN
    PRINT 'La base de datos [KuraiTrackingDB] ya existe.';
END
GO

USE [KuraiTrackingDB];
GO

-- =============================================================================
-- 2. TABLA: [dbo].[TraqueoTraspasos]
-- Guarda el detalle ligero de cada partida traspasada desde el Almacén General
-- hacia cada sucursal (sin descripciones pesadas ni columnas redundantes).
-- =============================================================================
IF OBJECT_ID(N'dbo.TraqueoTraspasos', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TraqueoTraspasos (
        DocumentItemID      BIGINT         NOT NULL,
        DocumentID          BIGINT         NOT NULL,
        ModuleID            INT            NOT NULL CONSTRAINT DF_TraqueoTraspasos_ModuleID DEFAULT (204),
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
        FechaRespaldo       DATETIME2      NOT NULL CONSTRAINT DF_TraqueoTraspasos_FechaRespaldo DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_TraqueoTraspasos PRIMARY KEY CLUSTERED (DocumentItemID ASC)
    );

    CREATE NONCLUSTERED INDEX IX_TraqueoTraspasos_DocumentID 
        ON dbo.TraqueoTraspasos (DocumentID ASC);

    CREATE NONCLUSTERED INDEX IX_TraqueoTraspasos_DestinoID 
        ON dbo.TraqueoTraspasos (DestinoID ASC, DocumentID DESC);

    PRINT 'Tabla [dbo].[TraqueoTraspasos] creada exitosamente.';
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.TraqueoTraspasos', 'ModuleID') IS NULL
    BEGIN
        ALTER TABLE dbo.TraqueoTraspasos ADD ModuleID INT NOT NULL CONSTRAINT DF_TraqueoTraspasos_ModuleID DEFAULT (204);
        PRINT 'Columna [ModuleID] agregada a [dbo].[TraqueoTraspasos].';
    END

    IF COL_LENGTH('dbo.TraqueoTraspasos', 'PrecioUnitario') IS NULL
    BEGIN
        ALTER TABLE dbo.TraqueoTraspasos ADD PrecioUnitario FLOAT NOT NULL CONSTRAINT DF_TraqueoTraspasos_PrecioUnitario DEFAULT (0);
        PRINT 'Columna [PrecioUnitario] agregada a [dbo].[TraqueoTraspasos].';
    END

    IF COL_LENGTH('dbo.TraqueoTraspasos', 'ImporteTotal') IS NULL
    BEGIN
        ALTER TABLE dbo.TraqueoTraspasos ADD ImporteTotal FLOAT NOT NULL CONSTRAINT DF_TraqueoTraspasos_ImporteTotal DEFAULT (0);
        PRINT 'Columna [ImporteTotal] agregada a [dbo].[TraqueoTraspasos].';
    END
END
GO

-- =============================================================================
-- 3. TABLA: [dbo].[ConteoPorSucursal]
-- Lleva el conteo automático en vivo de cuántos traspasos y partidas ha
-- recibido cada sucursal, así como cuál fue su último documento y fecha.
-- =============================================================================
IF OBJECT_ID(N'dbo.ConteoPorSucursal', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ConteoPorSucursal (
        SucursalID          BIGINT         NOT NULL,
        SucursalNombre      NVARCHAR(150)  NOT NULL,
        TotalTraspasos      INT            NOT NULL,
        TotalPartidas       INT            NOT NULL,
        UltimoDocumentID    BIGINT         NOT NULL,
        UltimoFolio         NVARCHAR(100)  NOT NULL,
        UltimaFechaTraspaso DATETIME2      NOT NULL,
        FechaActualizacion  DATETIME2      NOT NULL CONSTRAINT DF_ConteoPorSucursal_FechaAct DEFAULT (SYSDATETIME()),
        CONSTRAINT PK_ConteoPorSucursal PRIMARY KEY CLUSTERED (SucursalID ASC)
    );

    PRINT 'Tabla [dbo].[ConteoPorSucursal] creada exitosamente.';
END
GO

-- =============================================================================
-- 4. TABLA: [dbo].[PayloadsJsonTraspasos]
-- Guarda el Body JSON exacto generado por cada traspaso y controla la cola de
-- envío hacia la API (EnviadoApi = 0 pendiente, EnviadoApi = 1 enviado).
-- =============================================================================
IF OBJECT_ID(N'dbo.PayloadsJsonTraspasos', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PayloadsJsonTraspasos (
        DocumentID          BIGINT         NOT NULL,
        ModuleID            INT            NOT NULL CONSTRAINT DF_PayloadsJson_ModuleID DEFAULT (204),
        ExternalID          NVARCHAR(100)  NOT NULL,
        OrigenID            BIGINT         NOT NULL,
        DestinoID           BIGINT         NOT NULL,
        DestinoNombre       NVARCHAR(150)  NOT NULL,
        FechaNegocio        NVARCHAR(20)   NOT NULL,
        TotalItems          INT            NOT NULL,
        JsonBody            NVARCHAR(MAX)  NOT NULL,
        EnviadoApi          BIT            NOT NULL CONSTRAINT DF_PayloadsJson_EnviadoApi DEFAULT (0),
        CodigoRespuestaHttp INT            NULL,
        FechaGeneracion     DATETIME2      NOT NULL CONSTRAINT DF_PayloadsJson_FechaGen DEFAULT (SYSDATETIME()),
        FechaEnvioApi       DATETIME2      NULL,
        CONSTRAINT PK_PayloadsJsonTraspasos PRIMARY KEY CLUSTERED (DocumentID ASC)
    );

    CREATE NONCLUSTERED INDEX IX_PayloadsJson_Pendientes 
        ON dbo.PayloadsJsonTraspasos (EnviadoApi ASC, ModuleID ASC, DocumentID ASC);

    CREATE NONCLUSTERED INDEX IX_PayloadsJson_DestinoID 
        ON dbo.PayloadsJsonTraspasos (DestinoID ASC, DocumentID DESC);

    PRINT 'Tabla [dbo].[PayloadsJsonTraspasos] creada exitosamente.';
END
ELSE IF COL_LENGTH('dbo.PayloadsJsonTraspasos', 'ModuleID') IS NULL
BEGIN
    ALTER TABLE dbo.PayloadsJsonTraspasos ADD ModuleID INT NOT NULL CONSTRAINT DF_PayloadsJson_ModuleID DEFAULT (204);
    PRINT 'Columna [ModuleID] agregada a [dbo].[PayloadsJsonTraspasos].';
END
GO

-- =============================================================================
-- 5. SEGURIDAD: HABILITAR MODO MIXTO Y CREAR LOGINS / USUARIOS CON PERMISOS ESTRICTOS
-- =============================================================================
USE [master];
GO

-- Habilitar Autenticación en Modo Mixto (SQL Server y Windows Authentication)
-- Nota: Si el servidor estaba solo en modo Windows, requiere reiniciar el servicio SQL Server una vez.
EXEC xp_instance_regwrite 
    N'HKEY_LOCAL_MACHINE', 
    N'Software\Microsoft\MSSQLServer\MSSQLServer', 
    N'LoginMode', 
    REG_DWORD, 
    2;
GO

-- 5.1 Login de SOLO LECTURA para producción [KURAI2026]
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'usr_kurai_readonly')
BEGIN
    CREATE LOGIN [usr_kurai_readonly] 
        WITH PASSWORD = N'KuraiReadOnly#2026!',
        DEFAULT_DATABASE = [KURAI2026],
        CHECK_EXPIRATION = OFF,
        CHECK_POLICY = OFF;
    PRINT 'Login [usr_kurai_readonly] creado.';
END
GO

-- 5.2 Login con TODOS LOS PERMISOS para la BD local [KuraiTrackingDB]
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'usr_kurai_tracking_admin')
BEGIN
    CREATE LOGIN [usr_kurai_tracking_admin] 
        WITH PASSWORD = N'KuraiTrackingAdmin#2026!',
        DEFAULT_DATABASE = [KuraiTrackingDB],
        CHECK_EXPIRATION = OFF,
        CHECK_POLICY = OFF;
    PRINT 'Login [usr_kurai_tracking_admin] creado.';
END
GO

-- =============================================================================
-- 6. ASIGNAR PERMISOS DE SOLO LECTURA EN [KURAI2026]
-- =============================================================================
IF DB_ID(N'KURAI2026') IS NOT NULL
BEGIN
    USE [KURAI2026];

    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'usr_kurai_readonly')
    BEGIN
        CREATE USER [usr_kurai_readonly] FOR LOGIN [usr_kurai_readonly];
    END
    ELSE
    BEGIN
        ALTER USER [usr_kurai_readonly] WITH LOGIN = [usr_kurai_readonly];
    END

    -- Otorgar permiso de lectura (SELECT)
    ALTER ROLE [db_datareader] ADD MEMBER [usr_kurai_readonly];

    -- Bloquear explícitamente cualquier escritura (INSERT, UPDATE, DELETE) y cambios de esquema (DDL)
    ALTER ROLE [db_denydatawriter] ADD MEMBER [usr_kurai_readonly];
    DENY ALTER, CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION TO [usr_kurai_readonly];

    PRINT 'Usuario [usr_kurai_readonly] configurado como ESTRICTAMENTE SOLO LECTURA en [KURAI2026].';
END
GO

-- =============================================================================
-- 7. ASIGNAR TODOS LOS PERMISOS (db_owner) EN [KuraiTrackingDB]
-- =============================================================================
USE [KuraiTrackingDB];
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'usr_kurai_tracking_admin')
BEGIN
    CREATE USER [usr_kurai_tracking_admin] FOR LOGIN [usr_kurai_tracking_admin];
END

-- Otorgar todos los permisos sobre [KuraiTrackingDB]
ALTER ROLE [db_owner] ADD MEMBER [usr_kurai_tracking_admin];

PRINT 'Usuario [usr_kurai_tracking_admin] configurado con TODOS LOS PERMISOS (db_owner) en [KuraiTrackingDB].';
GO
