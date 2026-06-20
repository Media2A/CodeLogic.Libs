# CL.PostgreSQL — Query Builder

> Typed LINQ-shaped expressions translated to real SQL — filters, ordering, paging, joins, projections, aggregates, bulk writes, raw SQL, and transactions.

See the [overview](index.md) for loading, repositories, configuration, and events.

`pg.Query<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to server-side SQL — there is no client-side filtering — and materializes rows into `T`.

```csharp
var pg = Libraries.Get<PostgreSQLLibrary>();

Result<List<User>> users = await pg.Query<User>()
    .Where(u => u.IsActive && u.LoginCount > 10)
    .OrderByDescending(u => u.Name)
    .Take(50)
    .ToListAsync();

if (users.IsSuccess)
    foreach (var u in users.Value!) { /* … */ }
```

## Filtering with `Where`

`Where` takes an `Expression<Func<T, bool>>` and translates it to a parameterized `WHERE` clause. Chained calls are AND-combined.

```csharp
pg.Query<User>()
    .Where(u => u.IsActive)
    .Where(u => u.LoginCount >= 1 && u.LoginCount < 1000)
    .Where(u => u.Name != null);
```

Supported expression shapes include comparisons, `&&` / `||`, `!`, `string` methods (`Contains` / `StartsWith` / `EndsWith` → `LIKE`), and null checks (→ `IS NULL` / `IS NOT NULL`). Captured local variables are parameterized.

## Ordering & paging

```csharp
pg.Query<User>()
    .OrderBy(u => u.Name)
    .OrderByDescending(u => u.LoginCount)
    .Skip(40)         // alias: Offset(40)
    .Take(20);        // alias: Limit(20)
```

`OrderBy` / `OrderByDescending` take a key selector. `Take`/`Limit` and `Skip`/`Offset` are aliases for `LIMIT` and `OFFSET`. For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<User>> page = await pg.Query<User>()
    .Where(u => u.IsActive)
    .OrderByDescending(u => u.Name)
    .ToPagedListAsync(page: 1, pageSize: 25);

PagedResult<User> p = page.Value!;
// p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages, p.HasPreviousPage, p.HasNextPage
```

## Joins & projections

`Join(table, condition, JoinType)` appends a literal join clause for ad-hoc joins outside the typed model. `Select` emits an explicit projection column list.

```csharp
pg.Query<Order>()
    .Join("customers c", "c.\"Id\" = t.\"CustomerId\"", JoinType.Left)
    .Where(o => o.Total > 100)
    .Select(o => new { o.Id, o.Total });
```

`JoinType` is `Inner` (default), `Left`, `Right`, or `Cross`.

## Grouping

`GroupBy<TKey>` adds a `GROUP BY` clause; combine it with the aggregate terminals below.

```csharp
pg.Query<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.CustomerId);
```

## Terminal operations

Nothing runs until a terminal is awaited.

| Terminal | Returns | SQL |
|----------|---------|-----|
| `ToListAsync(ct)` | `Result<List<T>>` | `SELECT …` |
| `FirstOrDefaultAsync(ct)` | `Result<T?>` | `SELECT … LIMIT 1` |
| `ToPagedListAsync(page, pageSize, ct)` | `Result<PagedResult<T>>` | data page + `COUNT(*)` |
| `CountAsync(ct)` | `Result<long>` | `SELECT COUNT(*)` |
| `MaxAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MAX(col)` |
| `MinAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT MIN(col)` |
| `SumAsync<TResult>(selector, ct)` | `Result<TResult>` | `SELECT SUM(col)` |
| `AverageAsync<TResult>(selector, ct)` | `Result<double>` | `SELECT AVG(col)` |
| `UpdateAsync(updates, ct)` | `Result<int>` | bulk `UPDATE` |
| `DeleteAsync(ct)` | `Result<int>` | bulk `DELETE` |

```csharp
Result<User?>   first = await pg.Query<User>().Where(u => u.Id == 1).FirstOrDefaultAsync();
Result<long>    open  = await pg.Query<User>().Where(u => u.IsActive).CountAsync();
Result<int>     top   = await pg.Query<User>().MaxAsync(u => u.LoginCount);
Result<int>     total = await pg.Query<User>().SumAsync(u => u.LoginCount);
Result<double>  avg   = await pg.Query<User>().AverageAsync(u => u.LoginCount);
```

> Both the repository's `CountAsync` and the **query builder's** `CountAsync` return `Result<long>`. Use the builder when you need to count a filtered set.

## Bulk update & delete

The builder runs set-based mutations server-side without materializing rows. Both honour the chained `Where` filter.

```csharp
// Bulk update via an explicit column map
Result<int> archived = await pg.Query<User>()
    .Where(u => !u.IsActive)
    .UpdateAsync(new Dictionary<string, object?> { ["Status"] = "archived" });

// Bulk delete
Result<int> purged = await pg.Query<User>()
    .Where(u => !u.IsActive)
    .DeleteAsync();
```

> A bulk `UpdateAsync` / `DeleteAsync` without a `Where` filter targets the **whole table**. Filter deliberately.

## Raw SQL escape hatch

When the builder can't express something, `pg.QueryRaw()` returns a non-generic `QueryBuilder` for parameterized raw SQL. Rows come back as dictionaries keyed by column name.

```csharp
var raw = pg.QueryRaw();   // optional connectionId

// Query → list of column→value dictionaries
Result<List<Dictionary<string, object?>>> rows = await raw.QueryAsync(
    "SELECT \"Name\", \"LoginCount\" FROM \"users\" WHERE \"Id\" = @id",
    new() { ["@id"] = 1 });

// Non-query → affected rows
Result<int> affected = await raw.ExecuteAsync("TRUNCATE \"users\"");
```

To materialize raw SQL straight into an entity instead of dictionaries, use the repository's `RawQueryAsync` / `RawExecuteAsync`:

```csharp
var repo = pg.GetRepository<User>();
Result<List<User>> users = await repo.RawQueryAsync(
    "SELECT * FROM \"users\" WHERE \"Name\" = @n", new() { ["@n"] = "Ada" });
Result<int> n = await repo.RawExecuteAsync("UPDATE \"users\" SET \"IsActive\" = false");
```

## Transactions

`BeginTransactionAsync` returns a `TransactionScope` (an `IAsyncDisposable`). Commit explicitly; if the scope is disposed without a commit it rolls back automatically.

```csharp
await using TransactionScope tx = await pg.BeginTransactionAsync();   // optional connectionId
try
{
    var raw = pg.QueryRaw();
    await raw.ExecuteAsync("UPDATE \"accounts\" SET \"Balance\" = \"Balance\" - @amt WHERE \"Id\" = @from",
        new() { ["@amt"] = 100m, ["@from"] = 1 });
    await raw.ExecuteAsync("UPDATE \"accounts\" SET \"Balance\" = \"Balance\" + @amt WHERE \"Id\" = @to",
        new() { ["@amt"] = 100m, ["@to"] = 2 });

    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();   // or just let the scope dispose
    throw;
}
```

`TransactionScope` exposes `CommitAsync(ct)` and `RollbackAsync(ct)`; disposal without a commit triggers an automatic rollback.

## Choosing a connection

Every entry point accepts a `connectionId` selecting a named database from `config.postgresql.json`; it defaults to `"Default"`. On the builder, `.WithConnection("Reporting")` does the same fluently.

```csharp
var reports = pg.Query<Sale>().WithConnection("Reporting");
var repo    = pg.GetRepository<Sale>("Reporting");
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.PostgreSQL)
