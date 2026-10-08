namespace Ecommerce.Features.Returns.Models;
public class ReturnRequest
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public Guid OrderId {get;set;}
    public Guid PaymentId {get;set;}
    public string Key {get;set;}="";
    public string Reason {get;set;}="";
    public string Status {get;set;}="Requested";
    public DateTime CreatedAt {get;set;}=DateTime.UtcNow;
    public DateTime? ApprovedAt {get;set;}
    public DateTime? ReceivedAt {get;set;}
    public DateTime? CompletedAt {get;set;}
}
