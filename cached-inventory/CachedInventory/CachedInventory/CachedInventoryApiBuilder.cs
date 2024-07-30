using System.Collections.Concurrent;
using CachedInventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

public static class CachedInventoryApiBuilder
{
  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);
    var semaphores = new ConcurrentDictionary<int, SemaphoreSlim>();
    var timers = new ConcurrentDictionary<int, Timer>();

    // Add services to the container.
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddScoped<IWarehouseStockSystemClient, WarehouseStockSystemClient>();
    builder.Services.AddMemoryCache();

    builder.Services.AddSingleton<BackgroundStockUpdateService>();

    builder.Services.AddSingleton(semaphores);
    builder.Services.AddSingleton(timers);
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
          [FromServices] ConcurrentDictionary<int, SemaphoreSlim> semaphores,
          [FromBody] RetrieveStockRequest req
        ) =>
        {
          var semaphore = semaphores.GetOrAdd(req.ProductId, new SemaphoreSlim(1, 1));
          await semaphore.WaitAsync();
          try
          {
            if (!cache.TryGetValue(req.ProductId, out int cachedStock))
            {
              cachedStock = await client.GetStock(req.ProductId);
            }

            if (cachedStock < req.Amount)
            {
              return Results.BadRequest("Not enough stock.");
            }

            cache.Set(req.ProductId, cachedStock - req.Amount);
            ResetTimer(req.ProductId, client, cache, timers);
            //  backgroundStockUpdateService.QueueStockUpdate(req.ProductId, cachedStock - req.Amount);
            return Results.Ok();
          }
          finally
          {
            semaphore.Release();
          }
        }
      )
      .WithName("RetrieveStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/restock",
        async (
          [FromServices] IWarehouseStockSystemClient client,
          [FromServices] ConcurrentDictionary<int, SemaphoreSlim> semaphores,
          [FromBody] RestockRequest req
        ) =>
        {
          var semaphore = semaphores.GetOrAdd(req.ProductId, new SemaphoreSlim(1, 1));
          await semaphore.WaitAsync();
          try
          {
            var cacheKey = req.ProductId;

            if (!cache.TryGetValue(cacheKey, out int cachedStock))
            {
              cachedStock = await client.GetStock(req.ProductId);
            }

            cache.Set(cacheKey, cachedStock + req.Amount);
            ResetTimer(req.ProductId, client, cache, timers);
            // backgroundStockUpdateService.QueueStockUpdate(req.ProductId, cachedStock + req.Amount);

            return Results.Ok();
          }
          finally
          {
            semaphore.Release();
          }
        }
      )
      .WithName("Restock")
      .WithOpenApi();

    return app;
  }

  private static void ResetTimer(
    int productId,
    IWarehouseStockSystemClient client,
    IMemoryCache cache,
    ConcurrentDictionary<int, Timer> timers
  )
  {
    if (timers.TryGetValue(productId, out var existingTimer))
    {
      existingTimer.Change(100, Timeout.Infinite);
    }
    else
    {
      var newTimer = new Timer(
        async state =>
        {
          if (state != null)
          {
            var pid = (int)state;
            if (cache.TryGetValue(pid, out int stock))
            {
              await client.UpdateStock(pid, stock);
            }
          }
        },
        productId,
        100,
        Timeout.Infinite
      );
      timers[productId] = newTimer;
    }
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);
