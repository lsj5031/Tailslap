using System.Drawing;
using System.Drawing.Imaging;
using TailSlap;
using Xunit;

namespace TailSlap.Tests;

public sealed class TrayIconRendererTests
{
    [Fact]
    public void PrepareLineArt_RemovesPaperAndPreservesDarkInk()
    {
        using var source = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            using var pen = new Pen(Color.Black, 2f);
            graphics.DrawLine(pen, 1, 1, 6, 6);
        }

        using var prepared = TrayIconRenderer.PrepareLineArt(source);

        Assert.Equal(0, prepared.GetPixel(0, 0).A);
        Assert.True(prepared.GetPixel(3, 3).A > 200);
    }

    [Fact]
    public void RenderBitmap_UsesReadablePlateAndTransparentOutside()
    {
        using var source = new Bitmap(24, 24, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(Color.Black);
            graphics.FillEllipse(brush, 7, 7, 10, 10);
        }

        using var rendered = TrayIconRenderer.RenderBitmap(source, 32, cropToContent: true);

        Assert.Equal(0, rendered.GetPixel(0, 0).A);
        Assert.Equal(255, rendered.GetPixel(16, 16).A);
        Assert.InRange(rendered.GetPixel(5, 16).R, 245, 255);
    }
}
