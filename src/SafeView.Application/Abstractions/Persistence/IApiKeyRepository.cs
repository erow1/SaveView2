using SafeView.Domain.Api;

namespace SafeView.Application.Abstractions.Persistence;

public interface IApiKeyRepository : IRepository<ApiKey>
{
    Task<ApiKey?> FindByHashAsync(string keyHash, CancellationToken ct = default);
    Task TouchUsedAsync(string id, DateTime usedAt, CancellationToken ct = default);
}
