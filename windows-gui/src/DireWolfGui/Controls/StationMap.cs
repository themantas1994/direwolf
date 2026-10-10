using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DireWolfGui.Controls;

/// <summary>A station drawn on the map.  Only stations with a real decoded position are mapped.</summary>
public sealed class MapMarker
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    /// <summary>Older positions, oldest first (real reports only).</summary>
    public IReadOnlyList<Point> Track { get; init; } = [];
    public bool IsStale { get; init; }
    public bool IsObject { get; init; }
    public string? Symbol { get; init; }
}

/// <summary>
/// Web Mercator map of stations.  With network tiles off (the default) a latitude /
/// longitude grid is drawn instead, so the map works offline.  Mouse: drag to pan, wheel
/// to zoom, click to select.  Keyboard: arrows pan, +/- zoom.
/// </summary>
public sealed class StationMap : FrameworkElement
{
    private const double TileSize = 256;
    private Point? _dragStart;
    private double _dragLat, _dragLon;
    private bool _dragged;
    private TileCache? _tiles;

    public StationMap()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public static readonly DependencyProperty CenterLatitudeProperty = Reg(nameof(CenterLatitude), 20.0);
    public static readonly DependencyProperty CenterLongitudeProperty = Reg(nameof(CenterLongitude), 0.0);
    public static readonly DependencyProperty ZoomProperty = Reg(nameof(Zoom), 2.0);
    public static readonly DependencyProperty MarkersProperty = Reg<IEnumerable?>(nameof(Markers), null, OnMarkersChanged);
    public static readonly DependencyProperty SelectedIdProperty = Reg<string?>(nameof(SelectedId), null);
    public static readonly DependencyProperty TilesEnabledProperty = Reg(nameof(TilesEnabled), false, OnTileSourceChanged);
    public static readonly DependencyProperty TileUrlTemplateProperty = Reg(nameof(TileUrlTemplate), "https://tile.openstreetmap.org/{z}/{x}/{y}.png", OnTileSourceChanged);
    public static readonly DependencyProperty TileCacheDirectoryProperty = Reg(nameof(TileCacheDirectory), "", OnTileSourceChanged);
    public static readonly DependencyProperty AttributionProperty = Reg(nameof(Attribution), "© OpenStreetMap contributors");
    public static readonly DependencyProperty HomeLatitudeProperty = Reg<double?>(nameof(HomeLatitude), null);
    public static readonly DependencyProperty HomeLongitudeProperty = Reg<double?>(nameof(HomeLongitude), null);
    public static readonly DependencyProperty ShowTracksProperty = Reg(nameof(ShowTracks), true);
    public static readonly DependencyProperty ShowLabelsProperty = Reg(nameof(ShowLabels), true);

    public double CenterLatitude { get => (double)GetValue(CenterLatitudeProperty); set => SetValue(CenterLatitudeProperty, value); }
    public double CenterLongitude { get => (double)GetValue(CenterLongitudeProperty); set => SetValue(CenterLongitudeProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, Math.Clamp(value, 1, 18)); }
    public IEnumerable? Markers { get => (IEnumerable?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public string? SelectedId { get => (string?)GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public bool TilesEnabled { get => (bool)GetValue(TilesEnabledProperty); set => SetValue(TilesEnabledProperty, value); }
    public string TileUrlTemplate { get => (string)GetValue(TileUrlTemplateProperty); set => SetValue(TileUrlTemplateProperty, value); }
    public string TileCacheDirectory { get => (string)GetValue(TileCacheDirectoryProperty); set => SetValue(TileCacheDirectoryProperty, value); }
    public string Attribution { get => (string)GetValue(AttributionProperty); set => SetValue(AttributionProperty, value); }
    public double? HomeLatitude { get => (double?)GetValue(HomeLatitudeProperty); set => SetValue(HomeLatitudeProperty, value); }
    public double? HomeLongitude { get => (double?)GetValue(HomeLongitudeProperty); set => SetValue(HomeLongitudeProperty, value); }
    public bool ShowTracks { get => (bool)GetValue(ShowTracksProperty); set => SetValue(ShowTracksProperty, value); }
    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    /// <summary>Raised when the user clicks a marker (or empty map: null).</summary>
    public event EventHandler<string?>? MarkerClicked;

    private static DependencyProperty Reg<T>(string name, T def, PropertyChangedCallback? cb = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(StationMap),
            new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender |
                (name is nameof(SelectedId) or nameof(CenterLatitude) or nameof(CenterLongitude) or nameof(Zoom)
                    ? FrameworkPropertyMetadataOptions.BindsTwoWayByDefault : 0), cb));

    private static void OnMarkersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (StationMap)d;
        if (e.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= map.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged n) n.CollectionChanged += map.OnCollectionChanged;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    private static void OnTileSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StationMap)d)._tiles = null;

    /// <summary>Centre and zoom so all given markers are visible.</summary>
    public void FitTo(IEnumerable<MapMarker> markers)
    {
        var list = markers.ToList();
        if (list.Count == 0 || ActualWidth < 10) return;
        double minLat = list.Min(m => m.Latitude), maxLat = list.Max(m => m.Latitude);
        double minLon = list.Min(m => m.Longitude), maxLon = list.Max(m => m.Longitude);
        CenterLatitude = (minLat + maxLat) / 2;
        CenterLongitude = (minLon + maxLon) / 2;
        for (var z = 16.0; z >= 1; z--)
        {
            var a = Project(maxLat, minLon, z);
            var b = Project(minLat, maxLon, z);
            if (Math.Abs(b.X - a.X) < ActualWidth * 0.8 && Math.Abs(b.Y - a.Y) < ActualHeight * 0.8)
            {
                Zoom = z;
                return;
            }
        }
        Zoom = 1;
    }

    // ---- Projection ----

    private static Point Project(double lat, double lon, double zoom)
    {
        lat = Math.Clamp(lat, -85.0511, 85.0511);
        var scale = TileSize * Math.Pow(2, zoom);
        var x = (lon + 180.0) / 360.0 * scale;
        var s = Math.Sin(lat * Math.PI / 180.0);
        var y = (0.5 - Math.Log((1 + s) / (1 - s)) / (4 * Math.PI)) * scale;
        return new Point(x, y);
    }

    private static (double Lat, double Lon) Unproject(Point p, double zoom)
    {
        var scale = TileSize * Math.Pow(2, zoom);
        var lon = p.X / scale * 360.0 - 180.0;
        var n = Math.PI - 2.0 * Math.PI * p.Y / scale;
        var lat = 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        return (lat, lon);
    }

    private Point ToScreen(double lat, double lon)
    {
        var c = Project(CenterLatitude, CenterLongitude, Zoom);
        var p = Project(lat, lon, Zoom);
        return new Point(p.X - c.X + ActualWidth / 2, p.Y - c.Y + ActualHeight / 2);
    }

    private (double Lat, double Lon) FromScreen(Point s)
    {
        var c = Project(CenterLatitude, CenterLongitude, Zoom);
        return Unproject(new Point(s.X - ActualWidth / 2 + c.X, s.Y - ActualHeight / 2 + c.Y), Zoom);
    }

    // ---- Rendering ----

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 1 || h < 1) return;

        var bg = Res("MapBackgroundBrush", Brushes.WhiteSmoke);
        var grid = Res("MapGridBrush", Brushes.LightGray);
        var label = Res("MapLabelBrush", Brushes.Black);
        var accent = Res("AccentBrush", Brushes.SteelBlue);
        var stale = Res("StaleBrush", Brushes.Gray);
        var rx = Res("RxBrush", Brushes.SeaGreen);
        var warn = Res("WarningBrush", Brushes.DarkOrange);
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        string? note = null;
        if (TilesEnabled && !string.IsNullOrWhiteSpace(TileUrlTemplate))
        {
            note = DrawTiles(dc, w, h);
        }
        else
        {
            DrawGraticule(dc, w, h, grid, label, typeface, dpi);
        }

        // Home position (from the station configuration, if known).
        if (HomeLatitude is double hl && HomeLongitude is double ho)
        {
            var p = ToScreen(hl, ho);
            var pen = new Pen(warn, 2);
            dc.DrawLine(pen, new Point(p.X - 7, p.Y), new Point(p.X + 7, p.Y));
            dc.DrawLine(pen, new Point(p.X, p.Y - 7), new Point(p.X, p.Y + 7));
        }

        var markers = Markers?.OfType<MapMarker>().ToList() ?? [];
        if (ShowTracks)
        {
            var trackPen = new Pen(accent, 1.5) { DashStyle = DashStyles.Solid };
            trackPen.Freeze();
            foreach (var m in markers.Where(m => m.Track.Count > 0))
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    var first = true;
                    foreach (var t in m.Track.Append(new Point(m.Longitude, m.Latitude)))
                    {
                        var s = ToScreen(t.Y, t.X);
                        if (first) { ctx.BeginFigure(s, false, false); first = false; }
                        else ctx.LineTo(s, true, false);
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, trackPen, geo);
            }
        }

        var outline = new Pen(bg, 1.5);
        foreach (var m in markers)
        {
            var p = ToScreen(m.Latitude, m.Longitude);
            if (p.X < -50 || p.Y < -50 || p.X > w + 50 || p.Y > h + 50) continue;
            var selected = m.Id == SelectedId;
            var fill = m.IsStale ? stale : (m.IsObject ? warn : rx);
            var r = selected ? 7.0 : 5.0;
            if (m.IsObject) dc.DrawRectangle(fill, outline, new Rect(p.X - r, p.Y - r, 2 * r, 2 * r));
            else dc.DrawEllipse(fill, outline, p, r, r);
            if (selected) dc.DrawEllipse(null, new Pen(accent, 2.5), p, r + 4, r + 4);
            if (ShowLabels || selected)
            {
                var ft = new FormattedText(m.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    selected ? 13 : 11, label, dpi);
                var box = new Rect(p.X + r + 3, p.Y - ft.Height / 2, ft.Width + 4, ft.Height);
                dc.DrawRoundedRectangle(bg, null, box, 3, 3);
                dc.DrawText(ft, new Point(box.X + 2, box.Y));
            }
        }

        DrawScale(dc, w, h, label, typeface, dpi);

        // Attribution is required by tile providers; show it whenever tiles are on.
        var footer = TilesEnabled ? Attribution : "Offline map: latitude/longitude grid (enable map tiles in Settings)";
        if (note is not null) footer = note + "   " + footer;
        var at = new FormattedText(footer, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, label, dpi);
        var rect = new Rect(w - at.Width - 10, h - at.Height - 4, at.Width + 8, at.Height + 2);
        dc.DrawRectangle(bg, null, rect);
        dc.DrawText(at, new Point(rect.X + 4, rect.Y + 1));
    }

    private string? DrawTiles(DrawingContext dc, double w, double h)
    {
        if (_tiles is null || _tiles.UrlTemplate != TileUrlTemplate)
        {
            var dir = string.IsNullOrWhiteSpace(TileCacheDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DireWolfStation", "tiles")
                : TileCacheDirectory;
            _tiles = new TileCache(TileUrlTemplate, dir);
            _tiles.TileArrived += (_, _) => Dispatcher.BeginInvoke(InvalidateVisual);
        }

        var z = (int)Math.Round(Zoom);
        var scale = Math.Pow(2, Zoom - z);
        var c = Project(CenterLatitude, CenterLongitude, z);
        var size = TileSize * scale;
        var originX = w / 2 - c.X * scale;
        var originY = h / 2 - c.Y * scale;
        var n = 1 << z;
        var x0 = (int)Math.Floor(-originX / size);
        var y0 = Math.Max(0, (int)Math.Floor(-originY / size));
        var x1 = (int)Math.Floor((w - originX) / size);
        var y1 = Math.Min(n - 1, (int)Math.Floor((h - originY) / size));
        for (var tx = x0; tx <= x1; tx++)
        {
            for (var ty = y0; ty <= y1; ty++)
            {
                var wrapped = ((tx % n) + n) % n;
                var img = _tiles.Get(z, wrapped, ty);
                if (img is not null)
                    dc.DrawImage(img, new Rect(originX + tx * size, originY + ty * size, size + 0.5, size + 0.5));
            }
        }
        return _tiles.LastError;
    }

    private void DrawGraticule(DrawingContext dc, double w, double h, Brush grid, Brush label, Typeface tf, double dpi)
    {
        var pen = new Pen(grid, 1);
        pen.Freeze();
        var (topLat, leftLon) = FromScreen(new Point(0, 0));
        var (bottomLat, rightLon) = FromScreen(new Point(w, h));
        var span = Math.Max(Math.Abs(rightLon - leftLon), 1e-6);
        double[] steps = [30, 10, 5, 2, 1, 0.5, 0.25, 0.1, 0.05, 0.02, 0.01, 0.005];
        var step = steps.FirstOrDefault(s => span / s >= 4, 0.005);
        for (var lon = Math.Floor(leftLon / step) * step; lon <= rightLon; lon += step)
        {
            var x = ToScreen(CenterLatitude, lon).X;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, h));
            var ft = new FormattedText(FormatDeg(lon, 'E', 'W'), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10, label, dpi);
            dc.DrawText(ft, new Point(x + 2, 2));
        }
        for (var lat = Math.Floor(bottomLat / step) * step; lat <= topLat; lat += step)
        {
            if (lat < -85 || lat > 85) continue;
            var y = ToScreen(lat, CenterLongitude).Y;
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            var ft = new FormattedText(FormatDeg(lat, 'N', 'S'), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 10, label, dpi);
            dc.DrawText(ft, new Point(2, y + 1));
        }
    }

    private static string FormatDeg(double v, char pos, char neg) =>
        Math.Abs(v).ToString(Math.Abs(v) >= 1 || v == 0 ? "0.##" : "0.###", CultureInfo.InvariantCulture) + "°" + (v >= 0 ? pos : neg);

    private void DrawScale(DrawingContext dc, double w, double h, Brush label, Typeface tf, double dpi)
    {
        // Metres per screen pixel at the centre latitude.
        var mpp = 156543.03392 * Math.Cos(CenterLatitude * Math.PI / 180) / Math.Pow(2, Zoom);
        double[] nice = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000, 50000, 100000, 200000, 500000, 1000000, 2000000];
        var metres = nice.LastOrDefault(m => m / mpp <= 120, 1);
        var px = metres / mpp;
        var pen = new Pen(label, 2);
        var y = h - 26;
        dc.DrawLine(pen, new Point(10, y), new Point(10 + px, y));
        dc.DrawLine(pen, new Point(10, y - 4), new Point(10, y + 4));
        dc.DrawLine(pen, new Point(10 + px, y - 4), new Point(10 + px, y + 4));
        var text = metres >= 1000 ? $"{metres / 1000:0} km" : $"{metres:0} m";
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, 11, label, dpi), new Point(14, y - 18));
    }

    // ---- Interaction ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(this);
        _dragLat = CenterLatitude;
        _dragLon = CenterLongitude;
        _dragged = false;
        CaptureMouse();
        if (e.ClickCount == 2)
        {
            ZoomAt(e.GetPosition(this), 1);
            _dragStart = null;
            ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        var d = pos - start;
        if (!_dragged && d.Length < 4) return;
        _dragged = true;
        var c = Project(_dragLat, _dragLon, Zoom);
        var (lat, lon) = Unproject(new Point(c.X - d.X, c.Y - d.Y), Zoom);
        CenterLatitude = Math.Clamp(lat, -85, 85);
        CenterLongitude = ((lon + 540) % 360) - 180;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_dragStart is Point p && !_dragged) SelectAt(p);
        _dragStart = null;
    }

    private void SelectAt(Point p)
    {
        MapMarker? best = null;
        var bestDist = 12.0;
        foreach (var m in Markers?.OfType<MapMarker>() ?? [])
        {
            var d = (ToScreen(m.Latitude, m.Longitude) - p).Length;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        SelectedId = best?.Id;
        MarkerClicked?.Invoke(this, best?.Id);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 0.5 : -0.5);
        e.Handled = true;
    }

    private void ZoomAt(Point screen, double delta)
    {
        var (lat, lon) = FromScreen(screen);
        var newZoom = Math.Clamp(Zoom + delta, 1, 18);
        if (newZoom == Zoom) return;
        // Keep the point under the cursor fixed.
        var target = Project(lat, lon, newZoom);
        var (clat, clon) = Unproject(new Point(target.X - (screen.X - ActualWidth / 2), target.Y - (screen.Y - ActualHeight / 2)), newZoom);
        Zoom = newZoom;
        CenterLatitude = Math.Clamp(clat, -85, 85);
        CenterLongitude = clon;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = 80.0;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus: ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), 1); break;
            case Key.Subtract or Key.OemMinus: ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), -1); break;
            case Key.Left: Pan(-step, 0); break;
            case Key.Right: Pan(step, 0); break;
            case Key.Up: Pan(0, -step); break;
            case Key.Down: Pan(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Pan(double dx, double dy)
    {
        var c = Project(CenterLatitude, CenterLongitude, Zoom);
        var (lat, lon) = Unproject(new Point(c.X + dx, c.Y + dy), Zoom);
        CenterLatitude = Math.Clamp(lat, -85, 85);
        CenterLongitude = lon;
    }
}
