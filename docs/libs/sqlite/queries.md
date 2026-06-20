# CL.SQLite — Query Builder

> Typed LINQ-shaped expressions translated to SQL — filters, multi-key ordering, projections, `GroupBy` aggregates, paging, bulk update & delete, and raw SQL.

See the [overview](index.md) for loading, the repository, schema sync, configuration, and events.

`db.GetQueryBuilder<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to a parameterized SQL statement on the server side and materializes rows into `T`.

```csharp
var db = Libraries.Get<SQLiteLibrary>();

Result<List<Order>> orders = await db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open" && o.Total > 100)
    .OrderByDescending(o => o.CreatedUtc)
    .Take(50)
    .ToListAsync();

if (orders.IsSuccess)
    foreach (var o in orders.Value!) { /* … */ }
```

Each entry point accepts a `connectionId` selecting a named database from `config.sqlite.json`; it defaults to `"Default"`. `db.GetQueryBuilder<Order>("Reporting")` queries the `Reporting` database.

## Filtering with `Where`

`Where` takes an `Expression<Func<T, bool>>` and translates it to a parameterized `WHERE` clause. Chained calls are AND-combined.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && o.Total < 1000)
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
```

Supported expression shapes include comparisons, `&&` / `||` / `!`, `string` methods (`Contains` / `StartsWith` / `EndsWith` → `LIKE`), and null checks (→ `IS NULL` / `IS NOT NULL`). Captured local variables and `DateTime.UtcNow`-relative expressions are parameterized — values never land in the SQL string.

## Ordering

Order by one key with `OrderBy` / `OrderByDescending`, then add tie-breakers with `ThenBy` / `ThenByDescending`. Each takes a key selector.

```csharp
db.GetQueryBuilder<Order>()
    .OrderBy(o => o.Status)
    .ThenByDescending(o => o.CreatedUtc)
    .ThenBy(o => o.Id);
```

## Paging

`Limit` / `Take` and `Offset` / `Skip` are aliases for SQL `LIMIT` and `OFFSET`:

```csharp
db.GetQueryBuilder<Order>()
    .OrderByDescending(o => o.CreatedUtc)
    .Skip(40)        // alias: Offset(40)
    .Take(20);       // alias: Limit(20)
```

For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<Order>> page = await db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);

if (page.IsSuccess)
{
    PagedResult<Order> p = page.Value!;
    // p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages
}
```

## Projections — `Select`

`Select` takes an `Expression<Func<T, object?>>` and restricts the emitted column list to just the members the projection touches, instead of selecting every mapped column.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.Status == "open")
    .Select(o => new { o.Id, o.Total });
```

## Aggregates — `GroupBy`

`GroupBy<TKey>` adds a `GROUP BY` clause; pair it with `CountAsync` or the typed aggregate terminals.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.Day);
```

The aggregate terminals run server-side over the current `WHERE` (and grouping):

```csharp
Result<decimal> revenue = await db.GetQueryBuilder<Order>()
    .Where(o => o.Day == today)
    .SumAsync(o => o.Total);

Result<decimal> top = await db.GetQueryBuilder<Order>().MaxAsync(o => o.Total);
Result<decimal> low = await db.GetQueryBuilder<Order>().MinAsync(o => o.Total);
```

## Terminal operations

Nothing runs until a terminal is called; each returns a `Result<…>`.

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT … LIMIT 1` |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)` |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)` |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)` |
| `DeleteAsync(ct)` | `Result<int>` | `DELETE … WHERE …` |
| `UpdateAsync(updates, ct)` | `Result<int>` | `UPDATE … SET … WHERE …` |

```csharp
Result<long>  open  = await db.GetQueryBuilder<Order>().Where(o => o.Status == "open").CountAsync();
Result<Order?> first = await db.GetQueryBuilder<Order>().Where(o => o.Id == 1).FirstOrDefaultAsync();
```

## Bulk update & delete

`DeleteAsync` and `UpdateAsync` run set-based mutations server-side without materializing rows. Both return the number of affected rows. `UpdateAsync` takes an explicit column map.

```csharp
// Bulk delete by predicate
Result<int> purged = await db.GetQueryBuilder<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();

// Bulk update by predicate, via an explicit column map
Result<int> renamed = await db.GetQueryBuilder<Note>()
    .Where(n => n.Title == "")
    .UpdateAsync(new Dictionary<string, object?> { ["title"] = "(untitled)" });
```

> A `DeleteAsync` or `UpdateAsync` with no `Where` affects every row in the table. Always scope bulk mutations with a predicate unless that is genuinely intended.

## Raw SQL escape hatches

When the builder can't express something, drop to parameterized raw SQL on the repository. Both take named parameters as a `Dictionary<string, object?>`.

```csharp
var repo = db.GetRepository<UserRecord>();

// Materialize rows into T with the same mapper as the builder
Result<List<UserRecord>> rows = await repo.RawQueryAsync(
    "SELECT * FROM users WHERE country = @c AND created_utc >= @since",
    new() { ["@c"] = "DK", ["@since"] = since });

// Non-query — returns affected rows
Result<int> n = await repo.RawExecuteAsync(
    "UPDATE users SET active = 0 WHERE last_seen < @cutoff",
    new() { ["@cutoff"] = cutoff });
```

Always bind values via named parameters — never interpolate user input into the SQL string.

## See also

- [Overview](index.md) — loading, the repository, schema sync, configuration, and events.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.SQLite)
