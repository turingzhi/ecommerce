using System.Security.Claims;
using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Api.Tests.Accounts;

// Only the persistence boundary is replaced. Identity's validators, password
// hashing, normalization and claim operations are exercised through UserManager.
public sealed class MemoryIdentityStore : IUserPasswordStore<IdentityUser>,
    IUserEmailStore<IdentityUser>, IUserClaimStore<IdentityUser>
{
    public Dictionary<string, IdentityUser> Users { get; } = [];
    private readonly Dictionary<string, List<Claim>> claims = [];
    public void Dispose() { }
    public Task<IdentityResult> CreateAsync(IdentityUser user, CancellationToken ct)
    {
        Users.Add(user.Id, user);
        return Task.FromResult(IdentityResult.Success);
    }
    public Task<IdentityResult> UpdateAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
    public Task<IdentityResult> DeleteAsync(IdentityUser user, CancellationToken ct)
    {
        Users.Remove(user.Id);
        return Task.FromResult(IdentityResult.Success);
    }
    public Task<string> GetUserIdAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.UserName);
    public Task SetUserNameAsync(IdentityUser user, string? name, CancellationToken ct) { user.UserName = name; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
    public Task SetNormalizedUserNameAsync(IdentityUser user, string? name, CancellationToken ct) { user.NormalizedUserName = name; return Task.CompletedTask; }
    public Task<IdentityUser?> FindByIdAsync(string id, CancellationToken ct) => Task.FromResult(Users.GetValueOrDefault(id));
    public Task<IdentityUser?> FindByNameAsync(string name, CancellationToken ct) => Task.FromResult(Users.Values.SingleOrDefault(user => user.NormalizedUserName == name));
    public Task SetPasswordHashAsync(IdentityUser user, string? hash, CancellationToken ct) { user.PasswordHash = hash; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.PasswordHash is not null);
    public Task SetEmailAsync(IdentityUser user, string? email, CancellationToken ct) { user.Email = email; return Task.CompletedTask; }
    public Task<string?> GetEmailAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.Email);
    public Task<bool> GetEmailConfirmedAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.EmailConfirmed);
    public Task SetEmailConfirmedAsync(IdentityUser user, bool confirmed, CancellationToken ct) { user.EmailConfirmed = confirmed; return Task.CompletedTask; }
    public Task<IdentityUser?> FindByEmailAsync(string email, CancellationToken ct) => Task.FromResult(Users.Values.SingleOrDefault(user => user.NormalizedEmail == email));
    public Task<string?> GetNormalizedEmailAsync(IdentityUser user, CancellationToken ct) => Task.FromResult(user.NormalizedEmail);
    public Task SetNormalizedEmailAsync(IdentityUser user, string? email, CancellationToken ct) { user.NormalizedEmail = email; return Task.CompletedTask; }
    public Task<IList<Claim>> GetClaimsAsync(IdentityUser user, CancellationToken ct) => Task.FromResult<IList<Claim>>(claims.GetValueOrDefault(user.Id, []).ToList());
    public Task AddClaimsAsync(IdentityUser user, IEnumerable<Claim> additions, CancellationToken ct)
    {
        if (!claims.TryGetValue(user.Id, out var saved)) claims[user.Id] = saved = [];
        saved.AddRange(additions);
        return Task.CompletedTask;
    }
    public Task ReplaceClaimAsync(IdentityUser user, Claim old, Claim replacement, CancellationToken ct)
    {
        var saved = claims.GetValueOrDefault(user.Id, []);
        saved.RemoveAll(claim => claim.Type == old.Type && claim.Value == old.Value);
        saved.Add(replacement);
        return Task.CompletedTask;
    }
    public Task RemoveClaimsAsync(IdentityUser user, IEnumerable<Claim> removals, CancellationToken ct)
    {
        foreach (var claim in removals) claims.GetValueOrDefault(user.Id, []).RemoveAll(saved => saved.Type == claim.Type && saved.Value == claim.Value);
        return Task.CompletedTask;
    }
    public Task<IList<IdentityUser>> GetUsersForClaimAsync(Claim claim, CancellationToken ct) =>
        Task.FromResult<IList<IdentityUser>>(Users.Values.Where(user => claims.GetValueOrDefault(user.Id, []).Any(saved => saved.Type == claim.Type && saved.Value == claim.Value)).ToList());
}
