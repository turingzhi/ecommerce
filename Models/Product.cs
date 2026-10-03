namespace Ecommerce;

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";

    public long PriceCents { get; set; }
    public int Available { get; set; }
    public long Version { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
