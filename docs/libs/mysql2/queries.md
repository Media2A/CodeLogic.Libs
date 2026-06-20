# CL.MySQL2 — Query Builder

> Typed LINQ-shaped expressions translated to real SQL — filters, subqueries, ordering, paging, joins, projections, aggregates, bulk writes, raw SQL, and transactions.

See the [overview](index.md) for loading, repositories, configuration, and events.

`mysql.Query<T>()` returns a `QueryBuilder<T>` you compose fluently. Nothing executes until a terminal method runs; each terminal returns a `Result<…>`. The builder translates expressions to SQL on the server side — there is no client-side filtering — and materializes rows with a compiled, reflection-free mapper.

```csharp
var mysql = Libraries.Get<MySQL2Library>();

Result<List<Order>> orders = await mysql.Query<Order>()
    .Where(o => o.Status == "open" && o.Total > 100)
    .OrderByDescending(o => o.CreatedUtc)
    .Take(50)
    .ToListAsync();

if (orders.IsSuccess)
    foreach (var o in orders.Value!) { /* … */ }
```

## Filtering with `Where`

`Where` takes an `Expression<Func<T, bool>>` and translates it to a parameterized `WHERE` clause. Chained calls are AND-combined.

```csharp
mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .Where(o => o.Total >= 100 && o.Total < 1000)
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-30));
```

Supported expression shapes include comparisons, `&&` / `||`, `!`, `string` methods (`Contains` / `StartsWith` / `EndsWith` → `LIKE`), `Contains` over a collection (→ `IN (...)`, capped at `MaxInClauseValues`), and null checks (→ `IS NULL` / `IS NOT NULL`). Captured local variables and `DateTime.UtcNow`-relative expressions are parameterized.

## Subquery filters — `EXISTS` / `IN`

Four WHERE-family methods compile to real SQL subqueries against a different entity. They compose with ordinary `.Where(...)`.

```csharp
// Correlated EXISTS — correlate via the two-parameter predicate
mysql.Query<Order>()
    .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent");

mysql.Query<Order>()
    .WhereNotExists<Shipment>((o, s) => s.OrderId == o.Id);

// IN (subquery) — outer column matched against an inner column, with an optional inner filter
mysql.Query<Order>()
    .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip);

mysql.Query<Order>()
    .WhereNotIn<Customer, long>(o => o.CustomerId, c => c.Id);
```

- `WhereExists<TInner>` / `WhereNotExists<TInner>` → `[NOT] EXISTS (SELECT 1 FROM inner WHERE …)`.
- `WhereIn<TInner, TKey>` / `WhereNotIn<TInner, TKey>` → `col [NOT] IN (SELECT innerCol FROM inner [WHERE innerFilter])`.

> **Subquery-filtered queries are not cacheable** and cannot be turned into a typed `.Join` — the result cache stamps each entry with a single table's version counter, so it cannot invalidate on the inner table's mutations. `.WithCache` is silently bypassed on these. `WhereExists` against the outer query's own table is rejected (unqualified inner columns would be ambiguous).

## Ordering & paging

```csharp
mysql.Query<Order>()
    .OrderBy(o => o.CreatedUtc)
    .OrderByDescending(o => o.Total)
    .Skip(40)         // alias: Offset(40)
    .Take(20);        // alias: Limit(20)
```

`OrderBy` / `OrderByDescending` take a key selector. `Take`/`Limit` and `Skip`/`Offset` are aliases for `LIMIT` and `OFFSET`. For first-page metadata, use the paged terminal:

```csharp
Result<PagedResult<Order>> page = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .OrderByDescending(o => o.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 25);

PagedResult<Order> p = page.Value!;
// p.Items, p.PageNumber, p.PageSize, p.TotalItems, p.TotalPages, p.HasPreviousPage, p.HasNextPage
```

## Joins

### Typed joins

`Join<TRight, TKey, TResult>` translates a strongly-typed equi-join to SQL with table aliases and a compiled projection into `TResult` — only the columns the selector touches are transferred. It returns a `JoinedQuery<TLeft, TRight, TResult>`.

```csharp
Result<List<OrderView>> views = await mysql.Query<Order>()
    .Where(o => o.Total > 100)                  // carried filters re-qualified to the left table
    .Join<Customer, long, OrderView>(
        o => o.CustomerId,                       // left key
        c => c.Id,                               // right key
        (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name },
        JoinType.Inner)
    .Where((o, c) => c.IsVip)                    // two-parameter filters on the joined shape
    .OrderByDescending((o, c) => o.Total)
    .Take(20)
    .ToListAsync();
```

- **`JoinType`**: `Inner` (default), `Left`, `Right`, `Cross`. A keyed join implies an equi-join, so `Cross` is rejected there.
- **Composite keys**: `o => new { o.A, o.B }` matched positionally with `c => new { c.X, c.Y }`.
- **`TRight` must be specified explicitly** — it cannot be inferred from a lambda parameter type.
- **Fluent surface on the join**: `.Where((l, r) => …)`, `.OrderBy` / `.OrderByDescending((l, r) => …)`, `.Take` / `.Skip` / `.Limit` / `.Offset`, and the `ToListAsync` / `FirstOrDefaultAsync` / `CountAsync` terminals.

> **Joined queries are not cacheable.** `.WithCache` / `.SmartCache` are intentionally absent on `JoinedQuery` rather than risk serving stale joins — the single-table version stamp cannot detect mutations on the other side.

### Raw-string joins

For ad-hoc joins outside the typed model, the string overload appends a literal join clause:

```csharp
mysql.Query<Order>()
    .Join("customers c", "c.id = t0.customer_id", JoinType.Left);
```

## Projections — `Select`

`Select<TResult>` emits a real `SELECT col1, col2, …` column list (projection pushdown) and materializes into `TResult` — anonymous types or DTOs. It returns a `ProjectedQuery<TSource, TResult>`.

```csharp
Result<List<OrderSummary>> rows = await mysql.Query<Order>()
    .Where(o => o.Status == "open")
    .Select(o => new OrderSummary { Id = o.Id, Total = o.Total })
    .WithCache(TimeSpan.FromSeconds(30))     // projections of a single table are cacheable
    .ToListAsync();
```

`ProjectedQuery` exposes `WithCache(ttl)`, `SmartCache(pool)`, `ToListAsync`, and `FirstOrDefaultAsync`.

## Aggregates — `GroupBy`

`GroupBy<TKey>` returns a `GroupedQuery<TKey, TSource>`; its `Select` projects the grouping into a `ProjectedQuery` and translates to a real `GROUP BY` with aggregate functions — no client-side materialization.

```csharp
Result<List<DailyTotal>> daily = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .GroupBy(o => o.Day)
    .Select(g => new DailyTotal
    {
        Day      = g.Key,
        Count    = g.Count(),
        Revenue  = g.Sum(x => x.Total),
        AvgTotal = g.Average(x => x.Total),
        MaxTotal = g.Max(x => x.Total),
        MinTotal = g.Min(x => x.Total),
    })
    .ToListAsync();
```

Inside the projection use `g.Key`, `g.Sum(x => …)`, `g.Average(...)`, `g.Min(...)`, `g.Max(...)`, `g.Count()`, and `g.Any()`.

## Terminal operations

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

```csharp
Result<long>    open  = await mysql.Query<Order>().Where(o => o.Status == "open").CountAsync();
Result<decimal> top   = await mysql.Query<Order>().MaxAsync(o => o.Total);
Result<decimal> total = await mysql.Query<Order>().Where(o => o.Day == today).SumAsync(o => o.Total);
Result<double>  avg   = await mysql.Query<Order>().AverageAsync(o => o.Total);
```

## Bulk update & delete

The builder runs set-based mutations server-side without materializing rows.

```csharp
// Bulk update via a LINQ set expression
Result<int> repriced = await mysql.Query<Order>()
    .Where(o => o.Status == "draft")
    .UpdateAsync(o => new Order { Status = "open", UpdatedUtc = DateTime.UtcNow });

// Bulk update via an explicit column map
Result<int> flagged = await mysql.Query<Order>()
    .Where(o => o.Total > 10000)
    .UpdateAsync(new Dictionary<string, object?> { ["needs_review"] = true });

// Bulk delete (hard delete regardless of [SoftDelete])
Result<int> purged = await mysql.Query<Order>()
    .Where(o => o.CreatedUtc < DateTime.UtcNow.AddYears(-3))
    .DeleteAsync();
```

> The query builder's bulk `UpdateAsync` / `DeleteAsync` stay raw — they do **not** apply soft-delete auto-filtering, so you can target or restore deleted rows. `QueryBuilder.DeleteAsync` is always a hard delete. Soft-delete behaviour applies only to single-table reads and `Repository.DeleteAsync`; see [Schema & Migrations](schema-migrations.md).

## Raw SQL escape hatches

When the builder can't express something, drop to parameterized raw SQL on the library. All three use named parameters, flow through observability, and inherit the transient-retry policy.

```csharp
// Materialize rows into T with the same compiled mapper as the builder
Result<List<UserRecord>> rows = await mysql.SqlQueryAsync<UserRecord>(
    "SELECT * FROM users WHERE country = @c AND created_utc >= @since",
    new Dictionary<string, object?> { ["@c"] = "DK", ["@since"] = since });

// Non-query — returns affected rows
Result<int> n = await mysql.ExecuteSqlAsync(
    "UPDATE users SET active = 0 WHERE last_seen < @cutoff",
    new Dictionary<string, object?> { ["@cutoff"] = cutoff });

// Single scalar value
Result<long?> max = await mysql.SqlScalarAsync<long>(
    "SELECT MAX(id) FROM users");
```

## Transactions

`BeginTransactionAsync` returns a `TransactionScope` (an `IAsyncDisposable`). Commit explicitly; if the scope is disposed without a commit it rolls back automatically.

```csharp
await using TransactionScope tx = await mysql.BeginTransactionAsync();
try
{
    await mysql.ExecuteSqlAsync("UPDATE accounts SET balance = balance - @amt WHERE id = @from",
        new Dictionary<string, object?> { ["@amt"] = 100m, ["@from"] = 1L });
    await mysql.ExecuteSqlAsync("UPDATE accounts SET balance = balance + @amt WHERE id = @to",
        new Dictionary<string, object?> { ["@amt"] = 100m, ["@to"] = 2L });

    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();   // or just let the scope dispose
    throw;
}
```

> Statements inside an explicit transaction scope are **never** transient-retried — the whole transaction is the caller's to retry. The result cache and smart-cache pools are also disabled inside a transaction. See [Performance & Caching](performance.md).

## Choosing a connection

Every entry point accepts a `connectionId` selecting a named database from `config.mysql.json`; it defaults to `"Default"`. On the builder, `.WithConnection("Reporting")` does the same fluently.

```csharp
var reports = mysql.Query<Sale>().WithConnection("Reporting");
var repo    = mysql.GetRepository<Sale>("Reporting");
```

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
