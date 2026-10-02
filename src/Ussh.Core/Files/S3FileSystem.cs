using System.Collections.Concurrent;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Ussh.Core.Models;
using Ussh.Core.Ssh;

namespace Ussh.Core.Files;

/// <summary>
/// Amazon S3 (or S3-compatible storage) as a file system, using access keys.
///
/// Two modes:
/// <list type="bullet">
/// <item>Bucket set: paths are "/" + key ("/a/b/c.txt") inside that bucket.</item>
/// <item>Bucket blank ("all buckets"): the top level lists the account's buckets and paths are
/// "/bucket/key". The bucket level is read-only: uSSH never creates, renames or deletes buckets.</item>
/// </list>
///
/// S3 has no real folders: keys are flat, and "a/b/c.txt" shows as folder a, folder b, file
/// c.txt. New folders are the usual zero-byte "a/b/" marker objects; renames are copy-then-delete.
/// On AWS each bucket's region is looked up on first use, so buckets in different regions work.
/// Large uploads use multipart upload and resume after an interruption; downloads resume with
/// ranged GETs. Errors are mapped so credential/permission problems are reported clearly and
/// only transient ones (network, 5xx, throttling) are retried.
/// </summary>
public sealed class S3FileSystem : IFileSystem
{
    private const string BucketLevelReason =
        "Buckets can't be created, renamed, deleted or uploaded into from uSSH. Open a bucket and work inside it.";

    private readonly ServerProfile _profile;
    private readonly bool _customEndpoint;
    private readonly string _defaultRegion;
    private readonly ConcurrentDictionary<string, AmazonS3Client> _clients = new();
    private readonly ConcurrentDictionary<string, string> _bucketRegions = new();

    public S3FileSystem(ServerProfile profile)
    {
        _profile = profile;
        Name = profile.DisplayName;
        _customEndpoint = !string.IsNullOrWhiteSpace(profile.S3ServiceUrl);
        _defaultRegion = string.IsNullOrWhiteSpace(profile.S3Region) ? "us-east-1" : profile.S3Region.Trim();
    }

    /// <summary>Multipart part size (S3's minimum is 5 MB except for the last part).</summary>
    internal long PartSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>Files at least this big upload in parts (and can resume).</summary>
    internal long MultipartThreshold { get; set; } = 32 * 1024 * 1024;

    /// <summary>For tests: creates the client for a region (lets an emulator stand in for AWS regions).</summary>
    internal Func<string, AmazonS3Client>? ClientFactory { get; set; }

    public string Name { get; }
    public bool IsLocal => false;
    public bool SupportsPermissions => false;

    /// <summary>True when no bucket is configured: the top level lists every bucket in the account.</summary>
    public bool IsAccountWide => string.IsNullOrWhiteSpace(_profile.S3Bucket);

    public Task<string> GetStartPathAsync(CancellationToken token)
    {
        var prefix = _profile.S3Prefix.Trim('/');
        return Task.FromResult(prefix.Length == 0 ? "/" : "/" + prefix);
    }

    public string? ReadOnlyReason(string path) =>
        IsAccountWide && Split(path).Key.Length == 0 ? BucketLevelReason : null;

    public Task<IReadOnlyList<FileEntry>> ListAsync(string directory, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Split(directory);
        if (bucket.Length == 0)
            return await ListBucketsAsync(token).ConfigureAwait(false);

        var prefix = key.Length == 0 ? "" : key + "/";
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        var basePath = BasePath(bucket);
        var entries = new List<FileEntry>();
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuation,
            }, token).ConfigureAwait(false);
            foreach (var folder in response.CommonPrefixes ?? new List<string>())
            {
                var name = folder[prefix.Length..].TrimEnd('/');
                if (name.Length > 0)
                    entries.Add(new FileEntry(name, basePath + folder.TrimEnd('/'), true, 0, null, IsHidden: name.StartsWith('.')));
            }
            foreach (var obj in response.S3Objects ?? new List<S3Object>())
            {
                if (obj.Key == prefix)
                    continue; // the folder's own marker object
                entries.Add(ToEntry(basePath + obj.Key, obj.Key[prefix.Length..], obj.Size ?? 0, obj.LastModified, obj.StorageClass?.Value));
            }
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuation != null);
        return (IReadOnlyList<FileEntry>)entries;
    }, token);

    public Task<FileEntry?> GetEntryAsync(string path, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Split(path);
        if (bucket.Length == 0)
            return new FileEntry("/", "/", true, 0, null);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        if (key.Length == 0)
        {
            // A bucket (all-buckets mode). Listing it proves it exists and we can read it.
            await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 }, token).ConfigureAwait(false);
            return new FileEntry(bucket, "/" + bucket, true, 0, null);
        }
        try
        {
            var head = await client.GetObjectMetadataAsync(bucket, key, token).ConfigureAwait(false);
            return ToEntry(BasePath(bucket) + key, LastSegment(key), head.ContentLength, head.LastModified, head.StorageClass?.Value);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode != "NoSuchBucket")
        {
            // Not an object: is it a folder (anything under "key/")?
            var listing = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = key + "/",
                MaxKeys = 1,
            }, token).ConfigureAwait(false);
            return (listing.S3Objects?.Count ?? 0) > 0
                ? new FileEntry(LastSegment(key), BasePath(bucket) + key, true, 0, null)
                : null;
        }
    }, token);

    public Task CreateDirectoryAsync(string path, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Writable(path);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key + "/", ContentBody = "" }, token).ConfigureAwait(false);
    }, token);

    public Task DeleteFileAsync(string path, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Writable(path);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        await client.DeleteObjectAsync(bucket, key, token).ConfigureAwait(false);
    }, token);

    /// <summary>Removes the folder marker; its contents are deleted first by the caller.</summary>
    public Task DeleteDirectoryAsync(string path, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Writable(path);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        await client.DeleteObjectAsync(bucket, key + "/", token).ConfigureAwait(false);
    }, token);

    public Task RenameAsync(string from, string to, CancellationToken token) => Guard(async () =>
    {
        var (bucket, fromKey) = Writable(from);
        var (toBucket, toKey) = Writable(to);
        if (toBucket != bucket)
            throw new NotSupportedException("Renaming can't move items between buckets.");
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        var source = await GetEntryAsync(from, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"{from} doesn't exist.");
        if (!source.IsDirectory)
        {
            await CopyThenDeleteAsync(client, bucket, fromKey, toKey, token).ConfigureAwait(false);
            return;
        }
        // A folder is every key under its prefix (including its marker, if any).
        var fromPrefix = fromKey + "/";
        var toPrefix = toKey + "/";
        foreach (var key in await AllKeysAsync(client, bucket, fromPrefix, token).ConfigureAwait(false))
            await CopyThenDeleteAsync(client, bucket, key, toPrefix + key[fromPrefix.Length..], token).ConfigureAwait(false);
    }, token);

    public Task SetPermissionsAsync(string path, int mode, CancellationToken token) =>
        throw new NotSupportedException("S3 objects don't have Unix permissions.");

    public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Split(path);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        var request = new GetObjectRequest { BucketName = bucket, Key = key };
        if (offset > 0)
            request.ByteRange = new ByteRange(offset, long.MaxValue);
        var response = await client.GetObjectAsync(request, token).ConfigureAwait(false);
        return (Stream)new ResponseStream(response);
    }, token);

    public Task UploadAsync(string localFile, string path, bool resume, IProgress<long> progress, CancellationToken token) => Guard(async () =>
    {
        var (bucket, key) = Writable(path);
        var client = await ClientForAsync(bucket, token).ConfigureAwait(false);
        var size = new FileInfo(localFile).Length;
        if (size < MultipartThreshold)
        {
            progress.Report(0);
            await using var stream = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, AutoCloseStream = false };
            request.StreamTransferProgress += (_, e) => progress.Report(e.TransferredBytes);
            await client.PutObjectAsync(request, token).ConfigureAwait(false);
            progress.Report(size);
            return;
        }
        await MultipartUploadAsync(client, bucket, localFile, key, size, resume, progress, token).ConfigureAwait(false);
    }, token);

    public string Combine(string directory, string name) => FileOperations.CombineRemote(directory, name);

    public string? Parent(string path) => FileOperations.ParentRemote(path);

    public ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ paths, buckets and regions

    /// <summary>Path → (bucket, key). In all-buckets mode the first segment is the bucket.</summary>
    private (string Bucket, string Key) Split(string path)
    {
        var trimmed = path.Trim('/');
        if (!IsAccountWide)
            return (_profile.S3Bucket.Trim(), trimmed);
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? (trimmed, "") : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    /// <summary>Like <see cref="Split"/>, refusing the bucket level (and the bucket list) for any change.</summary>
    private (string Bucket, string Key) Writable(string path)
    {
        var (bucket, key) = Split(path);
        if (bucket.Length == 0 || key.Length == 0)
            throw new UnauthorizedAccessException(IsAccountWide ? BucketLevelReason : "That's the top of the bucket.");
        return (bucket, key);
    }

    /// <summary>Path prefix that items in <paramref name="bucket"/> are shown under.</summary>
    private string BasePath(string bucket) => IsAccountWide ? "/" + bucket + "/" : "/";

    private async Task<IReadOnlyList<FileEntry>> ListBucketsAsync(CancellationToken token)
    {
        var response = await ClientFor(_defaultRegion).ListBucketsAsync(token).ConfigureAwait(false);
        return (response.Buckets ?? new List<S3Bucket>())
            .Select(b => new FileEntry(b.BucketName, "/" + b.BucketName, true, 0,
                b.CreationDate is { } created ? new DateTimeOffset(DateTime.SpecifyKind(created.ToUniversalTime(), DateTimeKind.Utc)) : null,
                Info: "bucket"))
            .ToList();
    }

    /// <summary>
    /// The client for a bucket's region. On AWS the region is looked up once per bucket (falling
    /// back to the configured region if that isn't permitted); custom endpoints use one client.
    /// </summary>
    private async Task<AmazonS3Client> ClientForAsync(string bucket, CancellationToken token)
    {
        if (_customEndpoint)
            return ClientFor(_defaultRegion);
        if (!_bucketRegions.TryGetValue(bucket, out var region))
        {
            region = _defaultRegion;
            try
            {
                var location = await ClientFor(_defaultRegion).GetBucketLocationAsync(bucket, token).ConfigureAwait(false);
                region = location.Location?.Value switch
                {
                    null or "" => "us-east-1", // S3's name for us-east-1
                    "EU" => "eu-west-1",        // legacy name
                    var value => value,
                };
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed)
            {
                // No s3:GetBucketLocation permission: use the configured region.
            }
            _bucketRegions[bucket] = region;
        }
        return ClientFor(region);
    }

    private AmazonS3Client ClientFor(string region) => _clients.GetOrAdd(region, CreateClient);

    private AmazonS3Client CreateClient(string region)
    {
        if (ClientFactory != null)
            return ClientFactory(region);
        var config = new AmazonS3Config
        {
            MaxErrorRetry = 3,
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (_customEndpoint)
        {
            // S3-compatible storage: path-style addressing, and only the checksums every
            // implementation understands (newer AWS-only defaults break many of them).
            config.ServiceURL = _profile.S3ServiceUrl.Trim();
            config.ForcePathStyle = true;
            config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
            config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;
            config.AuthenticationRegion = region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }
        return new AmazonS3Client(new BasicAWSCredentials(_profile.S3AccessKeyId ?? "", _profile.S3SecretAccessKey ?? ""), config);
    }

    // ------------------------------------------------------------------ multipart upload

    private async Task MultipartUploadAsync(AmazonS3Client client, string bucket, string localFile, string key, long size,
        bool resume, IProgress<long> progress, CancellationToken token)
    {
        var parts = new List<PartETag>();
        string? uploadId = null;
        if (resume)
            (uploadId, parts) = await FindResumableUploadAsync(client, bucket, key, size, token).ConfigureAwait(false);
        if (uploadId == null)
        {
            await AbortStaleUploadsAsync(client, bucket, key, token).ConfigureAwait(false);
            uploadId = (await client.InitiateMultipartUploadAsync(bucket, key, token).ConfigureAwait(false)).UploadId;
            parts.Clear();
        }

        var partCount = (int)((size + PartSize - 1) / PartSize);
        var done = parts.Count * PartSize;
        progress.Report(done);
        await using var file = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        for (var number = parts.Count + 1; number <= partCount; number++)
        {
            var offset = (number - 1) * PartSize;
            var length = Math.Min(PartSize, size - offset);
            var buffer = new byte[length];
            file.Seek(offset, SeekOrigin.Begin);
            await file.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
            var request = new UploadPartRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = uploadId,
                PartNumber = number,
                PartSize = length,
                InputStream = new MemoryStream(buffer),
            };
            var before = done;
            request.StreamTransferProgress += (_, e) => progress.Report(before + e.TransferredBytes);
            var response = await client.UploadPartAsync(request, token).ConfigureAwait(false);
            parts.Add(new PartETag(number, response.ETag));
            done += length;
            progress.Report(done);
        }

        await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts,
        }, token).ConfigureAwait(false);
    }

    /// <summary>
    /// An unfinished upload of this key whose parts match this file's part layout, with the
    /// parts already uploaded (in order, without gaps). (null, empty) if there isn't one.
    /// </summary>
    private async Task<(string? UploadId, List<PartETag> Parts)> FindResumableUploadAsync(AmazonS3Client client, string bucket,
        string key, long size, CancellationToken token)
    {
        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key }, token).ConfigureAwait(false);
        var latest = (uploads.MultipartUploads ?? new List<MultipartUpload>())
            .Where(u => u.Key == key)
            .OrderByDescending(u => u.Initiated)
            .FirstOrDefault();
        if (latest == null)
            return (null, new List<PartETag>());

        var parts = new List<PartETag>();
        int? marker = null;
        while (true)
        {
            var listing = await client.ListPartsAsync(new ListPartsRequest
            {
                BucketName = bucket,
                Key = key,
                UploadId = latest.UploadId,
                PartNumberMarker = marker?.ToString(),
            }, token).ConfigureAwait(false);
            foreach (var part in (listing.Parts ?? new List<PartDetail>()).OrderBy(p => p.PartNumber))
            {
                // Only a contiguous run of full-size parts can be reused.
                var expectedLength = Math.Min(PartSize, size - (long)parts.Count * PartSize);
                if (part.PartNumber != parts.Count + 1 || part.Size != expectedLength)
                    return (latest.UploadId, parts);
                parts.Add(new PartETag(part.PartNumber ?? 0, part.ETag));
            }
            if (listing.IsTruncated != true)
                break;
            marker = listing.NextPartNumberMarker;
        }
        return (latest.UploadId, parts);
    }

    private static async Task AbortStaleUploadsAsync(AmazonS3Client client, string bucket, string key, CancellationToken token)
    {
        var uploads = await client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key }, token).ConfigureAwait(false);
        foreach (var upload in (uploads.MultipartUploads ?? new List<MultipartUpload>()).Where(u => u.Key == key))
        {
            try { await client.AbortMultipartUploadAsync(bucket, key, upload.UploadId, token).ConfigureAwait(false); }
            catch (AmazonS3Exception) { }
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task CopyThenDeleteAsync(AmazonS3Client client, string bucket, string fromKey, string toKey, CancellationToken token)
    {
        await client.CopyObjectAsync(bucket, fromKey, bucket, toKey, token).ConfigureAwait(false);
        await client.DeleteObjectAsync(bucket, fromKey, token).ConfigureAwait(false);
    }

    private static async Task<List<string>> AllKeysAsync(AmazonS3Client client, string bucket, string prefix, CancellationToken token)
    {
        var keys = new List<string>();
        string? continuation = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = continuation,
            }, token).ConfigureAwait(false);
            keys.AddRange((response.S3Objects ?? new List<S3Object>()).Select(o => o.Key));
            continuation = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (continuation != null);
        return keys;
    }

    private static FileEntry ToEntry(string path, string name, long size, DateTime? modified, string? storageClass) =>
        new(name, path, false, size,
            modified is { } m ? new DateTimeOffset(DateTime.SpecifyKind(m.ToUniversalTime(), DateTimeKind.Utc)) : null,
            IsHidden: name.StartsWith('.'),
            Info: storageClass);

    private static string LastSegment(string key) => key[(key.LastIndexOf('/') + 1)..];

    /// <summary>Runs an S3 call, translating failures into clear, correctly-classified errors.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> operation, CancellationToken token)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, token) is { } translated)
        {
            throw translated;
        }
    }

    private async Task Guard(Func<Task> operation, CancellationToken token)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (Translate(ex, token) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// Access problems → FileConnectionException / UnauthorizedAccessException (not retried);
    /// network trouble, 5xx and throttling → IOException (retried by the transfer queue).
    /// </summary>
    private Exception? Translate(Exception ex, CancellationToken token)
    {
        if (ex is OperationCanceledException && token.IsCancellationRequested)
            return null;
        if (ex is AmazonS3Exception s3)
        {
            var bucket = IsAccountWide ? "That bucket" : $"Bucket \"{_profile.S3Bucket}\"";
            return s3.ErrorCode switch
            {
                "NoSuchBucket" => new FileConnectionException($"{bucket} doesn't exist (check the name and region).", ex),
                "InvalidAccessKeyId" => new FileConnectionException("The access key ID isn't recognised.", ex),
                "SignatureDoesNotMatch" => new FileConnectionException("The secret access key doesn't match the access key ID.", ex),
                "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException" =>
                    new FileConnectionException($"{bucket} is in a different region; check the region setting.", ex),
                "AccessDenied" or "AllAccessDisabled" => new UnauthorizedAccessException(IsAccountWide && s3.Message.Contains("ListAllMyBuckets", StringComparison.Ordinal)
                    ? "These keys can't list buckets (they need s3:ListAllMyBuckets). Enter a bucket name in the server's settings instead."
                    : "Access denied by S3 (check the IAM permissions for this key).", ex),
                "NoSuchKey" => new FileNotFoundException("That object no longer exists.", ex),
                _ when (int)s3.StatusCode >= 500 || s3.ErrorCode is "SlowDown" or "RequestTimeout" or "Throttling"
                    => new IOException($"S3 is temporarily unavailable ({s3.ErrorCode ?? s3.StatusCode.ToString()}).", ex),
                _ => null,
            };
        }
        if (ex is HttpRequestException or AmazonClientException or TimeoutException
            || ex is OperationCanceledException) // HTTP timeouts surface as cancellation
            return new IOException("Couldn't reach S3: " + ex.Message, ex);
        return null;
    }

    /// <summary>Keeps the GET response alive while its body is read.</summary>
    private sealed class ResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public ResponseStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
