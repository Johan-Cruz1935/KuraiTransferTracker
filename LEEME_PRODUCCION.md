# Guía de Despliegue a Producción — KuraiTransferTracker

Este paquete (`publish`) es **autocontenido (`win-x64`)**: incluye el runtime de .NET 8 integrado dentro de `KuraiTransferTracker.exe`, por lo que **no requiere instalar nada adicional** en el servidor de producción.

---

## Estructura de la Carpeta de Producción

| Archivo / Carpeta | Descripción |
| :--- | :--- |
| `KuraiTransferTracker.exe` | Ejecutable principal del Agente / Servicio de Windows (autocontenido). |
| `appsettings.yml` | Archivo de configuración (conexiones SQL, URL de la API, fecha inicial, tamaño de lote). |
| `0_Inicializar_BaseDeDatos_Y_Usuarios.bat` | Crea `[KuraiTrackingDB]`, sus 3 tablas, habilita Modo Mixto en SQL Server y configura los 2 usuarios SQL. |
| `1_Probar_En_Consola.bat` | Permite probar un ciclo único (`--run-once`) o ejecución continua viendo los logs en pantalla. |
| `2_Instalar_Servicio_Windows.bat` | Registra e inicia el Servicio de Windows `KuraiTransferTracker` en inicio automático (`Auto`) con recuperación ante fallos. |
| `3_Desinstalar_Servicio_Windows.bat` | Detiene y desinstala el Servicio de Windows (conservando intacta la base de datos `[KuraiTrackingDB]`). |
| `Scripts\CrearBaseDeDatos_KuraiTrackingDB.sql` | Script SQL completo de creación de base de datos, tablas y usuarios de seguridad. |
| `Scripts\ConsultasRespaldo.sql` | Consultas útiles para auditar traspasos respaldados, conteo por sucursal y cola de envío a la API. |
| `logs\` | Carpeta generada automáticamente con un archivo de log por día (`kurai-tracker-YYYY-MM-DD.log`). |

---

## Pasos para Llevar a Producción

### Paso 1: Copiar la carpeta al servidor
Copia el contenido de esta carpeta a una ruta fija en el servidor donde corre SQL Server, por ejemplo:
`C:\Servicios\KuraiTransferTracker\`

### Paso 2: Inicializar la Base de Datos y Usuarios SQL
Haz **clic derecho -> Ejecutar como Administrador** sobre:
- `0_Inicializar_BaseDeDatos_Y_Usuarios.bat`
  *(O bien ejecuta manualmente `Scripts\CrearBaseDeDatos_KuraiTrackingDB.sql` en SQL Server Management Studio y reinicia el servicio `MSSQLSERVER` una vez si el servidor no tenía habilitado el Modo Mixto).*

Usuarios creados por el script:
1. **`usr_kurai_readonly`** (`KuraiReadOnly#2026!`): Permisos **estrictamente de solo lectura** (`db_datareader` + `db_denydatawriter`) sobre `[KURAI2026]`.
2. **`usr_kurai_tracking_admin`** (`KuraiTrackingAdmin#2026!`): Permisos totales (`db_owner`) **únicamente** sobre `[KuraiTrackingDB]`.

### Paso 3: Verificar `appsettings.yml`
Revisa los parámetros en `appsettings.yml` si necesitas ajustar la IP/puerto de la API o la fecha inicial:
```yaml
TrackerSettings:
  SourceConnectionString: "Server=localhost;Database=KURAI2026;User Id=usr_kurai_readonly;Password=KuraiReadOnly#2026!;TrustServerCertificate=True;ApplicationIntent=ReadOnly;"
  MasterConnectionString: "Server=localhost;Database=KuraiTrackingDB;User Id=usr_kurai_tracking_admin;Password=KuraiTrackingAdmin#2026!;TrustServerCertificate=True;"
  LocalDatabaseName: "KuraiTrackingDB"
  LocalBackupConnectionString: "Server=localhost;Database=KuraiTrackingDB;User Id=usr_kurai_tracking_admin;Password=KuraiTrackingAdmin#2026!;TrustServerCertificate=True;"
  PollingIntervalSeconds: 30
  UseFolioAsExternalId: true
  UseProductKeyAsProductId: false
  ApiBaseUrl: "http://192.168.1.111:8000"
  SyncEndpoint: "/transfers/sync"
  AgentApiKey: "replace_with_a_long_random_agent_key"
  StartDate: "2026-09-01T00:00:00"
  ApiBatchSize: 100
```

### Paso 4: Prueba rápida antes de dejar el servicio fijo
Ejecuta `1_Probar_En_Consola.bat` y elige la opción **`1` (`--run-once`)** para verificar en consola que:
1. Conecta correctamente a `[KURAI2026]` y `[KuraiTrackingDB]`.
2. Envía el lote a la API y muestra el Body JSON y la respuesta `HTTP 200`.

### Paso 5: Instalar como Servicio de Windows
Haz **clic derecho -> Ejecutar como Administrador** sobre:
- `2_Instalar_Servicio_Windows.bat`

El servicio quedará activo 24/7 en segundo plano e iniciará automáticamente con Windows.
