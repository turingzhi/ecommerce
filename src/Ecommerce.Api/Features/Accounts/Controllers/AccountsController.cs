using Ecommerce.Features.Accounts.Contracts;
using Ecommerce.Features.Orders.Models;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Features.Accounts.Controllers;

[ApiController]
[Route("auth")]
[Authorize]
public sealed class AccountsController : ControllerBase
{
    [HttpGet("me")]
    public IResult GetCurrentUser()
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id))
            return Results.Unauthorized();
        return Results.Ok(new CurrentUserResponse(
            id,
            User.FindFirstValue(ClaimTypes.Email),
            User.FindAll("permission")
                .Select(c => c.Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList()));
    }
}
