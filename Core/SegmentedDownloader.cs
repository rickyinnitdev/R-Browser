using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace R1.Core;

/// <summary>
/// High-speed multi-threaded segmented downloader.
/// Bypasses single-connection speed throttles (MediaFire, MegaUp, 1Fichier, Google Drive, etc.)
/// by opening parallel HTTP Range streams directly into pre-allocated disk files with zero RAM buffering.
/// </summary>
public static class SegmentedDownloader
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 16
    })
    {
        Timeout = TimeSpan.FromHours(6)
    };

    public sealed class DownloadProgress
    {
        public long TotalBytes;
        public long ReceivedBytes;
        public long BytesPerSecond;
        public string State = "Starting";
        public string? Error;
        public bool IsCompleted;
    }

    public static async Task DownloadAsync(
        string url,
        string destinationPath,
        int maxConnections,
        string? cookies,
        string? referer,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default)
    {
        maxConnections = Math.Clamp(maxConnections, 1, 16);
        var state = new DownloadProgress();

        // 1. Probe server for byte-range support & file size via Range: bytes=0-0
        long totalBytes = -1;
        bool supportsRanges = false;
        string finalUrl = url;

        using (var probeReq = new HttpRequestMessage(HttpMethod.Get, url))
        {
            ApplyHeaders(probeReq, cookies, referer);
            probeReq.Headers.Range = new RangeHeaderValue(0, 0);

            using var probeResp = await Http.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (probeResp.RequestMessage?.RequestUri != null)
                finalUrl = probeResp.RequestMessage.RequestUri.ToString();

            if (probeResp.StatusCode == HttpStatusCode.PartialContent)
            {
                supportsRanges = true;
                if (probeResp.Content.Headers.ContentRange?.Length.HasValue == true)
                    totalBytes = probeResp.Content.Headers.ContentRange.Length.Value;
            }
            else if (probeResp.IsSuccessStatusCode)
            {
                supportsRanges = false;
                totalBytes = probeResp.Content.Headers.ContentLength ?? -1;
            }
            else
            {
                throw new HttpRequestException($"Server responded with status {probeResp.StatusCode}");
            }
        }

        state.TotalBytes = totalBytes;
        state.State = "Downloading";
        progress.Report(state);

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        var partPath = destinationPath + ".r1part";

        // If file is small (< 2 MB) or server does not support ranges, download in single stream
        if (!supportsRanges || totalBytes < 2_000_000 || maxConnections == 1)
        {
            await DownloadSingleStreamAsync(finalUrl, partPath, cookies, referer, totalBytes, state, progress, ct);
            File.Move(partPath, destinationPath, overwrite: true);
            return;
        }

        // 2. Pre-allocate target file on disk to prevent fragmentation & enable parallel seeking
        using (var initFs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            initFs.SetLength(totalBytes);
        }

        // 3. Partition file into parallel chunks
        int connectionCount = (int)Math.Min(maxConnections, Math.Max(1, totalBytes / 1_000_000));
        long chunkSize = totalBytes / connectionCount;
        var chunks = new (long Start, long End)[connectionCount];
        for (int i = 0; i < connectionCount; i++)
        {
            long start = i * chunkSize;
            long end = (i == connectionCount - 1) ? totalBytes - 1 : (start + chunkSize - 1);
            chunks[i] = (start, end);
        }

        long totalReceived = 0;
        long lastSampleReceived = 0;
        var lastSampleTime = DateTime.UtcNow;

        using var speedTimer = new System.Threading.Timer(_ =>
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - lastSampleTime).TotalSeconds;
            if (elapsed >= 0.5)
            {
                long current = Interlocked.Read(ref totalReceived);
                long diff = Math.Max(0, current - lastSampleReceived);
                state.BytesPerSecond = (long)(diff / elapsed);
                state.ReceivedBytes = current;
                lastSampleReceived = current;
                lastSampleTime = now;
                progress.Report(state);
            }
        }, null, 500, 500);

        // 4. Download chunks in parallel directly into assigned byte offsets
        var tasks = chunks.Select(chunk => Task.Run(async () =>
        {
            const int maxRetries = 3;
            long currentStart = chunk.Start;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (currentStart > chunk.End) break;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                    ApplyHeaders(req, cookies, referer);
                    req.Headers.Range = new RangeHeaderValue(currentStart, chunk.End);

                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new HttpRequestException($"Chunk range expected 206 Partial Content but got {resp.StatusCode}");

                    await using var contentStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(
                        partPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        bufferSize: 4096,
                        useAsync: true);

                    fileStream.Seek(currentStart, SeekOrigin.Begin);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                    try
                    {
                        while (currentStart <= chunk.End)
                        {
                            int toRead = (int)Math.Min(buffer.Length, chunk.End - currentStart + 1);
                            int read = await contentStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (read == 0) break;

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            currentStart += read;
                            Interlocked.Add(ref totalReceived, read);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    if (currentStart > chunk.End) break; // chunk finished successfully
                }
                catch when (attempt < maxRetries - 1 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(500 * (attempt + 1), ct);
                }
            }
        }, ct)).ToArray();

        await Task.WhenAll(tasks);
        File.Move(partPath, destinationPath, overwrite: true);

        state.ReceivedBytes = totalBytes;
        state.State = "Completed";
        state.IsCompleted = true;
        state.BytesPerSecond = 0;
        progress.Report(state);
    }

    private static async Task DownloadSingleStreamAsync(
        string url,
        string destinationPath,
        string? cookies,
        string? referer,
        long totalBytes,
        DownloadProgress state,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, cookies, referer);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        if (totalBytes <= 0 && resp.Content.Headers.ContentLength.HasValue)
        {
            totalBytes = resp.Content.Headers.ContentLength.Value;
            state.TotalBytes = totalBytes;
        }

        await using var contentStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

        long received = 0;
        long lastSample = 0;
        var lastTime = DateTime.UtcNow;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int read;
            while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                state.ReceivedBytes = received;

                var now = DateTime.UtcNow;
                if ((now - lastTime).TotalSeconds >= 0.5)
                {
                    state.BytesPerSecond = (long)((received - lastSample) / (now - lastTime).TotalSeconds);
                    lastSample = received;
                    lastTime = now;
                    progress.Report(state);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        state.ReceivedBytes = received;
        state.State = "Completed";
        state.IsCompleted = true;
        state.BytesPerSecond = 0;
        progress.Report(state);
    }

    private static void ApplyHeaders(HttpRequestMessage req, string? cookies, string? referer)
    {
        req.Headers.UserAgent.ParseAdd(DefaultUserAgent);
        req.Headers.Accept.ParseAdd("*/*");
        if (!string.IsNullOrWhiteSpace(cookies))
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out _))
            req.Headers.Referrer = new Uri(referer);
    }
}
