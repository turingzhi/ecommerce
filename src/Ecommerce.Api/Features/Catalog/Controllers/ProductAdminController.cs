using Ecommerce.Common.Pagination;
using Ecommerce.Features.Catalog.Contracts;
using Ecommerce.Features.Catalog.Models;
using Ecommerce.Features.Catalog.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecommerce.Features.Catalog.Controllers;

[ApiController]
[Route("admin/products")]
[Authorize(Policy = ProductAdministration.Policy)]
public sealed class ProductAdminController : ControllerBase
{
    [HttpGet("")]
    public async Task<IResult> List(
        [FromQuery] string? name,
        [FromQuery] string? category,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromServices] ProductQueryService query,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (name?.Trim().Length > 200 || category?.Trim().Length > 200)
            return Results.BadRequest(new { error = "Filters must contain at most 200 characters." });
        return Results.Ok(await query.ListForAdminAsync(name, category, PageBounds.Normalize(page, pageSize), ct));
    }

    [HttpPost("")]
    public async Task<IResult> Create(
        [FromBody] CreateProductRequest body,
        [FromServices] ProductCatalogService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            var product = await service.CreateAsync(new(body.Name!, body.Description!, body.Category!, body.PriceCents, body.Available), ct);
            return Results.Created($"/products/{product.Id}", AdminProductResponse.From(product));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IResult> Update(
        [FromRoute] int id,
        [FromBody] UpdateProductRequest body,
        [FromServices] ProductCatalogService service,
        CancellationToken ct)
    {
        HttpContext.Response.Headers.CacheControl = "no-store";
        if (body.ExpectedVersion <= 0)
            return Results.BadRequest(new { error = "A positive expectedVersion is required." });
        try
        {
            return Results.Ok(AdminProductResponse.From(await service.UpdateVersionedAsync(
                id,
                new(body.Name!, body.Description!, body.Category!, body.PriceCents),
                body.ExpectedVersion,
                ct)));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "Product changed; reload before saving." });
        }
    }
}
