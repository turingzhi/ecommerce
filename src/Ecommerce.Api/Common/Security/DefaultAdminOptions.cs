namespace Ecommerce.Common.Security;

public sealed class DefaultAdminOptions
{
    public bool Enabled { get; set; }
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}
