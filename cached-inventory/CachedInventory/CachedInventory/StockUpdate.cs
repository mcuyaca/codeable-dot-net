using System.Collections.Concurrent;
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
  private readonly ConcurrentDictionary<int, int> _updates = new ConcurrentDictionary<int, int>();
  private readonly ILogger<BackgroundStockUpdateService> _logger;
  private readonly TimeSpan _waitTime = TimeSpan.FromSeconds(3);

  public BackgroundStockUpdateService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundStockUpdateService> logger
  )
  {
    _scopeFactory = scopeFactory;
    _logger = logger;
  }

  public void QueueStockUpdate(int productId, int amount)
  {
    _logger.LogWarning("New request with Id {productId} to amount {Amount}.", productId, amount);
    _updates[productId] = amount; // Reemplaza la cantidad existente para el producto
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
    try
    {
      foreach (var keyValue in _updates.ToList())
      {
        if (_updates.TryRemove(keyValue.Key, out int amount))
        {
          _logger.LogInformation(
            "Updating product with Id {ProductId} to amount {Amount}.",
            keyValue.Key,
            amount
          );
          await warehouseStockSystem.UpdateStock(keyValue.Key, amount);
          _logger.LogInformation("Product with Id {ProductId} finished updating.", keyValue.Key);
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error processing stock updates");
    }
  }
}
