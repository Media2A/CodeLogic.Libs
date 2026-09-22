using Xunit;

// Live servers are shared, and several tests disable retries on purpose to observe a single failure.
// Running them in parallel trips server-side limits (OpenSSH MaxStartups) and emulator races that the
// tests would then misreport, so the suite runs sequentially; it takes seconds either way.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
