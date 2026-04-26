using SafeView.Domain.Users;

namespace SafeView.Application.Abstractions.Persistence;

public interface IUserRepository : IRepository<User>
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<User?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
}

public interface IRoleRepository : IRepository<Role>
{
    Task<Role?> FindByNameAsync(string name, CancellationToken ct = default);
}
