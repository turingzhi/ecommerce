using Ecommerce.Features.Payments.Contracts;
using Ecommerce.Features.Payments.Models;
using Ecommerce.Features.Refunds.Contracts;
using Ecommerce.Features.Refunds.Models;
using System.Text.Json;
using Xunit;

namespace Ecommerce.Api.Tests.Payments;

public class FinancialResponseTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void PaymentTimestampRemainsUtcAfterSqlRoundTrip(DateTimeKind kind)
    {
        var payment = new Payment { CreatedAt = new DateTime(2026, 10, 8, 0, 0, 0, kind) };
        var response = PaymentResponse.From(payment);
        Assert.Equal("2026-10-08T00:00:00Z",
            JsonSerializer.SerializeToElement(response).GetProperty("CreatedAt").GetString());
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void RefundTimestampRemainsUtcAfterSqlRoundTrip(DateTimeKind kind)
    {
        var refund = new Refund { CreatedAt = new DateTime(2026, 10, 8, 0, 0, 0, kind) };
        var response = RefundResponse.From(refund);
        Assert.Equal("2026-10-08T00:00:00Z",
            JsonSerializer.SerializeToElement(response).GetProperty("CreatedAt").GetString());
    }
}
