namespace Ecommerce.Features.Accounts.Contracts;
public record CurrentUserResponse(string UserId,string? Email,IReadOnlyList<string> Permissions);
