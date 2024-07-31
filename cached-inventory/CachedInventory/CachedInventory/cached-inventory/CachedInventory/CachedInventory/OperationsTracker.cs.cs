namespace CachedInventory;

using System.Text.Json;

public interface IOperationsTracker
{
  Task<int[]> GetActionsByProductId(int productId);
  Task<string> CreateOperationsTracker(DateTime time, int productId, int action);
  Task RemoveCache();
}

public class OperationsTracker : IOperationsTracker
{
  private static readonly string FileName = "operations-tracker.json";
  private static readonly SemaphoreSlim Semaphore = new(1);

  public async Task<int[]> GetActionsByProductId(int productId)
  {
    try
    {
      await Semaphore.WaitAsync();
      var operations = await ReadOperationsFromFile();
      return operations
        .Where(op => op.ProductId == productId && op is { Ok: true, InCache: true })
        .Select(op => op.Action)
        .ToArray();
    }
    catch
    {
      return [];
    }
    finally
    {
      Semaphore.Release();
    }
  }

  public async Task<string> CreateOperationsTracker(DateTime time, int productId, int action) =>
    await UpdateOperations(operations =>
    {
      var newOperation = new Operation
      {
        Id = Guid.NewGuid().ToString(),
        Time = time,
        ProductId = productId,
        Action = action
      };
      operations.Add(newOperation);
      return newOperation.Id;
    });

  public async Task RemoveCache() =>
    await UpdateOperations(operations =>
    {
      foreach (var operation in operations)
      {
        operation.InCache = false;
      }

      return 0;
    });

  private async Task<List<Operation>> ReadOperationsFromFile()
  {
    if (!File.Exists(FileName))
    {
      return [];
    }

    var json = await File.ReadAllTextAsync(FileName);
    return JsonSerializer.Deserialize<List<Operation>>(json) ?? [];
  }

  private async Task<T> UpdateOperations<T>(Func<List<Operation>, T> updater)
  {
    try
    {
      await Semaphore.WaitAsync();
      var operations = await ReadOperationsFromFile();
      var returnValue = updater(operations);
      var json = JsonSerializer.Serialize(
        operations,
        new JsonSerializerOptions { WriteIndented = true }
      );
      await File.WriteAllTextAsync(FileName, json);
      return returnValue;
    }
    finally
    {
      Semaphore.Release();
    }
  }

  private class Operation
  {
    public required string Id { get; set; }
    public DateTime Time { get; set; }
    public bool Ok { get; set; } = true;
    public int ProductId { get; set; }
    public int Action { get; set; }
    public bool InCache { get; set; } = true;
  }
}
