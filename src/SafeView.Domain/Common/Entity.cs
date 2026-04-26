namespace SafeView.Domain.Common;

/// <summary>
/// Bazowa encja — identyfikator jako 24-znakowy hex (kompatybilny z MongoDB ObjectId).
/// Domain nie zależy od MongoDB — generujemy w warstwie Infrastructure.
/// </summary>
public abstract class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..24];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
