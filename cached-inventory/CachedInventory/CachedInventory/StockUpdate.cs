using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CachedInventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class BackgroundStockUpdateService : BackgroundService
{
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly Dictionary<int, int> _updates = new Dictionary<int, int>();
  private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
  private readonly ILogger<BackgroundStockUpdateService> _logger;
  private readonly TimeSpan _waitTime = TimeSpan.FromSeconds(9);

  public BackgroundStockUpdateService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundStockUpdateService> logger
  )
  {
    _scopeFactory = scopeFactory;
    _logger = logger;
  }

  public async Task QueueStockUpdate(int productId, int amount)
  {
    _logger.LogWarning("New request with Id {productId} to amount {Amount}.", productId, amount);
    await _semaphore.WaitAsync();
    try
    {
      _updates[productId] = amount; // Reemplaza la cantidad existente para el producto
    }
    finally
    {
      _semaphore.Release();
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var scope = _scopeFactory.CreateScope();
    var warehouseStockSystem =
      scope.ServiceProvider.GetRequiredService<IWarehouseStockSystemClient>();

    while (!stoppingToken.IsCancellationRequested)
    {
      await Task.Delay(_waitTime, stoppingToken);
      await ProcessUpdatesAsync(warehouseStockSystem, stoppingToken);
    }
  }

  private async Task ProcessUpdatesAsync(
    IWarehouseStockSystemClient warehouseStockSystem,
    CancellationToken stoppingToken
  )
  {
    Dictionary<int, int> updatesToProcess;
    await _semaphore.WaitAsync(stoppingToken);
    try
    {
      updatesToProcess = new Dictionary<int, int>(_updates);
      _updates.Clear();
    }
    finally
    {
      _semaphore.Release();
    }

    try
    {
      foreach (var keyValue in updatesToProcess)
      {
        _logger.LogInformation(
          "Updating product with Id {ProductId} to amount {Amount}.",
          keyValue.Key,
          keyValue.Value
        );
        await warehouseStockSystem.UpdateStock(keyValue.Key, keyValue.Value);
        _logger.LogInformation("Product with Id {ProductId} finished updating.", keyValue.Key);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error processing stock updates");
    }
  }
}
