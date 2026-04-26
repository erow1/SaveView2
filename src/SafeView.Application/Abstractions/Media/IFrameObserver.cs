using SafeView.Domain.Cameras;

namespace SafeView.Application.Abstractions.Media;

/// <summary>
/// Obserwator klatek — powiadamiany przez <c>CameraFrameSampler</c> po każdym
/// udanym snapshot-cie. ML / reguły strefowe / incydenty rejestrują się jako
/// implementacje i dostają surowy wynik do przetworzenia.
/// Implementacja MUSI być szybka i nie blokować — długie operacje wrzucaj w własny background.
/// </summary>
public interface IFrameObserver
{
    Task OnFrameAsync(Camera camera, SnapshotResult frame, CancellationToken ct);
}
