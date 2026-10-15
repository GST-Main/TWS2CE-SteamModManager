using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TWS2CESteamModManager.App.Services;

public interface IThumbnailLoader
{
    Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken);
}

public sealed class ThumbnailLoader : IThumbnailLoader
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 4 * 1024 * 1024
    };
    private readonly SemaphoreSlim slots = new(6);
    private readonly ConcurrentDictionary<string, ImageSource> cache = new();

    public async Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return null;
        if (cache.TryGetValue(url!, out ImageSource? cached)) return cached;
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = await Client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 640;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            if (cache.Count >= 120) cache.Clear();
            cache[url!] = image;
            return image;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or NotSupportedException
            or ArgumentException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
        finally { slots.Release(); }
    }
}
