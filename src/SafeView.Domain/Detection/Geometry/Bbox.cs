namespace SafeView.Domain.Detection.Geometry;

/// <summary>
/// Bounding box w znormalizowanych koordynatach [0..1] względem klatki kamery.
/// Immutable record — value type dla zero-alloc w hot path detekcji.
/// </summary>
public readonly record struct Bbox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
    public double Area => Width * Height;

    /// <summary>Cztery narożniki (TopLeft, TopRight, BottomRight, BottomLeft).</summary>
    public (double X, double Y)[] Corners() =>
    [
        (X, Y),
        (Right, Y),
        (Right, Bottom),
        (X, Bottom),
    ];

    /// <summary>Pole przecięcia z innym bboxem (0 gdy brak overlapu).</summary>
    public double IntersectionArea(Bbox other)
    {
        var x1 = Math.Max(X, other.X);
        var y1 = Math.Max(Y, other.Y);
        var x2 = Math.Min(Right, other.Right);
        var y2 = Math.Min(Bottom, other.Bottom);
        if (x2 <= x1 || y2 <= y1) return 0;
        return (x2 - x1) * (y2 - y1);
    }
}
