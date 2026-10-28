# KuraiTransferTracker

Servicio Windows (.NET 8 C#) para la extracción, respaldo y sincronización en tiempo real de traspasos de almacén desde el ERP KURAI2026 hacia la base de datos local `KuraiTrackingDB` y despacho HTTP en lotes hacia la API de integración.

## Características Principales
- **Soporte Multi-Módulo:** Módulo 204 (TAG), Módulo 1247 (Pedidos Nocturnos) y Módulo 1257 (Remisiones CEDIS).
- **Cálculo de Precios e Importes:** Enlace automático con `dbo._UltimoCostoProductos` (`unit_price` y `total_price`).
- **Backfill Inteligente:** Ingesta de históricos pasados por lotes sin borrado de base de datos ni duplicación de envíos.
- **Resiliencia:** Reintentos automáticos ante caídas de red y cola persistente de pendientes.