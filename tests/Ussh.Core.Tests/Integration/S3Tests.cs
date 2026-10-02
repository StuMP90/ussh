using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Ussh.Core.Files;
using Ussh.Core.Ssh;
using Xunit.Abstractions;

namespace Ussh.Core.Tests.Integration;

/// <summary>Amazon S3 browsing and transfers, against a local S3 emulator (moto).</summary>
[Trait("Category", "Integration")]
public sealed class S3Tests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _dispose = new();
    private readonly List<IAsyncDisposable> _asyncDispose = new();
    private readonly string _local = Path.Combine(Path.GetTempPath(), "zssh-s3-local-" + Guid.NewGuid().ToString("N"));

    public S3Tests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        foreach (var item in _asyncDispose)
            item.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        foreach (var item in _dispose)
            item.Dispose();
        try { Directory.Delete(_local, true); } catch { }
    }

    [Fact]
    public async Task BrowseFoldersFromKeysCreateRenameAndDelete()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("browse-bucket", prefix: "team/");
        var raw = Raw(server);
        foreach (var key in new[] { "team/a.txt", "team/reports/2026/q1.csv", "team/reports/2026/q2.csv", "other/x.bin" })
            await raw.PutObjectAsync(new PutObjectRequest { BucketName = "browse-bucket", Key = key, ContentBody = "data:" + key });
        var fs = Open(profile);

        Assert.Equal("/team", await fs.GetStartPathAsync(default)); // "Start in folder"
        var team = await fs.ListAsync("/team", default);
        Assert.Equal(new[] { ("a.txt", false), ("reports", true) }, team.Select(e => (e.Name, e.IsDirectory)).OrderBy(e => e.Name));
        Assert.Equal(new[] { "other", "team" }, (await fs.ListAsync("/", default)).Select(e => e.Name).Order());
        Assert.True((await fs.GetEntryAsync("/team/reports", default))!.IsDirectory);
        Assert.Equal(10 + "team/a.txt".Length - 5, (await fs.GetEntryAsync("/team/a.txt", default))!.Size);
        Assert.Null(await fs.GetEntryAsync("/team/missing", default));

        // New (empty) folder shows up via its marker object.
        await fs.CreateDirectoryAsync("/team/empty", default);
        Assert.Contains(await fs.ListAsync("/team", default), e => e.Name == "empty" && e.IsDirectory);
        Assert.Empty(await fs.ListAsync("/team/empty", default));

        // Rename a file, then a whole folder (copy + delete of every key under it).
        await fs.RenameAsync("/team/a.txt", "/team/b.txt", default);
        await fs.RenameAsync("/team/reports", "/team/archive", default);
        Assert.Equal(new[] { "archive", "b.txt", "empty" }, (await fs.ListAsync("/team", default)).Select(e => e.Name).Order());
        Assert.Equal(new[] { "q1.csv", "q2.csv" }, (await fs.ListAsync("/team/archive/2026", default)).Select(e => e.Name).Order());

        var archive = (await fs.GetEntryAsync("/team/archive", default))!;
        var size = await FileOperations.MeasureAsync(fs, new[] { archive }, default);
        Assert.Equal(2, size.Files);
        await FileOperations.DeleteTreeAsync(fs, archive, default);
        Assert.Equal(new[] { "b.txt", "empty" }, (await fs.ListAsync("/team", default)).Select(e => e.Name).Order());
    }

    [Fact]
    public async Task UploadsAndDownloadsFolderTreesIncludingMultipart()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("tree-bucket");
        var source = Path.Combine(_local, "site");
        Directory.CreateDirectory(Path.Combine(source, "img"));
        WriteRandom(Path.Combine(source, "index.html"), 2_000);
        WriteRandom(Path.Combine(source, "img", "big.bin"), 13 * 1024 * 1024); // multipart with 5 MB parts
        var transfers = Open(profile, smallParts: true);
        var queue = Queue(transfers);

        var local = new LocalFileSystem();
        await queue.UploadAsync(new[] { (await local.GetEntryAsync(source, default))! }, "/");
        await WaitForQueue(queue);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Completed, i.State));

        var back = Path.Combine(_local, "back");
        Directory.CreateDirectory(back);
        var browse = Open(profile);
        await queue.DownloadAsync(new[] { (await browse.GetEntryAsync("/site", default))! }, back);
        await WaitForQueue(queue);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Completed, i.State));
        AssertSameTree(source, Path.Combine(back, "site"));
    }

    [Fact]
    public async Task InterruptedMultipartUploadResumesWithExistingParts()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("resume-bucket");
        var big = Path.Combine(_local, "big.bin");
        WriteRandom(big, 26 * 1024 * 1024); // 6 parts of 5 MB
        var transfers = Open(profile, smallParts: true);
        var queue = Queue(transfers);

        await queue.UploadAsync(new[] { (await new LocalFileSystem().GetEntryAsync(big, default))! }, "/");
        var item = Assert.Single(queue.Items);
        await WaitUntil(() => item.TransferredBytes > 12 * 1024 * 1024 || item.IsFinished, "some parts uploaded");
        Assert.False(item.IsFinished, "upload finished too quickly to interrupt");
        queue.Cancel(item);
        await WaitForQueue(queue);

        // The unfinished upload and its parts are still on the server…
        var uploads = await Raw(server).ListMultipartUploadsAsync("resume-bucket");
        var upload = Assert.Single(uploads.MultipartUploads);
        var partsBefore = (await Raw(server).ListPartsAsync("resume-bucket", "big.bin", upload.UploadId)).Parts.Count;
        _output.WriteLine($"parts before retry: {partsBefore}");
        Assert.True(partsBefore >= 2);

        // …and retrying continues that upload rather than starting again.
        queue.Retry(item);
        await WaitForQueue(queue, TimeSpan.FromSeconds(120));
        Assert.Equal(TransferState.Completed, item.State);
        Assert.Empty((await Raw(server).ListMultipartUploadsAsync("resume-bucket")).MultipartUploads ?? new List<MultipartUpload>());

        var downloaded = Path.Combine(_local, "check.bin");
        await using (var stream = await Open(profile).OpenReadAsync("/big.bin", 0, default))
        await using (var file = File.Create(downloaded))
            await stream.CopyToAsync(file);
        Assert.Equal(Hash(big), Hash(downloaded));
    }

    [Fact]
    public async Task InterruptedDownloadResumesWithRangeRequest()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("download-bucket");
        var source = Path.Combine(_local, "source.bin");
        WriteRandom(source, 40 * 1024 * 1024);
        await Raw(server).PutObjectAsync(new PutObjectRequest { BucketName = "download-bucket", Key = "data/source.bin", FilePath = source });
        var queue = Queue(Open(profile));
        var target = Path.Combine(_local, "target");
        Directory.CreateDirectory(target);

        await queue.DownloadAsync(new[] { (await Open(profile).GetEntryAsync("/data/source.bin", default))! }, target);
        var item = Assert.Single(queue.Items);
        await WaitUntil(() => item.TransferredBytes > 5 * 1024 * 1024 || item.IsFinished, "download under way");
        if (!item.IsFinished)
        {
            queue.Cancel(item);
            await WaitForQueue(queue);
            var partial = new FileInfo(Path.Combine(target, "source.bin")).Length;
            _output.WriteLine($"partial: {partial:N0} bytes");
            queue.Retry(item);
        }
        await WaitForQueue(queue, TimeSpan.FromSeconds(120));

        Assert.Equal(TransferState.Completed, item.State);
        Assert.Equal(Hash(source), Hash(Path.Combine(target, "source.bin")));
    }

    [Fact]
    public async Task MissingBucketIsReportedClearly()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("exists");
        profile.S3Bucket = "does-not-exist";

        var ex = await Assert.ThrowsAsync<FileConnectionException>(() => Open(profile).ListAsync("/", default));
        Assert.Contains("doesn't exist", ex.Message);
    }

    [Fact]
    public async Task AllBucketsModeListsBucketsAndWorksInsideThem()
    {
        if (Start() is not { } server) return;
        var alpha = server.CreateBucket("alpha");
        server.CreateBucket("beta");
        var raw = Raw(server);
        await raw.PutObjectAsync(new PutObjectRequest { BucketName = "alpha", Key = "docs/readme.txt", ContentBody = "alpha readme" });
        await raw.PutObjectAsync(new PutObjectRequest { BucketName = "beta", Key = "logs/app.log", ContentBody = "beta log" });
        var profile = alpha.Clone();
        profile.S3Bucket = "";            // all buckets
        profile.S3Prefix = "beta/logs";   // start folder includes the bucket
        var fs = Open(profile);

        Assert.True(fs.IsAccountWide);
        Assert.Equal("/beta/logs", await fs.GetStartPathAsync(default));
        var buckets = await fs.ListAsync("/", default);
        Assert.Equal(new[] { "alpha", "beta" }, buckets.Select(b => b.Name).Order());
        Assert.All(buckets, b => Assert.True(b.IsDirectory));
        Assert.Equal("app.log", Assert.Single(await fs.ListAsync("/beta/logs", default)).Name);
        var docs = Assert.Single(await fs.ListAsync("/alpha", default));
        Assert.Equal(("docs", "/alpha/docs", true), (docs.Name, docs.Path, docs.IsDirectory));
        Assert.True((await fs.GetEntryAsync("/alpha", default))!.IsDirectory);
        Assert.Equal(12, (await fs.GetEntryAsync("/alpha/docs/readme.txt", default))!.Size);

        // Normal work inside a bucket.
        await fs.CreateDirectoryAsync("/alpha/new", default);
        await fs.RenameAsync("/alpha/docs/readme.txt", "/alpha/docs/README.txt", default);
        var queue = Queue(Open(profile));
        var file = Path.Combine(_local, "up.txt");
        File.WriteAllText(file, "uploaded");
        await queue.UploadAsync(new[] { (await new LocalFileSystem().GetEntryAsync(file, default))! }, "/alpha/new");
        await queue.DownloadAsync(new[] { (await fs.GetEntryAsync("/beta/logs/app.log", default))! }, _local);
        await WaitForQueue(queue);
        Assert.All(queue.Items, i => Assert.Equal(TransferState.Completed, i.State));
        Assert.Equal("beta log", File.ReadAllText(Path.Combine(_local, "app.log")));
        Assert.Equal(new[] { "docs/README.txt", "new/", "new/up.txt" },
            (await raw.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "alpha" })).S3Objects.Select(o => o.Key).Order());
    }

    [Fact]
    public async Task BucketsThemselvesCanNeverBeChanged()
    {
        if (Start() is not { } server) return;
        var profile = server.CreateBucket("keep-me");
        var raw = Raw(server);
        for (var i = 0; i < 5; i++)
            await raw.PutObjectAsync(new PutObjectRequest { BucketName = "keep-me", Key = $"data/{i}.txt", ContentBody = "x" });
        profile.S3Bucket = "";
        var fs = Open(profile);
        var bucket = Assert.Single(await fs.ListAsync("/", default), b => b.Name == "keep-me");

        Assert.NotNull(fs.ReadOnlyReason("/keep-me"));
        Assert.NotNull(fs.ReadOnlyReason("/a-new-bucket"));
        Assert.Null(fs.ReadOnlyReason("/keep-me/data"));

        // Deleting the bucket entry is refused before a single object is touched.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => FileOperations.DeleteTreeAsync(fs, bucket, default));
        Assert.Equal(5, (await raw.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "keep-me" })).S3Objects.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fs.DeleteDirectoryAsync("/keep-me", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fs.CreateDirectoryAsync("/a-new-bucket", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fs.RenameAsync("/keep-me", "/renamed", default));

        // Uploading into the bucket list is refused before anything is queued.
        var queue = Queue(Open(profile));
        var file = Path.Combine(_local, "stray.txt");
        File.WriteAllText(file, "x");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            queue.UploadAsync(new[] { new LocalFileSystem().GetEntryAsync(file, default).Result! }, "/"));
        Assert.Empty(queue.Items);
        Assert.Equal(new[] { "keep-me" }, (await raw.ListBucketsAsync()).Buckets.Select(b => b.BucketName));

        // A whole folder inside the bucket can still be deleted.
        await FileOperations.DeleteTreeAsync(fs, (await fs.GetEntryAsync("/keep-me/data", default))!, default);
        Assert.Empty((await raw.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "keep-me" })).S3Objects ?? new List<S3Object>());
    }

    [Fact]
    public async Task BucketsInDifferentRegionsAreReachedInTheirOwnRegion()
    {
        if (Start() is not { } server) return;
        using (var london = Raw(server, "eu-west-2"))
            await london.PutBucketAsync(new PutBucketRequest { BucketName = "london-bucket", BucketRegion = S3Region.FindValue("eu-west-2") });
        await Raw(server).PutBucketAsync("virginia-bucket");
        await Raw(server, "eu-west-2").PutObjectAsync(new PutObjectRequest { BucketName = "london-bucket", Key = "uk.txt", ContentBody = "uk" });
        await Raw(server).PutObjectAsync(new PutObjectRequest { BucketName = "virginia-bucket", Key = "us.txt", ContentBody = "us" });

        // As on AWS (no custom endpoint): the emulator stands in for every regional endpoint.
        var requested = new List<string>();
        var profile = new Ussh.Core.Models.ServerProfile
        {
            Kind = Ussh.Core.Models.ServerKind.S3,
            S3Region = "us-east-1",
            S3AccessKeyId = "test",
            S3SecretAccessKey = "test",
        };
        var fs = new S3FileSystem(profile)
        {
            ClientFactory = region =>
            {
                lock (requested) requested.Add(region);
                return Raw(server, region);
            },
        };
        _asyncDispose.Add(fs);

        Assert.Equal("uk.txt", Assert.Single(await fs.ListAsync("/london-bucket", default)).Name);
        Assert.Equal("us.txt", Assert.Single(await fs.ListAsync("/virginia-bucket", default)).Name);
        Assert.Contains("eu-west-2", requested); // the London bucket was talked to in its region
        Assert.Contains("us-east-1", requested);
    }

    // ------------------------------------------------------------------ helpers

    private S3TestServer? Start()
    {
        var server = S3TestServer.TryStart();
        if (server == null)
        {
            _output.WriteLine("SKIPPED: moto_server not found (pip install 'moto[server]', or set ZSSH_MOTO_SERVER).");
            return null;
        }
        _dispose.Add(server);
        return server;
    }

    private S3FileSystem Open(Ussh.Core.Models.ServerProfile profile, bool smallParts = false)
    {
        var fs = new S3FileSystem(profile);
        if (smallParts)
        {
            fs.PartSize = 5 * 1024 * 1024;
            fs.MultipartThreshold = 6 * 1024 * 1024;
        }
        _asyncDispose.Add(fs);
        return fs;
    }

    private TransferQueue Queue(IFileSystem remote)
    {
        var queue = new TransferQueue(new LocalFileSystem(), remote, _ => Task.FromResult(new ConflictDecision(ConflictChoice.Overwrite, true)));
        _asyncDispose.Insert(0, queue);
        return queue;
    }

    private AmazonS3Client Raw(S3TestServer server, string region = "us-east-1")
    {
        var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
            new AmazonS3Config { ServiceURL = server.Url, ForcePathStyle = true, AuthenticationRegion = region });
        _dispose.Add(client);
        return client;
    }

    private static async Task WaitForQueue(TransferQueue queue, TimeSpan? timeout = null)
    {
        await Task.Delay(100);
        await WaitUntil(() => queue.ActiveCount == 0, "transfers to finish", timeout);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    private static void WriteRandom(string path, int bytes)
    {
        var data = new byte[bytes];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var expectedFiles = Directory.GetFiles(expected, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(expected, f)).Order().ToList();
        var actualFiles = Directory.GetFiles(actual, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(actual, f)).Order().ToList();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var file in expectedFiles)
            Assert.Equal(Hash(Path.Combine(expected, file)), Hash(Path.Combine(actual, file)));
    }
}
