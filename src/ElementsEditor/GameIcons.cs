using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ElementsCore.Assets;

namespace ElementsEditor;

/// <summary>
/// Game icons for every tab: opened in the background from the client folder (surfaces.pck +
/// path.data), converted to bitmaps on demand and cached. Raises <see cref="Ready"/> on the UI thread
/// once loaded so grids can repaint.
/// </summary>
public static class GameIcons
{
    private static IconLibrary? _lib;
    private static string? _root;
    private static Task? _loading;
    private static readonly Dictionary<(string, int), Image?> _bitmaps = new();
    private static SynchronizationContext? _ui;

    public static event Action? Ready;
    /// <summary>elements.data record ID → path.data ID of its icon (set when elements.data is loaded).</summary>
    public static Func<long, long?>? ItemIcon { get; set; }

    public static bool Loaded => _lib?.Surfaces != null;
    public static string Status => _lib == null ? (_loading != null ? "đang nạp icon…" : "chưa nạp icon")
        : _lib.Messages.Count > 0 ? string.Join("; ", _lib.Messages) : $"{_lib.IconCount:N0} icon từ surfaces.pck";

    /// <summary>Starts loading the icons of a client (no-op when that client is already loaded / loading).</summary>
    public static void EnsureLoaded(string? clientRoot)
    {
        if (string.IsNullOrEmpty(clientRoot) || string.Equals(clientRoot, _root, StringComparison.OrdinalIgnoreCase)) return;
        _root = clientRoot;
        _ui ??= SynchronizationContext.Current;
        var old = _lib;
        _loading = Task.Run(() =>
        {
            try
            {
                var lib = IconLibrary.Open(clientRoot);
                lock (_bitmaps) { _lib = lib; _bitmaps.Clear(); }
                old?.Dispose();
            }
            catch { }
            if (_ui != null) _ui.Post(_ => Ready?.Invoke(), null);
            else Ready?.Invoke();
        });
    }

    public static string? PathOf(long pathId) => _lib?.PathOf(pathId);

    /// <summary>Icon of a resource path (Surfaces\…\x.dds), or null.</summary>
    public static Image? ForPath(string? path, int size = 24)
    {
        var lib = _lib;
        if (lib == null || string.IsNullOrWhiteSpace(path)) return null;
        lock (_bitmaps)
        {
            if (_bitmaps.TryGetValue((path, size), out var img)) return img;
            img = ToBitmap(lib.Icon(path), size);
            if (_bitmaps.Count > 6000) { foreach (var b in _bitmaps.Values) b?.Dispose(); _bitmaps.Clear(); }
            _bitmaps[(path, size)] = img;
            return img;
        }
    }

    /// <summary>Icon of a path.data ID (elements.data "file_icon").</summary>
    public static Image? ForPathId(long id, int size = 24) => PathOf(id) is { } p ? ForPath(p, size) : null;

    /// <summary>Icon of an elements.data record (item / skill …) by its ID.</summary>
    public static Image? ForItem(long elementsId, int size = 24) =>
        ItemIcon?.Invoke(elementsId) is long pid ? ForPathId(pid, size) : null;

    private static Image? ToBitmap(Bgra? b, int size)
    {
        if (b == null || b.Width <= 0 || b.Height <= 0) return null;
        var src = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
        var data = src.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < b.Height; y++)
            Marshal.Copy(b.Pixels, y * b.Width * 4, data.Scan0 + y * data.Stride, b.Width * 4);
        src.UnlockBits(data);
        if (size <= 0 || (b.Width == size && b.Height == size)) return src;
        // fit into size × size, keeping the aspect ratio
        float k = Math.Min((float)size / b.Width, (float)size / b.Height);
        int w = Math.Max(1, (int)Math.Round(b.Width * k)), h = Math.Max(1, (int)Math.Round(b.Height * k));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }
        src.Dispose();
        return dst;
    }

    /// <summary>Draws an icon centred vertically at the left of a cell; returns the width used.</summary>
    public static int DrawInCell(Graphics g, Image? img, Rectangle cell, int pad = 3)
    {
        if (img == null) return 0;
        int y = cell.Top + (cell.Height - img.Height) / 2;
        g.DrawImage(img, cell.Left + pad, y, img.Width, img.Height);
        return img.Width + pad * 2;
    }
}
