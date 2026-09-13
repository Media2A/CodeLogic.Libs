# CL.SQLite — Query Builder

> Typed LINQ-shaped expressions translated to SQL — filters, multi-key ordering, projections, `GroupBy` aggregates, paging, bulk update & delete, and raw SQL.

See the [overview](index.md) for loading, the repository, schema sync, configuration, and events.

`db.GetQueryBuilder<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to a single parameterized SQL statement, runs it against the embedded SQLite engine, and materializes rows into `T`.

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

Supported expression shapes:

| Shape | Translates to |
|-------|---------------|
| `==` `!=` `<` `<=` `>` `>=` | the matching SQL operator |
| `&&` `\|\|` `!` | `AND` / `OR` / `NOT (…)` |
| `x.Flag` on a `bool` member | `"Flag" = @p` bound to `true` |
| `x.Name.Contains/StartsWith/EndsWith(s)` | `LIKE '%s%'` / `'s%'` / `'%s'` |
| `== null` / `!= null` | `IS NULL` / `IS NOT NULL` |
| `collection.Contains(x.Id)` — array, `List<T>`, `HashSet<T>`, any `IEnumerable` | `"Id" IN (@p0, @p1, …)`, one bound parameter per element |

Captured local variables and expressions such as `DateTime.UtcNow.AddDays(-30)` are evaluated at build time and bound as parameters — values never land in the SQL string. An `IN` over an **empty** collection emits the literal `1=0`, matching nothing, rather than invalid SQL.

Anything else — another method call, arithmetic on a column, or comparing two columns to each other — raises `NotSupportedException`. Note where it surfaces: `QueryBuilder.Where` translates the expression **eagerly**, so an unsupported predicate throws out of the `Where` call itself rather than arriving as a failed `Result` from the terminal. (`Repository.FindAsync` translates inside its own `try`, so there the same predicate comes back as a failed `Result`.)

## Ordering

Order by one key with `OrderBy` / `OrderByDescending`, then add tie-breakers with `ThenBy` / `ThenByDescending`. Each takes a key selector.

```csharp
db.GetQueryBuilder<Order>()
    .OrderBy(o => o.Status)
    .ThenByDescending(o => o.CreatedUtc)
    .ThenBy(o => o.Id);
```

## Paging

`Limit` / `Take` and `Offset` / `Skip` are aliases for SQL `LIMIT` and `OFFSET`. An offset with no limit is legal — SQLite's grammar is `LIMIT expr [OFFSET expr]`, so it is emitted as `LIMIT -1 OFFSET n`, meaning "skip n, then everything":

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

`Select` takes an `Expression<Func<T, object?>>` and restricts the emitted column list to just the members the projection touches, instead of the default `SELECT *`.

```csharp
db.GetQueryBuilder<Order>()
    .Select(o => o.Total);                  // SELECT "total"
```

Rows are still materialized into `T`; columns you did not select are simply left at their default values on the returned instances.

Anonymous-type projections work the same way: every member — single or anonymous — is resolved back to the **source entity's** mapped `ColumnName`, and each emitted name is double-quoted, so renamed columns and reserved words are both safe.

```csharp
db.GetQueryBuilder<Order>()
    .Select(o => new { o.Id, o.CreatedUtc });   // SELECT "id", "created_utc"
```

## Aggregates — `GroupBy`

`GroupBy<TKey>` adds a `GROUP BY` clause to the **row-returning** terminals — `ToListAsync` and `ToPagedListAsync`.

```csharp
db.GetQueryBuilder<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.Day);
```

`GroupBy` also reaches the two counting terminals, where it changes what is being counted:

- `CountAsync` on a grouped builder returns the **number of groups**, not the number of rows — the grouped query is wrapped in `SELECT COUNT(*) FROM (…)`.
- `ToPagedListAsync` reports the same number as `TotalItems`, so paging through grouped rows pages correctly.

> **The typed aggregates refuse a grouped builder.** `SumAsync`, `MinAsync` and `MaxAsync` throw `NotSupportedException` when the builder carries a `GroupBy`, because a per-group aggregate has no single scalar answer and silently dropping the grouping would answer a different question. Read the grouped rows with `ToListAsync`, or compute per-group aggregates with `RawQueryAsync`.

The aggregate terminals evaluate in the database over the current `WHERE`:

```csharp
Result<decimal> revenue = await db.GetQueryBuilder<Order>()
    .Where(o => o.Day == today)
    .SumAsync(o => o.Total);

Result<decimal> top = await db.GetQueryBuilder<Order>().MaxAsync(o => o.Total);
Result<decimal> low = await db.GetQueryBuilder<Order>().MinAsync(o => o.Total);
```

## Terminal operations

Nothing runs until a terminal is called; each returns a `Result<…>`. There is no `AverageAsync`, `AnyAsync` or `SingleAsync` on this builder — `SUM`, `MAX` and `MIN` are the only aggregate terminals.

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT … LIMIT 1`; the limit is local to the call, so the builder can be reused |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)`; fails validation if `page` or `pageSize` < 1 |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)`; with `GroupBy`, counts groups |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)`; throws `NotSupportedException` with `GroupBy` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)`; throws `NotSupportedException` with `GroupBy` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)`; throws `NotSupportedException` with `GroupBy` |
| `DeleteAsync(ct)` | `Result<int>` | `DELETE … WHERE …` |
| `UpdateAsync(updates, ct)` | `Result<int>` | `UPDATE … SET … WHERE …` |

```csharp
Result<long>  open  = await db.GetQueryBuilder<Order>().Where(o => o.Status == "open").CountAsync();
Result<Order?> first = await db.GetQueryBuilder<Order>().Where(o => o.Id == 1).FirstOrDefaultAsync();
```

## Bulk update & delete

`DeleteAsync` and `UpdateAsync` issue a single set-based statement without materializing rows. Both return the number of affected rows. `UpdateAsync` takes an explicit column map whose keys are **column names** as they exist in the table (not C# property names); they are quoted and emitted into the `SET` list, with values bound as positional parameters (`@p0`, `@p1`, …) so that a column name which is not a legal parameter token still works. The names themselves are still emitted into the statement, so pass literal column names you control, never user input.

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
