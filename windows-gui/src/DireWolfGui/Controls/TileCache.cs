using System.Collections.Concurrent;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace DireWolfGui.Controls;

/// <summary>
/// Map tile source with a disk cache and a small memory cache.  Downloads are limited
/// to two at a time, identify the application (tile server usage policies require it)
/// and failed tiles are not retried for a while.  Nothing is downloaded unless the user
/// enabled network tiles.
/// </summary>
public sealed class TileCache
{
    private static readonly HttpClient Http = CreateClient();
    private readonly string _urlTemplate;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, BitmapSource> _memory = new();
    private readonly ConcurrentQueue<string> _memoryOrder = new();
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();
    private readonly ConcurrentDictionary<string, byte> _pending = new();
    private readonly SemaphoreSlim _downloads = new(2);
    private const int MemoryLimit = 300;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public TileCache(string urlTemplate, string cacheDir)
    {
        _urlTemplate = urlTemplate;
        _cacheDir = cacheDir;
    }

    public string UrlTemplate => _urlTemplate;

    /// <summary>Last download problem, shown on the map; null when tiles load.</summary>
    public string? LastError { get; private set; }

    public event EventHandler? TileArrived;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var v = typeof(TileCache).Assembly.GetName().Version?.ToString(3) ?? "0";
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"DireWolfStation/{v} (+https://github.com/themantas1994/direwolf)");
        return c;
    }

    /// <summary>Returns the tile if available now, otherwise starts loading it and returns null.</summary>
    public BitmapSource? Get(int z, int x, int y)
    {
        var key = $"{z}/{x}/{y}";
        if (_memory.TryGetValue(key, out var bmp)) return bmp;
        if (_failed.TryGetValue(key, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(2)) return null;
        if (_pending.TryAdd(key, 0)) _ = LoadAsync(key, z, x, y);
        return null;
    }

    private async Task LoadAsync(string key, int z, int x, int y)
    {
        try
        {
            var file = Path.Combine(_cacheDir, z.ToString(), x.ToString(), y + ".png");
            byte[]? data = null;
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < MaxAge)
                data = await File.ReadAllBytesAsync(file);

            if (data is null)
            {
                await _downloads.WaitAsync();
                try
                {
                    var url = _urlTemplate.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
                    data = await Http.GetByteArrayAsync(url);
                    LastError = null;
                }
                catch (Exception ex)
                {
                    // Fall back to an expired cached copy rather than nothing.
                    if (File.Exists(file)) data = await File.ReadAllBytesAsync(file);
                    else throw new IOException(ex.Message, ex);
                }
                finally
                {
                    _downloads.Release();
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, data);
                }
                catch
                {
                    // Cache is best effort.
                }
            }

            var image = Decode(data);
            _memory[key] = image;
            _memoryOrder.Enqueue(key);
            while (_memory.Count > MemoryLimit && _memoryOrder.TryDequeue(out var old)) _memory.TryRemove(old, out _);
            TileArrived?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _failed[key] = DateTime.UtcNow;
            LastError = "Map tiles unavailable: " + ex.Message;
            TileArrived?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private static BitmapSource Decode(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
