using CachedInventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();
    builder.Services.AddMemoryCache();

    builder.Services.AddSingleton<BackgroundStockUpdateService>();
    builder.Services.AddHostedService(provider =>
      provider.GetRequiredService<BackgroundStockUpdateService>()
    );

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      app.UseSwagger();
      app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    var backgroundStockUpdateService =
      app.Services.GetRequiredService<BackgroundStockUpdateService>();
    var cache = app.Services.GetRequiredService<IMemoryCache>();

    app.MapGet(
        "/stock/{productId:int}",
        async ([FromServices] IWarehouseStockSystemClient client, int productId) =>
        {
          var cacheKey = productId;

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
          var cacheKey = req.ProductId;

          if (!cache.TryGetValue(cacheKey, out int cachedStock))
          {
            cachedStock = await client.GetStock(req.ProductId);
          }

          if (cachedStock < req.Amount)
          {
            return Results.BadRequest("Not enough stock.");
          }

          cache.Set(cacheKey, cachedStock - req.Amount);
          backgroundStockUpdateService.QueueStockUpdate(req.ProductId, cachedStock - req.Amount);
          return Results.Ok();
        }
      )
      .WithName("RetrieveStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/restock",
        async ([FromServices] IWarehouseStockSystemClient client, [FromBody] RestockRequest req) =>
        {
          var cacheKey = req.ProductId;

          if (!cache.TryGetValue(cacheKey, out int cachedStock))
          {
            cachedStock = await client.GetStock(req.ProductId);
          }

          cache.Set(cacheKey, cachedStock + req.Amount);
          backgroundStockUpdateService.QueueStockUpdate(req.ProductId, cachedStock + req.Amount);

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
