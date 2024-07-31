namespace CachedInventory;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

public interface ILegacySyncService
{
  Task UpdateCache();
  void MarkForUpdate(int productId);
}

public class TrackerLegacySync(IWarehouseStockSystemClient client, IOperationsTracker tracker)
  : ILegacySyncService
{
  private readonly SemaphoreSlim semaphore = new(1);
  private readonly Dictionary<int, DateTime> updateQueue = new();
  private DateTime lastUpdatedAt = DateTime.UtcNow;

  public async Task UpdateCache()
  {
    int[] toUpdate;
    try
    {
      await semaphore.WaitAsync();
      toUpdate = updateQueue.Where(kv => kv.Value < lastUpdatedAt).Select(kv => kv.Key).ToArray();
      lastUpdatedAt = DateTime.UtcNow;
    }
    finally
    {
      semaphore.Release();
    }

    await Task.WhenAll(
      toUpdate.Select(async id =>
      {
        var actions = await tracker.GetActionsByProductId(id);
        var stock = actions.Sum();
        await client.UpdateStock(id, stock);
      })
    );
  }

  public void MarkForUpdate(int productId)
  {
    try
    {
      semaphore.Wait();
      updateQueue[productId] = DateTime.UtcNow;
    }
    finally
    {
      semaphore.Release();
    }
  }
}

public record ProductCacheItem(int ProductId, int Stock);

public static class CachedInventoryApiBuilder
{
  private static readonly ConcurrentDictionary<int, ProductCacheItem> ProductCache = new();
  private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProductLocks = new();

  public static WebApplication Build(string[] args)
  {
    var builder = WebApplication.CreateBuilder(args);

    // Add services to the container.
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddSingleton<IWarehouseStockSystemClient, WarehouseStockSystemClient>();
    builder.Services.AddSingleton<IOperationsTracker, OperationsTracker>();
    builder.Services.AddSingleton<ILegacySyncService, TrackerLegacySync>();
    builder.Services.AddMemoryCache();

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      app.UseSwagger();
      app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();

    app.MapGet(
        "/stock/{productId:int}",
        async (
          [FromServices] IWarehouseStockSystemClient client,
          [FromServices] IOperationsTracker tracker,
          int productId
        ) => await GetStockWithCache(client, tracker, productId)
      )
      .WithName("GetStock")
      .WithOpenApi();

    app.MapPost(
        "/stock/retrieve",
        async (
          [FromServices] IWarehouseStockSystemClient client,
          [FromServices] IOperationsTracker tracker,
          [FromServices] ILegacySyncService syncService,
          [FromBody] RetrieveStockRequest req
        ) =>
        {
          var semaphore = ProductLocks.GetOrAdd(req.ProductId, _ => new(1));
          try
          {
            await semaphore.WaitAsync();
            var stock = await GetStockWithCache(client, tracker, req.ProductId);
            if (stock < req.Amount)
            {
              return Results.BadRequest("Not enough stock.");
            }

            await tracker.CreateOperationsTracker(DateTime.UtcNow, req.ProductId, -req.Amount);
            ProductCache.AddOrUpdate(
              req.ProductId,
              new ProductCacheItem(req.ProductId, -req.Amount),
              (_, item) => item with { Stock = item.Stock - req.Amount }
            );
            syncService.MarkForUpdate(req.ProductId);
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
          [FromServices] IOperationsTracker tracker,
          [FromServices] ILegacySyncService syncService,
          [FromBody] RestockRequest req
        ) =>
        {
          var semaphore = ProductLocks.GetOrAdd(req.ProductId, _ => new(1));
          try
          {
            await semaphore.WaitAsync();
            await tracker.CreateOperationsTracker(DateTime.UtcNow, req.ProductId, req.Amount);
            syncService.MarkForUpdate(req.ProductId);
            ProductCache.AddOrUpdate(
              req.ProductId,
              new ProductCacheItem(req.ProductId, req.Amount),
              (_, item) => item with { Stock = item.Stock + req.Amount }
            );
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

    var legacySyncService = app.Services.GetRequiredService<ILegacySyncService>();

    _ = Task.Run(async () =>
    {
      while (true)
      {
        try
        {
          await Task.Delay(10);
          await legacySyncService.UpdateCache();
        }
        catch (Exception e)
        {
          Console.WriteLine(e);
        }
      }
      // ReSharper disable once FunctionNeverReturns
    });

    app.Lifetime.ApplicationStopping.Register(() =>
    {
      using var scope = app.Services.CreateScope();
      var trackerService = scope.ServiceProvider.GetRequiredService<IOperationsTracker>();
      trackerService.RemoveCache().GetAwaiter().GetResult();
    });
    return app;
  }

  public static async Task<int> GetStockWithCache(
    IWarehouseStockSystemClient client,
    IOperationsTracker tracker,
    int productId
  )
  {
    if (ProductCache.TryGetValue(productId, out var cacheItem))
    {
      var actions = await tracker.GetActionsByProductId(productId);
      return cacheItem.Stock + actions.Sum();
    }

    var stock = await client.GetStock(productId);
    ProductCache.TryAdd(productId, new(productId, stock));
    return stock;
  }
}

public record RetrieveStockRequest(int ProductId, int Amount);

public record RestockRequest(int ProductId, int Amount);
