using System.Collections.Concurrent;

namespace CachedInventory
{
  public class ConcurrentCache<T>
  {
    private readonly ConcurrentDictionary<int, Task<T>> _cache = new();

    public async Task<T> GetOrAddAsync(int key, Func<int, Task<T>> valueFactory)
    {
      return await _cache.GetOrAdd(key, async k => await valueFactory(k));
    }

    public bool TryGetValue(int key, out Task<T> value)
    {
      return _cache.TryGetValue(key, out value);
    }

    public void AddOrUpdate(int key, T value)
    {
      _cache.AddOrUpdate(key, Task.FromResult(value), (oldKey, oldValue) => Task.FromResult(value));
    }
  }
}
