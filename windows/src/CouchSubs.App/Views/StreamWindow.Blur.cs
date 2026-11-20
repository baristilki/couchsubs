// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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

    private void InitRegionBlur()
    {
        RenderOptions.SetBitmapScalingMode(regionBlur, BitmapScalingMode.Linear);
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

        if (regions.Count == 0 || gameBitmap is null)
        {
            regionBlur.Visibility = Visibility.Collapsed;
            return;
        }
        var group = new GeometryGroup();
        foreach (var rect in regions) group.Children.Add(new RectangleGeometry(rect, corner, corner));
        regionBlur.Source = gameBitmap;
        regionBlur.Clip = group;
        regionBlur.Effect ??= new BlurEffect { KernelType = KernelType.Gaussian };
        ((BlurEffect)regionBlur.Effect).Radius = Math.Max(10, regions.Min(rect => rect.Height) * 0.5);
        regionBlur.Visibility = Visibility.Visible;
    }

    private static GameModeWindow.BlurArea ToPixels(Rect rect, DpiScale dpi, double corner, float darken) => new(
        new Vortice.RawRect((int)Math.Floor(rect.Left * dpi.DpiScaleX), (int)Math.Floor(rect.Top * dpi.DpiScaleY),
            (int)Math.Ceiling(rect.Right * dpi.DpiScaleX), (int)Math.Ceiling(rect.Bottom * dpi.DpiScaleY)),
        (float)(corner * dpi.DpiScaleX), darken);

    /// <summary>Bulanık görüntü yazılmalı mı (DirectX motorunda WriteableBitmap yalnız bunlar için güncellenir).</summary>
    private bool NeedsBitmapForBlur => blurred.Visibility == Visibility.Visible || regionBlur.Visibility == Visibility.Visible;
}
