using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TailSlap;

/// <summary>
/// Converts the hand-drawn mascot artwork into crisp, transparent tray icons.
/// The source PNGs are scanned on white paper, so doing the paper removal before
/// downsampling preserves the pencil edges instead of baking a white halo into
/// the 16/32px icon.
/// </summary>
internal static class TrayIconRenderer
{
    private const int PaperThreshold = 14;
    private const float InkGain = 2.1f;
    private const int ContentAlphaThreshold = 8;
    private const float ContentPaddingRatio = 0.04f;

    private static readonly Color InkColor = Color.FromArgb(31, 31, 31);
    private static readonly Color PlateColor = Color.FromArgb(250, 249, 246);
    private static readonly Color PlateBorderColor = Color.FromArgb(255, 106, 0);

    internal static Icon? FromPngFile(string filePath, int preferredSize, bool cropToContent)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            using var stream = File.OpenRead(filePath);
            return FromPngStream(stream, preferredSize, cropToContent);
        }
        catch
        {
            return null;
        }
    }

    internal static Icon? FromPngStream(Stream stream, int preferredSize, bool cropToContent)
    {
        try
        {
            using var source = new Bitmap(stream);
            return FromBitmap(source, preferredSize, cropToContent);
        }
        catch
        {
            return null;
        }
    }

    internal static Icon? FromIcoFile(string filePath, int preferredSize, bool cropToContent)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            Icon source;
            try
            {
                source = new Icon(filePath, preferredSize, preferredSize);
            }
            catch
            {
                source = new Icon(filePath);
            }

            using (source)
            {
                return FromIcon(source, preferredSize, cropToContent);
            }
        }
        catch
        {
            return null;
        }
    }

    internal static Icon? FromIcoStream(Stream stream, int preferredSize, bool cropToContent)
    {
        try
        {
            Icon source;
            try
            {
                source = new Icon(stream, preferredSize, preferredSize);
            }
            catch
            {
                if (!stream.CanSeek)
                    return null;

                stream.Position = 0;
                source = new Icon(stream);
            }

            using (source)
            {
                return FromIcon(source, preferredSize, cropToContent);
            }
        }
        catch
        {
            return null;
        }
    }

    internal static Bitmap PrepareLineArt(Bitmap source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var prepared = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                prepared.SetPixel(x, y, ToTransparentInk(pixel));
            }
        }

        return prepared;
    }

    internal static Bitmap RenderBitmap(Bitmap source, int preferredSize, bool cropToContent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredSize);

        using var lineArt = PrepareLineArt(source);
        var sourceRect = cropToContent
            ? GetPaddedContentBounds(lineArt)
            : new Rectangle(0, 0, lineArt.Width, lineArt.Height);

        var rendered = new Bitmap(preferredSize, preferredSize, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(rendered);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.GammaCorrected;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.Clear(Color.Transparent);
        DrawIconPlate(graphics, preferredSize);
        graphics.CompositingMode = CompositingMode.SourceOver;

        const int inset = 1;
        float scale = Math.Min(
            (preferredSize - inset * 2f) / sourceRect.Width,
            (preferredSize - inset * 2f) / sourceRect.Height
        );
        float destinationWidth = sourceRect.Width * scale;
        float destinationHeight = sourceRect.Height * scale;
        float destinationX = (preferredSize - destinationWidth) / 2f;
        float destinationY = (preferredSize - destinationHeight) / 2f;

        graphics.DrawImage(
            lineArt,
            new RectangleF(destinationX, destinationY, destinationWidth, destinationHeight),
            sourceRect,
            GraphicsUnit.Pixel
        );

        return rendered;
    }

    private static void DrawIconPlate(Graphics graphics, int preferredSize)
    {
        float inset = Math.Max(1f, preferredSize * 0.06f);
        var plateRect = new RectangleF(
            inset,
            inset,
            preferredSize - inset * 2f,
            preferredSize - inset * 2f
        );
        float radius = Math.Max(2f, preferredSize * 0.22f);

        using var platePath = CreateRoundedRectanglePath(plateRect, radius);
        using var plateBrush = new SolidBrush(PlateColor);
        graphics.FillPath(plateBrush, platePath);

        using var borderPen = new Pen(PlateBorderColor, Math.Max(1f, preferredSize / 24f));
        borderPen.Alignment = PenAlignment.Inset;
        graphics.DrawPath(borderPen, platePath);
    }

    private static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2f;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon? FromBitmap(Bitmap source, int preferredSize, bool cropToContent)
    {
        try
        {
            using var rendered = RenderBitmap(source, preferredSize, cropToContent);
            IntPtr handle = rendered.GetHicon();
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                using var temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static Icon? FromIcon(Icon source, int preferredSize, bool cropToContent)
    {
        using var bitmap = source.ToBitmap();
        return FromBitmap(bitmap, preferredSize, cropToContent);
    }

    private static Color ToTransparentInk(Color pixel)
    {
        if (pixel.A == 0)
            return Color.Transparent;

        int brightest = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
        int darkness = 255 - brightest;
        if (darkness <= PaperThreshold)
            return Color.Transparent;

        int alpha = (int)MathF.Min(255f, (darkness - PaperThreshold) * InkGain);
        alpha = alpha * pixel.A / 255;

        // The current mascot is pencil linework. Normalizing it to the app's
        // near-black ink keeps faint strokes legible at the tray's tiny size.
        return Color.FromArgb(alpha, InkColor);
    }

    private static Rectangle GetPaddedContentBounds(Bitmap bitmap)
    {
        int minX = bitmap.Width;
        int minY = bitmap.Height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A < ContentAlphaThreshold)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
            return new Rectangle(0, 0, bitmap.Width, bitmap.Height);

        int padding = Math.Max(
            3,
            (int)Math.Round(Math.Max(maxX - minX + 1, maxY - minY + 1) * ContentPaddingRatio)
        );
        minX = Math.Max(0, minX - padding);
        minY = Math.Max(0, minY - padding);
        maxX = Math.Min(bitmap.Width - 1, maxX + padding);
        maxY = Math.Min(bitmap.Height - 1, maxY + padding);

        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
