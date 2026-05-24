namespace CL.MySQL2.Core;

/// <summary>
/// Generates time-ordered UUIDv7 values optimized for use as BINARY(16) primary keys.
/// UUIDv7 embeds a Unix millisecond timestamp in the high bits, so sequential inserts
/// append to the B-tree instead of causing random page splits — dramatically reducing
/// index fragmentation compared to random UUIDv4.
/// </summary>
public static class SequentialGuid
{
    /// <summary>
    /// Creates a new UUIDv7 (time-ordered, random tail). Use this instead of
    /// <see cref="Guid.NewGuid()"/> for columns with <c>StorageType = StorageType.Binary</c>
    /// to get optimal insert performance and natural time-based ordering.
    /// </summary>
    public static Guid NewId() => Guid.CreateVersion7();

    /// <summary>
    /// Creates a new UUIDv7 anchored to a specific timestamp. Useful for deterministic
    /// testing or back-dating records.
    /// </summary>
    public static Guid NewId(DateTimeOffset timestamp) => Guid.CreateVersion7(timestamp);
}
