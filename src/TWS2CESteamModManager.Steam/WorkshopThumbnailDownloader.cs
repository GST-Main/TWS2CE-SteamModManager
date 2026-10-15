using System.Net.Http;

namespace TWS2CESteamModManager.Steam;

public static class WorkshopThumbnailDownloader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 8 * 1024 * 1024 };

    /// <summary>Downloads to a new file at basePath plus its detected image extension.</summary>
    public static async Task<string> DownloadAsync(string url, string basePath, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("The thumbnail URL must use HTTP or HTTPS.", nameof(url));
        byte[] bytes = await Client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
        string extension = DetectExtension(bytes);
        string path = Path.GetFullPath(basePath) + extension;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false); }
        catch { stream.Dispose(); File.Delete(path); throw; }
        return path;
    }

    public static string DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return ".gif";
        if (bytes.StartsWith("BM"u8)) return ".bmp";
        throw new InvalidDataException("Steam returned an unsupported thumbnail image format.");
    }
}
