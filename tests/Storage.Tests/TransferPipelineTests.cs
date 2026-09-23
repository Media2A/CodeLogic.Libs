using System.Diagnostics;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers built-in progress reporting and speed limits for uploads, downloads, and relayed transfers.</summary>
public sealed class TransferPipelineTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

    [Fact]
    public async Task Upload_progress_reaches_completion_with_the_full_size()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        var reports = new Recorder();

        var result = await storage.UploadAsync("p.bin", new MemoryStream(Payload), new StorageUploadOptions { Progress = reports });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        var last = reports.Last;
        Assert.True(last.IsCompleted);
        Assert.Equal(Payload.Length, last.BytesTransferred);
        Assert.Equal(Payload.Length, last.TotalBytes);
        Assert.Equal(TimeSpan.Zero, last.EstimatedRemaining);
    }

    [Fact]
    public async Task Download_progress_reports_as_the_stream_is_read()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("d.bin", Payload);
        var reports = new Recorder();

        var bytes = await storage.DownloadBytesAsync("d.bin", new StorageDownloadOptions { Progress = reports });

        Assert.Equal(Payload, bytes.Value);
        Assert.True(reports.Last.IsCompleted);
        Assert.Equal(Payload.Length, reports.Last.BytesTransferred);
    }

    [Fact]
    public async Task Upload_speed_limit_slows_the_transfer_to_the_configured_rate()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        StorageTransferPipeline.SetLimits(storage, uploadBytesPerSecond: 200_000, downloadBytesPerSecond: null);

        var clock = Stopwatch.StartNew();
        var result = await storage.UploadBytesAsync("slow.bin", Payload);
        clock.Stop();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        // 300 kB at 200 kB/s with a 50 kB burst allowance takes about 1.25 s.
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.0, 10);
        Assert.Equal(Payload, (await storage.DownloadBytesAsync("slow.bin")).Value);
    }

    [Fact]
    public async Task Download_speed_limit_applies_and_uploads_stay_unlimited()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        StorageTransferPipeline.SetLimits(storage, uploadBytesPerSecond: null, downloadBytesPerSecond: 200_000);

        var upload = Stopwatch.StartNew();
        await storage.UploadBytesAsync("f.bin", Payload);
        upload.Stop();
        var download = Stopwatch.StartNew();
        await storage.DownloadBytesAsync("f.bin");
        download.Stop();

        Assert.True(upload.Elapsed.TotalSeconds < 1.0);
        Assert.InRange(download.Elapsed.TotalSeconds, 1.0, 10);
    }

    [Fact]
    public async Task Token_bucket_is_shared_by_concurrent_transfers()
    {
        var bucket = new TokenBucket(100_000);
        var clock = Stopwatch.StartNew();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            for (var i = 0; i < 5; i++) await bucket.TakeAsync(10_000, CancellationToken.None);
        }));

        // 200 kB through one 100 kB/s budget: about 1.75 s after the initial 25 kB burst.
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.4, 10);
    }

    [Fact]
    public async Task Metered_streams_stay_seekable_so_upload_retries_can_rewind()
    {
        var reports = new Recorder();
        await using var metered = new MeteredStream(new MemoryStream(Payload), reports, Payload.Length, "x", leaveOpen: false);
        var buffer = new byte[100_000];

        await metered.ReadExactlyAsync(buffer);
        metered.Position = 0;
        await metered.ReadExactlyAsync(buffer);

        Assert.True(metered.CanSeek);
        Assert.Equal(100_000, metered.Position);
        Assert.All(reports.All, report => Assert.True(report.BytesTransferred <= 100_000));
    }

    [Fact]
    public async Task Relayed_directory_progress_accumulates_across_files()
    {
        using var directory = new TestDirectory();
        var source = Local(directory.CreateDirectory("src"));
        var destination = Local(directory.CreateDirectory("dst"));
        await source.UploadBytesAsync("tree/a.bin", Payload[..100_000]);
        await source.UploadBytesAsync("tree/b.bin", Payload[..50_000]);
        var reports = new Recorder();

        var copied = await StorageTransferCoordinator.CopyAsync(source, "tree", destination, "copy",
            new StorageTransferOptions { Progress = reports }, CancellationToken.None);

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.True(reports.Last.IsCompleted);
        Assert.Equal(150_000, reports.Last.BytesTransferred);
        Assert.Contains(reports.All, report => report.ItemPath == "tree/a.bin");
        Assert.DoesNotContain(reports.All, report => report.ItemPath?.Contains(".cl-storage", StringComparison.Ordinal) == true);
    }

    private static LocalStorageBackend Local(string root) => new("local", new LocalConnectionConfig { RootPath = root });

    private sealed class Recorder : IProgress<StorageTransferProgress>
    {
        private readonly List<StorageTransferProgress> _reports = [];
        public IReadOnlyList<StorageTransferProgress> All { get { lock (_reports) return [.. _reports]; } }
        public StorageTransferProgress Last { get { lock (_reports) return _reports[^1]; } }
        public void Report(StorageTransferProgress value) { lock (_reports) _reports.Add(value); }
    }
}
