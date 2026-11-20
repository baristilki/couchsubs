// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Fork: ek alan kutularının (sağdaki etkileşim menüsü, telefon, görev) arkası oyunun görüntüsünden bulanık gösterilir;
/// kutu yarı saydam koyu kalır, oyun arkadan seçilir (altyazının bulanık örtüsü gibi). Yayın penceresinde bulanıklık
/// WPF ile çizilir (tek görüntü, kutuların birleşik kırpma alanı); oyun modunda altyazı katmanı arkasını göremediği için
/// kutuların yerleri oyun penceresine bildirilir, bulanıklığı ekran kartında o çizer.
/// </summary>
public sealed partial class StreamWindow
{
    /// <summary>Ek alan kutularının arkasındaki bulanık oyun görüntüsü (yalnız kutuların içi görünür).</summary>
    private readonly Image regionBlur = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    /// <summary>Altyazının bulanık örtüsünün alanı (oyun modu katmanında oyun penceresine bildirilir).</summary>
    private Rect? blurCoverArea;
    /// <summary>Oyun modu katmanı: kutu yerleri buraya bildirilir (asıl pencere oyun penceresine iletir).</summary>
    private Action<IReadOnlyList<GameModeWindow.BlurArea>>? blurSink;
    private bool blurQueued;

    /// <summary>Ek alan kutularının yarı saydam koyu zemini (arkası bulanık).</summary>
    private static readonly Brush RegionGlass = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0x0B, 0x0B, 0x0D)));

    private bool BlurRegions => controller.StreamBackground == StreamBackground.Game;

    /// <summary>
    /// Fork: bulanıklık için karenin 1/8'i boyunda kopyası. Küçük görüntü yumuşak büyütülünce kendiliğinden bulanık
    /// görünür; tam boy görüntüde WPF BlurEffect her karede ekran kartını %80'in üstünde meşgul ediyor, pencere 18 Hz'e
    /// düşüyordu.
    /// </summary>
    private WriteableBitmap? blurBitmap;
    private byte[] blurPixels = [];
    private const int BlurScale = 8;

    /// <summary>Bulanıklık kopyasını kareden günceller (her kutunun 4 noktasının ortalaması).</summary>
    private void UpdateBlurBitmap(byte[] pixels, int width, int height)
    {
        int w = Math.Max(1, width / BlurScale), h = Math.Max(1, height / BlurScale);
        if (blurBitmap is null || blurBitmap.PixelWidth != w || blurBitmap.PixelHeight != h)
        {
            blurBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            blurPixels = new byte[w * h * 4];
            if (blurred.Visibility == Visibility.Visible) blurred.Source = blurBitmap;
            if (regionBlur.Visibility == Visibility.Visible) regionBlur.Source = blurBitmap;
        }
        var stride = width * 4;
        const int a = BlurScale / 4, b = BlurScale * 3 / 4;
        for (var y = 0; y < h; y++)
        {
            int row1 = ((y * BlurScale) + a) * stride, row2 = ((y * BlurScale) + b) * stride;
            var target = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                int c1 = ((x * BlurScale) + a) * 4, c2 = ((x * BlurScale) + b) * 4;
                for (var k = 0; k < 3; k++)
                    blurPixels[target + k] = (byte)((pixels[row1 + c1 + k] + pixels[row1 + c2 + k] + pixels[row2 + c1 + k] + pixels[row2 + c2 + k]) >> 2);
                target += 4;
            }
        }
        blurBitmap.WritePixels(new Int32Rect(0, 0, w, h), blurPixels, w * 4, 0);
    }

    private void InitRegionBlur()
    {
        RenderOptions.SetBitmapScalingMode(regionBlur, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(blurred, BitmapScalingMode.HighQuality);
        root.Children.Insert(root.Children.IndexOf(game) + 1, regionBlur);
    }

    /// <summary>Kutular yerleştikten sonra arkalarının bulanık alanı güncellenir.</summary>
    private void QueueBlurAreas()
    {
        if (blurQueued) return;
        blurQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            blurQueued = false;
            UpdateBlurAreas();
        });
    }

    private void UpdateBlurAreas()
    {
        var regions = new List<Rect>();
        if (BlurRegions)
            foreach (var child in regionLayer.Children.OfType<FrameworkElement>())
                if (child.IsVisible && child.ActualWidth > 0 && child.ActualHeight > 0)
                    regions.Add(child.TransformToAncestor(root).TransformBounds(new Rect(child.RenderSize)));
        var corner = 6 * PictureRect().Height / 1080.0;

        if (overlay)
        {
            if (blurSink is null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var areas = new List<GameModeWindow.BlurArea>();
            if (blurCoverArea is { } cover) areas.Add(ToPixels(cover, dpi, 8 * Scale, 0.19f));
            foreach (var rect in regions) areas.Add(ToPixels(rect, dpi, corner, 0.25f));
            blurSink(areas);
            return;
        }

        if (regions.Count == 0 || (gameBitmap is null && blurBitmap is null))
        {
            regionBlur.Visibility = Visibility.Collapsed;
            return;
        }
        var group = new GeometryGroup();
        foreach (var rect in regions) group.Children.Add(new RectangleGeometry(rect, corner, corner));
        regionBlur.Source = (ImageSource?)blurBitmap ?? gameBitmap;
        regionBlur.Clip = group;
        regionBlur.Effect = null;
        regionBlur.Visibility = Visibility.Visible;
    }

    private static GameModeWindow.BlurArea ToPixels(Rect rect, DpiScale dpi, double corner, float darken) => new(
        new Vortice.RawRect((int)Math.Floor(rect.Left * dpi.DpiScaleX), (int)Math.Floor(rect.Top * dpi.DpiScaleY),
            (int)Math.Ceiling(rect.Right * dpi.DpiScaleX), (int)Math.Ceiling(rect.Bottom * dpi.DpiScaleY)),
        (float)(corner * dpi.DpiScaleX), darken);

    /// <summary>Bulanık görüntü yazılmalı mı (DirectX motorunda WriteableBitmap yalnız bunlar için güncellenir).</summary>
    private bool NeedsBitmapForBlur => blurred.Visibility == Visibility.Visible || regionBlur.Visibility == Visibility.Visible;
}
