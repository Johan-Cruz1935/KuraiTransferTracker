namespace KuraiTransferTracker.Models;

/// <summary>
/// Representa un renglón ligero de traspaso extraído de [KURAI2026]
/// sin campos innecesarios (como descripción del producto, EmpresaID o ModuloID).
/// </summary>
public sealed class TransferRecord
{
    public long DocumentID { get; init; }
    public long DocumentItemID { get; init; }
    public int ModuleID { get; init; } = 204;
    public long OrigenID { get; init; }
    public string OrigenNombre { get; init; } = string.Empty;
    public long DestinoID { get; init; }
    public string DestinoNombre { get; init; } = string.Empty;
    public long ProductID { get; init; }
    public string ClaveProducto { get; init; } = string.Empty;
    public double Cantidad { get; init; }
    public string Unidad { get; init; } = string.Empty;
    public double PrecioUnitario { get; init; }
    public double ImporteTotal { get; init; }
    public string Folio { get; init; } = string.Empty;
    public DateTime Fecha { get; init; }
    public long UsuarioCreadorID { get; init; }
}
