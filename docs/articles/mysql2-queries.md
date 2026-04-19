# CL.MySQL2 — Query Builder

`mysql.Query<T>()` returns a fluent chain you build up and then execute with a
terminal method. The whole chain compiles to a **single** SQL statement. No
rows materialize server-side before you see them; no columns transfer that you
don't ask for.

This page walks every shape you'll realistically need — with the SQL it emits
so there are no surprises.

---

## Anatomy of a query

```csharp
var results = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")          // predicate
    .OrderByDescending(o => o.PlacedUtc)      // sort
    .Take(50)                                 // LIMIT
    .WithCache(TimeSpan.FromMinutes(1))       // optional cache
    .ToListAsync();                           // terminal
```

**Rules to keep in mind:**

1. **`WithCache` must come before `GroupBy`** — the cache TTL is attached to the
   current builder and carried through to aggregation. Call it late and it's a
   no-op.
2. **Anything that changes the output shape returns a different type.**
   `Select<TResult>(...)` returns a `ProjectedQuery<T, TResult>`; `GroupBy`
   returns a `GroupedQuery<TKey, T>`. You can't go back to `QueryBuilder<T>`
   after either — keep chain order in mind.
3. **Terminals return `Result<T>`.** Check `IsSuccess` / `Error`.

---

## Filtering — `Where`

```csharp
.Where(o => o.Status == "paid")
// WHERE status = @p0

.Where(o => o.TotalCents >= 1000 && o.Status != "refunded")
// WHERE (total_cents >= @p0 AND status != @p1)

.Where(o => o.ShippedUtc == null)
// WHERE shipped_utc IS NULL

.Where(o => o.ShippedUtc != null)
// WHERE shipped_utc IS NOT NULL
```

`Where` composes — each call adds an `AND`-joined clause:

```csharp
var q = mysql.Query<OrderRecord>().Where(o => o.Status == "paid");
if (userId.HasValue)       q = q.Where(o => o.UserId == userId.Value);
if (since.HasValue)        q = q.Where(o => o.PlacedUtc >= since.Value);
var rows = await q.ToListAsync();
// WHERE (status = @p0) AND (user_id = @p1) AND (placed_utc >= @p2)
```

### String operations

All three use MySQL `LIKE` with wildcards escaped so user input is safe:

```csharp
.Where(u => u.Email.Contains("@example.com"))   // email LIKE '%@example.com%' (escaped)
.Where(u => u.Email.StartsWith("admin+"))        // email LIKE 'admin+%'
.Where(u => u.Email.EndsWith(".ru"))             // email LIKE '%.ru'

.Where(u => string.IsNullOrEmpty(u.MiddleName))
// (middle_name IS NULL OR middle_name = '')
```

### `IN (…)` clauses

```csharp
var userIds = new[] { 1L, 7L, 42L };
.Where(o => userIds.Contains(o.UserId))
// WHERE user_id IN (@p0, @p1, @p2)

// Or the static form:
.Where(o => Enumerable.Contains(userIds, o.UserId))
```

Beyond `MaxInClauseValues` (1000 by default) you should batch the list yourself
— that config knob exists to protect the driver.

### Nullable unwraps

```csharp
.Where(o => o.ShippedUtc.Value < o.DeliveredUtc.Value)
// WHERE shipped_utc < delivered_utc

// …same thing, shorter:
.Where(o => o.ShippedUtc < o.DeliveredUtc)
```

The visitor understands `Nullable<T>.Value` and simply emits the underlying
column reference.

### Captured variables

Closure reads are evaluated fast (direct `FieldInfo.GetValue`, no
`Expression.Compile`) and bound as parameters:

```csharp
var since = DateTime.UtcNow.AddDays(-30);
var minTotal = 5000;

.Where(o => o.PlacedUtc >= since && o.TotalCents >= minTotal)
// WHERE (placed_utc >= @p0 AND total_cents >= @p1)   with values bound
```

---

## Sorting — `OrderBy` / `ThenBy`

```csharp
.OrderBy(o => o.PlacedUtc)
.OrderByDescending(o => o.TotalCents)
```

Chain multiple for tie-breakers. Composable with `Take`/`Skip` for paging.

---

## Paging — `Take` / `Skip` / `ToPagedListAsync`

```csharp
var page2 = await mysql.Query<OrderRecord>()
    .OrderByDescending(o => o.PlacedUtc)
    .Skip(50).Take(50)
    .ToListAsync();

// Or for UI paging with counts:
var result = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")
    .OrderByDescending(o => o.PlacedUtc)
    .ToPagedListAsync(page: 2, pageSize: 25);

result.Value!.Items;          // List<OrderRecord>
result.Value!.TotalItems;     // long
result.Value!.TotalPages;     // int
result.Value!.HasNextPage;    // bool
```

`ToPagedListAsync` runs a `COUNT(*)` + `SELECT` in one round trip.

> ⚠️ **Deep `OFFSET` is a trap.** `OFFSET 100000` reads and discards 100k rows.
> If you're paging through a big dataset, use keyset paging — `Where(x => x.Id < lastSeen).Take(50)`.

---

## Projection — `Select<TResult>`

Stop shipping wide rows across the wire. `.Select` takes a typed expression
and the resulting `ProjectedQuery<TSource, TResult>` only reads the columns
you named.

```csharp
// Anonymous type
var lean = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")
    .Select(o => new { o.Id, o.TotalCents, o.PlacedUtc })
    .ToListAsync();
// SELECT `id` AS `Id`, `total_cents` AS `TotalCents`, `placed_utc` AS `PlacedUtc` ...

// Positional record
public sealed record OrderSummary(long Id, int TotalCents, DateTime When);

var summaries = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")
    .Select(o => new OrderSummary(o.Id, o.TotalCents, o.PlacedUtc))
    .ToListAsync();

// Single column
var ids = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "pending")
    .Select(o => o.Id)
    .ToListAsync();
```

For a 15-column table where you need 3 values, this is often a **10×** win on
its own — especially when `VARCHAR(256)`-shaped columns are in the row.

---

## Aggregation — `GroupBy` → `Select`

`GroupBy` returns a `GroupedQuery<TKey, T>`. The only way out is `Select(g => …)`,
which collapses groups into shaped rows. Everything translates to SQL
`GROUP BY` + aggregates. **No rows materialize client-side.**

### Scalar key

```csharp
// How many orders per status?
var counts = await mysql.Query<OrderRecord>()
    .GroupBy(o => o.Status)
    .Select(g => new { Status = g.Key, Count = g.Count() })
    .ToListAsync();
// SELECT `status` AS `Status`, COUNT(*) AS `Count`
// FROM `orders` GROUP BY `status`

// Average order value per user
var avgs = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")
    .GroupBy(o => o.UserId)
    .Select(g => new { UserId = g.Key, AvgCents = g.Average(o => o.TotalCents) })
    .OrderByDescending(x => x.AvgCents)
    .Take(100)
    .ToListAsync();
```

### Composite key

```csharp
// Orders by (day-of-week, hour) for a heatmap
var cells = await mysql.Query<SnapshotRecord>()
    .Where(s => s.SnapshotUtc >= since)
    .GroupBy(s => new { Dow  = SqlFn.DayOfWeek(s.SnapshotUtc),
                        Hour = SqlFn.Hour(s.SnapshotUtc) })
    .Select(g => new HeatmapCell(
        g.Key.Dow,
        g.Key.Hour,
        g.Average(x => (double)x.PlayerCount)))
    .OrderBy(c => c.Dow).ThenBy(c => c.Hour)
    .ToListAsync();
```

### Aggregate methods recognized inside `Select`

| Expression | SQL |
|---|---|
| `g.Key` | grouping key column(s) |
| `g.Key.Member` (composite) | individual group column |
| `g.Count()` | `COUNT(*)` |
| `g.Count(x => pred)` | `SUM(CASE WHEN pred THEN 1 ELSE 0 END)` |
| `g.Sum(x => x.Col)` | `SUM(col)` |
| `g.Average(x => x.Col)` | `AVG(col)` |
| `g.Min(x => x.Col)` | `MIN(col)` |
| `g.Max(x => x.Col)` | `MAX(col)` |
| `g.Any()` | `(COUNT(*) > 0)` |
| `g.Any(x => pred)` | `(SUM(CASE WHEN pred THEN 1 ELSE 0 END) > 0)` |

### Ternary → `CASE WHEN`

Works inside any aggregate argument:

```csharp
// Uptime %: count online snapshots as 100, offline as 0, average them.
.Select(g => g.Average(x => x.IsOnline ? 100.0 : 0.0))
// SELECT AVG((CASE WHEN is_online = 1 THEN 100 ELSE 0 END)) ...
```

### Two-pass "sum across dimension, then average over bucket"

The common time-series pattern. Do the inner aggregate in SQL; do the outer
bucket math in C# if you prefer — both work:

```csharp
// SQL does per-instant totals, C# rolls up to 15-minute buckets
var perInstant = await mysql.Query<SnapshotRecord>()
    .Where(s => s.SnapshotUtc >= since)
    .GroupBy(s => s.SnapshotUtc)
    .Select(g => new { Instant = g.Key, Total = g.Sum(x => x.PlayerCount) })
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();

var bucketTicks = TimeSpan.FromMinutes(15).Ticks;
var series = perInstant.Value!
    .GroupBy(x => new DateTime(x.Instant.Ticks - x.Instant.Ticks % bucketTicks, DateTimeKind.Utc))
    .OrderBy(g => g.Key)
    .Select(g => new TimeSeriesPoint(g.Key, (int)g.Average(x => x.Total)))
    .ToList();
```

The DB returns ~hours of rows (one per snapshot instant), not the full table.

---

## `SqlFn` reference

`SqlFn` is a set of marker methods the translator rewrites into MySQL function
calls. Use them inside `Where`, `GroupBy`, and aggregate arguments.

| Method | MySQL |
|---|---|
| `SqlFn.Year(d)` | `YEAR(d)` |
| `SqlFn.Month(d)` | `MONTH(d)` |
| `SqlFn.Day(d)` | `DAY(d)` |
| `SqlFn.Hour(d)` | `HOUR(d)` |
| `SqlFn.Minute(d)` | `MINUTE(d)` |
| `SqlFn.DayOfWeek(d)` | `DAYOFWEEK(d) - 1`  *(so 0=Sunday, matches .NET)* |
| `SqlFn.Date(d)` | `DATE(d)` |
| `SqlFn.BucketUtc(d, seconds)` | `FROM_UNIXTIME(FLOOR(UNIX_TIMESTAMP(d)/s)*s)` |
| `SqlFn.Coalesce(a, b, …)` | `COALESCE(...)` |
| `SqlFn.IfNull(v, fallback)` | `IFNULL(v, f)` |
| `SqlFn.Lower(s)` / `SqlFn.Upper(s)` | `LOWER(s)` / `UPPER(s)` |
| `SqlFn.Concat(a, b, …)` | `CONCAT(...)` |
| `SqlFn.Like(s, pattern)` | `s LIKE pattern`  (no wildcard escaping — raw pattern) |
| `SqlFn.Round(v, digits)` | `ROUND(v, d)` |
| `SqlFn.Floor(v)` | `FLOOR(v)` |
| `SqlFn.Ceiling(v)` | `CEILING(v)` |

Calling a `SqlFn.*` method **outside** a query context throws — they're
translation markers, not real implementations. This mirrors EF Core's
`EF.Functions`.

### Time-bucketing example

```csharp
// Active users per 5-minute bucket for the last hour
var activity = await mysql.Query<LoginRecord>()
    .Where(l => l.AtUtc >= DateTime.UtcNow.AddHours(-1))
    .GroupBy(l => SqlFn.BucketUtc(l.AtUtc, 300))   // 300s = 5min
    .Select(g => new { At = g.Key, Active = g.Count() })
    .OrderBy(x => x.At)
    .ToListAsync();
```

---

## Scalar terminals

Skip materialization entirely when you just want a number:

```csharp
long total       = (await mysql.Query<OrderRecord>().CountAsync()).Value;
long paidCount   = (await mysql.Query<OrderRecord>()
                        .Where(o => o.Status == "paid").CountAsync()).Value;

int maxSpend     = (await mysql.Query<OrderRecord>()
                        .Where(o => o.UserId == userId)
                        .MaxAsync(o => o.TotalCents)).Value;

double avgCents  = (await mysql.Query<OrderRecord>()
                        .Where(o => o.Status == "paid")
                        .AverageAsync(o => o.TotalCents)).Value;

int sumToday     = (await mysql.Query<OrderRecord>()
                        .Where(o => o.PlacedUtc >= DateTime.UtcNow.Date)
                        .SumAsync(o => o.TotalCents)).Value;
```

All emit `SELECT AGG(col) FROM ...` — one row back.

---

## `FirstOrDefaultAsync`

```csharp
var mostRecent = await mysql.Query<OrderRecord>()
    .Where(o => o.UserId == userId)
    .OrderByDescending(o => o.PlacedUtc)
    .FirstOrDefaultAsync();
// Appends LIMIT 1 for you.
```

---

## Bulk writes on predicates

### Typed `UpdateAsync(expr)` — one statement, no fetch

```csharp
// Mark every stale open ticket as 'stale' — one UPDATE, no rows transferred.
await mysql.Query<TicketRecord>()
    .Where(t => t.Status == "open" && t.CreatedUtc < cutoff)
    .UpdateAsync(t => new TicketRecord
    {
        Status = "stale",
        ReviewedUtc = DateTime.UtcNow
    });
// UPDATE `tickets` SET `status` = @upd_0, `reviewed_utc` = @upd_1
// WHERE (status = @p0 AND created_utc < @p1)
```

Values can reference the **row itself** — emitted as a column expression, not
a parameter:

```csharp
await mysql.Query<CounterRecord>()
    .Where(c => c.Key == "orders_processed")
    .UpdateAsync(c => new CounterRecord { Value = c.Value + 1 });
// UPDATE `counters` SET `value` = (`value` + 1) WHERE key = @p0
```

### Dictionary `UpdateAsync` (legacy column-keyed)

```csharp
await mysql.Query<OrderRecord>()
    .Where(o => o.Id == id)
    .UpdateAsync(new Dictionary<string, object?>
    {
        ["status"] = "refunded",
        ["refunded_utc"] = DateTime.UtcNow
    });
```

Useful when your setter columns are dynamic (e.g. admin panel forms).

### `DeleteAsync` — one statement

```csharp
var n = await mysql.Query<SnapshotRecord>()
    .Where(s => s.SnapshotUtc < DateTime.UtcNow.AddDays(-90))
    .DeleteAsync();
// DELETE FROM `snapshots` WHERE snapshot_utc < @p0
```

> **Prefer `[RetainDays]` over hand-rolled purges.** If you're deleting rows on
> a timestamp, just annotate the entity with `[RetainDays(90, nameof(SnapshotUtc))]`
> and the library's background worker handles it. See
> [Schema docs](mysql2-schema.md#retention-retaindays).

---

## Joins (current state)

Typed join translation isn't in v4 yet. Two options today:

1. **Run two queries + stitch in C#** (fast when ids fit in memory)

   ```csharp
   var orders = await mysql.Query<OrderRecord>().Where(...).ToListAsync();
   var userIds = orders.Value!.Select(o => o.UserId).Distinct().ToList();
   var users = await mysql.Query<UserRecord>()
       .Where(u => userIds.Contains(u.Id))
       .ToListAsync();
   // …then zip in memory.
   ```

2. **Opt into an untyped string join** — the escape hatch for one-offs:

   ```csharp
   .Join("users", "users.id = orders.user_id", JoinType.Left)
   ```

Typed joins (`.Join(inner, outerKey, innerKey, resultSelector)`) are on the
roadmap.

---

## Transactions inside queries

Pass a transaction scope when you construct the builder:

```csharp
await using var tx = await mysql.BeginTransactionAsync();

var repo = mysql.GetRepository<OrderRecord>(tx);
await repo.InsertAsync(newOrder);

// Queries during a transaction skip the cache (reads see uncommitted writes).
var relatedCount = await mysql.Query<OrderRecord>(tx)
    .Where(o => o.UserId == userId)
    .CountAsync();

await tx.CommitAsync();
```

---

## When things aren't translatable

If you write a `Where` predicate the translator can't reduce to SQL, you'll
get a `NotSupportedException` at first execution — not at compile time. This
is the accepted trade-off of expression-tree ORMs (same as EF Core).

Common gotchas:

```csharp
// ❌ Fails — `.ToLower().Trim().StartsWith(…)` is a chain the visitor doesn't reduce
.Where(u => u.Email.ToLower().Trim().StartsWith("admin"))

// ✅ Use SqlFn
.Where(u => SqlFn.Lower(u.Email).StartsWith("admin"))
```

```csharp
// ❌ Custom method calls aren't evaluated server-side
bool IsStale(DateTime d) => d < DateTime.UtcNow.AddDays(-30);
.Where(o => IsStale(o.PlacedUtc))

// ✅ Inline the predicate
var cutoff = DateTime.UtcNow.AddDays(-30);
.Where(o => o.PlacedUtc < cutoff)
```

If you hit something that feels like it *should* work — open an issue. The
visitor is a living document.

---

## Raw escape hatch

For anything the builder can't express yet, drop to the connection manager:

```csharp
var summary = await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "CALL reporting_monthly_summary(@month)";
    cmd.Parameters.AddWithValue("@month", 2026_04);
    return await cmd.ExecuteScalarAsync();
});
```

Raw SQL bypasses everything — the cache, the materializer, the visitor. Use it
sparingly; the reason this library exists is so you don't have to.

---

## What's next

- **Speed** → [Performance & Caching](mysql2-performance.md)
- **Table design** → [Schema & Migrations](mysql2-schema.md)
- **Back to basics** → [Overview](mysql2.md)
