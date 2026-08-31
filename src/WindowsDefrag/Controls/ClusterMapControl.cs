using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDefrag.Models;

namespace WindowsDefrag.Controls;

/// <summary>Paints the volume as a grid of coloured cluster blocks.</summary>
public sealed class ClusterMapControl : FrameworkElement
{
    public static readonly DependencyProperty MapProperty =
        DependencyProperty.Register(nameof(Map), typeof(ClusterKind[]), typeof(ClusterMapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

    public static readonly DependencyProperty BlockSizeProperty =
        DependencyProperty.Register(nameof(BlockSize), typeof(int), typeof(ClusterMapControl),
            new FrameworkPropertyMetadata(8, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShadedProperty =
        DependencyProperty.Register(nameof(Shaded), typeof(bool), typeof(ClusterMapControl),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnVisualPropertyChanged));

    public static readonly DependencyProperty ClustersPerBlockProperty =
        DependencyProperty.Register(nameof(ClustersPerBlock), typeof(long), typeof(ClusterMapControl),
            new FrameworkPropertyMetadata(1L));

    public event Action<int, long, long>? BlockActivated;
    public event Action<int, long, long, ClusterKind>? BlockHovered;

    private WriteableBitmap? _bmp;
    private int _cols;
    private int _rows;
    private int _drawn;
    private int _bmpW;
    private int _bmpH;
    private bool _redrawing;

    public ClusterMapControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        Focusable = false;
    }

    public ClusterKind[]? Map
    {
        get => (ClusterKind[]?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public int BlockSize
    {
        get => (int)GetValue(BlockSizeProperty);
        set => SetValue(BlockSizeProperty, value);
    }

    public bool Shaded
    {
        get => (bool)GetValue(ShadedProperty);
        set => SetValue(ShadedProperty, value);
    }

    public long ClustersPerBlock
    {
        get => (long)GetValue(ClustersPerBlockProperty);
        set => SetValue(ClustersPerBlockProperty, value);
    }

    public int HitTestBlock(Point pt)
    {
        var cell = Math.Max(4, BlockSize);
        if (_cols <= 0) return -1;
        var col = (int)(pt.X / cell);
        var row = (int)(pt.Y / cell);
        if (col < 0 || row < 0 || col >= _cols || row >= _rows)
            return -1;
        var index = row * _cols + col;
        return index < _drawn ? index : -1;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 640 : Math.Max(0, availableSize.Width);
        var h = double.IsInfinity(availableSize.Height) ? 240 : Math.Max(0, availableSize.Height);
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        RebuildBitmap((int)Math.Max(1, finalSize.Width), (int)Math.Max(1, finalSize.Height));
        return finalSize;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTestBlock(e.GetPosition(this));
        var src = Map;
        if (index < 0 || src is null || src.Length == 0)
            return;

        var srcIndex = src.Length == _drawn
            ? index
            : (int)((long)index * src.Length / Math.Max(1, _drawn));
        srcIndex = Math.Clamp(srcIndex, 0, src.Length - 1);
        var per = Math.Max(1, ClustersPerBlock);
        BlockHovered?.Invoke(srcIndex, srcIndex * per, per, src[srcIndex]);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount < 2)
            return;
        var index = HitTestBlock(e.GetPosition(this));
        var src = Map;
        if (index < 0)
            return;
        var srcIndex = src is { Length: > 0 } && src.Length != _drawn
            ? (int)((long)index * src.Length / Math.Max(1, _drawn))
            : index;
        if (src is { Length: > 0 })
            srcIndex = Math.Clamp(srcIndex, 0, src.Length - 1);
        var per = Math.Max(1, ClustersPerBlock);
        BlockActivated?.Invoke(srcIndex, srcIndex * per, per);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = RenderSize.Width;
        var h = RenderSize.Height;
        if (w < 1 || h < 1)
            return;

        if (_bmp is null || _bmpW != (int)w || _bmpH != (int)h)
            RebuildBitmap((int)w, (int)h);

        if (_bmp is not null)
            drawingContext.DrawImage(_bmp, new Rect(0, 0, w, h));
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ClusterMapControl map)
            map.RebuildBitmap((int)Math.Max(1, map.ActualWidth), (int)Math.Max(1, map.ActualHeight), invalidate: true);
    }

    private void RebuildBitmap(int w, int h, bool invalidate = false)
    {
        if (_redrawing)
            return;
        if (w < 4 || h < 4)
            return;

        _redrawing = true;
        try
        {
            var cell = Math.Clamp(BlockSize, 4, 24);
            _cols = Math.Max(1, w / cell);
            _rows = Math.Max(1, h / cell);
            var display = _cols * _rows;
            var src = Map;
            _drawn = display;

            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            var stride = bmp.BackBufferStride;
            var pixels = new byte[stride * h];
            FillBackground(pixels);

            for (var i = 0; i < display; i++)
            {
                ClusterKind kind;
                if (src is { Length: > 0 })
                {
                    if (Shaded && src.Length > display)
                    {
                        var from = (int)((long)i * src.Length / display);
                        var to = (int)((long)(i + 1) * src.Length / display);
                        kind = Dominant(src, from, Math.Max(from + 1, to));
                    }
                    else
                    {
                        var srcIndex = src.Length == display
                            ? i
                            : (int)((long)i * src.Length / display);
                        srcIndex = Math.Clamp(srcIndex, 0, src.Length - 1);
                        kind = src[srcIndex];
                    }
                }
                else
                {
                    kind = ClusterKind.Free;
                }

                var col = i % _cols;
                var row = i / _cols;
                FillCell(pixels, stride, col * cell, row * cell, cell, ColorOf(kind));
            }

            bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
            bmp.Freeze();
            _bmp = bmp;
            _bmpW = w;
            _bmpH = h;
        }
        finally
        {
            _redrawing = false;
        }

        if (invalidate)
            InvalidateVisual();
    }

    private static void FillBackground(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xDC;
            pixels[i + 1] = 0xDC;
            pixels[i + 2] = 0xDC;
            pixels[i + 3] = 255;
        }
    }

    private static ClusterKind Dominant(ClusterKind[] src, int from, int to)
    {
        Span<int> counts = stackalloc int[16];
        from = Math.Max(0, from);
        to = Math.Min(to, src.Length);
        var n = 0;
        for (var i = from; i < to; i++)
        {
            var k = (int)src[i];
            if ((uint)k < 16)
                counts[k]++;
            n++;
        }

        if (n == 0)
            return ClusterKind.Free;

        var best = ClusterKind.Free;
        var bestCount = 0;
        for (var k = 0; k < 16; k++)
        {
            if (counts[k] > bestCount)
            {
                bestCount = counts[k];
                best = (ClusterKind)k;
            }
        }

        // A few leftover fragments must not paint the whole cell red.
        if (best == ClusterKind.Fragmented && counts[(int)ClusterKind.Fragmented] * 4 < n)
            best = counts[(int)ClusterKind.Allocated] > 0 ? ClusterKind.Allocated : ClusterKind.Free;

        return best;
    }

    private static void FillCell(byte[] pixels, int stride, int x, int y, int cell, Color color)
    {
        var inner = Math.Max(1, cell - 1);
        var max = pixels.Length - 4;
        for (var py = 0; py < inner; py++)
        {
            var offset = (y + py) * stride + x * 4;
            for (var px = 0; px < inner; px++)
            {
                var o = offset + px * 4;
                if (o > max)
                    return;
                var boost = py == 0 || px == 0 ? 28 : py == inner - 1 || px == inner - 1 ? -22 : 0;
                pixels[o] = Clamp(color.B + boost);
                pixels[o + 1] = Clamp(color.G + boost);
                pixels[o + 2] = Clamp(color.R + boost);
                pixels[o + 3] = 255;
            }
        }
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    public static Color ColorOf(ClusterKind kind) => kind switch
    {
        ClusterKind.Processing => Color.FromRgb(0x00, 0xC0, 0x00),
        ClusterKind.Fragmented => Color.FromRgb(0xE8, 0x11, 0x23),
        ClusterKind.Locked => Color.FromRgb(0x8B, 0x45, 0x13),
        ClusterKind.Paging => Color.FromRgb(0xFF, 0x8C, 0x00),
        ClusterKind.Unmovable => Color.FromRgb(0x3D, 0x3D, 0x3D),
        ClusterKind.Mft => Color.FromRgb(0x9B, 0x59, 0xB6),
        ClusterKind.System => Color.FromRgb(0x50, 0x50, 0x50),
        ClusterKind.Directory => Color.FromRgb(0xE6, 0xB8, 0x00),
        ClusterKind.Compressed => Color.FromRgb(0x00, 0xB7, 0xC3),
        ClusterKind.Allocated => Color.FromRgb(0x00, 0x78, 0xD4),
        _ => Color.FromRgb(0xF4, 0xF4, 0xF4)
    };
}
