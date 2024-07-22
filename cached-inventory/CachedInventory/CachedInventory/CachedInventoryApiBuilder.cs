namespace CachedInventory;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();
    builder.Services.AddMemoryCache();
    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      app.UseSwagger();
      app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    var cache = app.Services.GetRequiredService<IMemoryCache>();
    cache.Set(
      "key",
      "value",
      new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(300) }
    );

    app.MapGet(
        "/stock/{productId:int}",
        async ([FromServices] IWarehouseStockSystemClient client, int productId) =>
        {
          var cacheKey = $"stock_{productId}";

          if (!cache.TryGetValue(cacheKey, out int cachedStock))
          {
            cachedStock = await client.GetStock(productId);
            cache.Set(cacheKey, cachedStock);
          }
          return cachedStock;
        }
      )
      .WithName("GetStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/retrieve",
        async (
          [FromServices] IWarehouseStockSystemClient client,
          [FromBody] RetrieveStockRequest req
        ) =>
        {
          var cacheKey = $"stock_{req.ProductId}";

          if (!cache.TryGetValue(cacheKey, out int cachedStock))
          {
            cachedStock = await client.GetStock(req.ProductId);
          }

          if (cachedStock < req.Amount)
          {
            return Results.BadRequest("Not enough stock.");
          }

          await client.UpdateStock(req.ProductId, cachedStock - req.Amount);
          cache.Set(cacheKey, cachedStock - req.Amount);
          return Results.Ok();
        }
      )
      .WithName("RetrieveStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/restock",
        async ([FromServices] IWarehouseStockSystemClient client, [FromBody] RestockRequest req) =>
        {
          var cacheKey = $"stock_{req.ProductId}";

          if (!cache.TryGetValue(cacheKey, out int cachedStock))
          {
            cachedStock = await client.GetStock(req.ProductId);
          }

          await client.UpdateStock(req.ProductId, req.Amount + cachedStock);
          cache.Set(cacheKey, req.Amount + cachedStock);
          return Results.Ok();
        }
      )
      .WithName("Restock")
      .WithOpenApi();

    return app;
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);
