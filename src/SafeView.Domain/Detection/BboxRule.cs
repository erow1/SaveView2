namespace SafeView.Domain.Detection;

/// <summary>
/// Kryterium decyzji czy bounding box detekcji „wpada do strefy".
/// Różne kryteria są przydatne dla różnych scenariuszy:
///  • <see cref="CenterInZone"/> — klasyczne, najczęstsze; pozycja obiektu = środek bbox
///  • <see cref="AnyCorner"/> — najluźniejsze; wystarczy że KAŻDY narożnik jest w strefie
///  • <see cref="AllCorners"/> — najtrudniejsze; cały obiekt musi być w strefie
///  • <see cref="NCorners"/> — pośrednie; np. 2 z 4 narożników (konfigurowalne)
///  • <see cref="Iou"/> — procentowe pokrycie bbox ze strefą (IoU threshold)
/// </summary>
public enum BboxRule
{
    CenterInZone = 0,
    AnyCorner = 1,
    AllCorners = 2,
    NCorners = 3,
    Iou = 4
}
