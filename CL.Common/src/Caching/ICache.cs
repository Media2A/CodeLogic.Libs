namespace CL.Common.Caching;

/// <summary>
/// Defines a generic asynchronous cache contract supporting typed values, TTL-based expiry,
/// prefix-based removal, and count queries.
/// Implementations include <see cref="MemoryCache"/> for in-process caching.
/// </summary>
public interface ICache
{
    /// <summary>
    /// Stores a value in the cache under the given key.
    /// If the key already exists it is overwritten.
    /// </summary>
    /// <typeparam name="T">The type of value to store.</typeparam>
    /// <param name="key">The cache key. Must not be null or empty.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttl">
    /// Optional time-to-live. The entry expires and is removed after this duration.
    /// When null the entry does not expire.
    /// </param>
    /// <returns>A <see cref="Task"/> that completes when the value has been stored.</returns>
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null);

    /// <summary>
    /// Retrieves a value from the cache by key.
    /// Returns the default value for <typeparamref name="T"/> when the key is not found or has expired.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key to look up. Must not be null or empty.</param>
    /// <returns>The cached value, or <c>default</c> if not found or expired.</returns>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// Checks whether a key exists in the cache and has not expired.
    /// </summary>
    /// <param name="key">The cache key to check. Must not be null or empty.</param>
    /// <returns><c>true</c> if the key exists and is not expired; otherwise <c>false</c>.</returns>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// Removes a single entry from the cache by key.
    /// Does nothing if the key does not exist.
    /// </summary>
    /// <param name="key">The cache key to remove. Must not be null or empty.</param>
    /// <returns>A <see cref="Task"/> that completes when the entry has been removed.</returns>
    Task RemoveAsync(string key);

    /// <summary>
    /// Removes all cache entries whose keys begin with the specified prefix.
    /// </summary>
    /// <param name="prefix">The key prefix to match. Must not be null or empty.</param>
    /// <returns>A <see cref="Task"/> that completes when all matching entries have been removed.</returns>
    Task RemoveByPrefixAsync(string prefix);

    /// <summary>
    /// Removes all entries from the cache regardless of their TTL.
    /// </summary>
    /// <returns>A <see cref="Task"/> that completes when the cache has been cleared.</returns>
    Task ClearAsync();

    /// <summary>
    /// Returns the number of non-expired entries currently in the cache.
    /// </summary>
    /// <returns>A <see cref="Task{TResult}"/> whose result is the count of live cache entries.</returns>
    Task<int> GetCountAsync();
}
