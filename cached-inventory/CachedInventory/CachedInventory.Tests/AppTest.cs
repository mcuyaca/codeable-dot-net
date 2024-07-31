// ReSharper disable ClassNeverInstantiated.Global

namespace CachedInventory.Tests;

public class SingleRetrieval
{
  [Fact(DisplayName = "retirar un producto")]
  public static async Task Test() => await TestApiPerformance.Test(1, [3], false, 2_000);
}

public class FourRetrievalsInParallel
{
  [Fact(DisplayName = "retirar cuatro productos en paralelo")]
  public static async Task Test() => await TestApiPerformance.Test(2, [1, 2, 3, 4], true, 1_000);
}

public class FourRetrievalsSequentially
{
  [Fact(DisplayName = "retirar cuatro productos secuencialmente")]
  public static async Task Test() => await TestApiPerformance.Test(3, [1, 2, 3, 4], false, 1_000);
}

public class SevenRetrievalsInParallel
{
  [Fact(DisplayName = "retirar siete productos en paralelo")]
  public static async Task Test() =>
    await TestApiPerformance.Test(4, [1, 2, 3, 4, 5, 6, 7], true, 500);
}

public class SevenRetrievalsSequentially
{
  [Fact(DisplayName = "retirar siete productos secuencialmente")]
  public static async Task Test() =>
    await TestApiPerformance.Test(5, [1, 2, 3, 4, 5, 6, 7], false, 500);
}

public class NotEnoughStock
{
  [Fact(DisplayName = "retirar un producto sin stock")]
  public static async Task Test() => await TestApiPerformance.Test(6, [7], false, 200, 6);
}

internal static class TestApiPerformance
{
  internal static async Task Test(
    int productId,
    int[] retrievals,
    bool isParallel,
    long expectedPerformance,
    int initialRetrive = 0
  )
  {
    await using var setup = await TestSetup.Initialize();
    await setup.Restock(productId, retrievals.Sum() - initialRetrive);
    await setup.VerifyStockFromFile(productId, retrievals.Sum() - initialRetrive);
    var tasks = new List<Task>();
    foreach (var retrieval in retrievals)
    {
      Task task;
      if (initialRetrive == 0)
      {
        task = setup.Retrieve(productId, retrieval);
      }
      else
      {
        task = setup.Retrieve(productId, retrieval, true);
      }

      if (!isParallel)
      {
        await task;
      }

      tasks.Add(task);
    }

    await Task.WhenAll(tasks);
    var finalStock = await setup.GetStock(productId);
    if (initialRetrive == 0)
    {
      Assert.True(finalStock == 0, $"El stock final no es 0, sino {finalStock}.");
    }

    Assert.True(
      setup.AverageRequestDuration < expectedPerformance,
      $"Duración promedio: {setup.AverageRequestDuration}ms, se esperaba un máximo de {expectedPerformance}ms."
    );

    await setup.VerifyStockFromFile(productId, finalStock);
  }
}
