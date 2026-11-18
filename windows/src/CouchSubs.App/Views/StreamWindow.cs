// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// "Yayın penceresi" (Mac: StreamWindow.swift): şeffaflık tanımayan yayın programları (TikTok LIVE Studio, Streamlabs,
/// OBS dışındakiler) için düz renkli zeminde altyazı çizen sıradan bir pencere. Programda "Pencere yakalama" ile eklenir,
/// zemin chroma key ile silinir. Başlığı sabittir ki yakalama listesinde bulunsun. Saydam altyazı penceresinden
/// (<see cref="SubtitleOverlayWindow"/>) ayrıdır; altyazı yalnız TV'de gösterilse de satırlar gelir.
/// Zemin "Oyun görüntüsü" seçiliyse PS5'ten gelen görüntü altyazının arkasına çizilir: pencere Discord ya da OBS'te
/// olduğu gibi yayınlanır, oyun TV'de oynanmaya devam eder.
/// </summary>
public sealed class StreamWindow : Window
{
    private static StreamWindow? open;

    private readonly AppController controller;
    private readonly SubtitleStack stack = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new();
    private readonly StackPanel linesPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    /// <summary>Ek alanların (görev, telefon mesajı, bildirim) çevirileri; saydam penceredeki gibi üstte, alanın yanına yaslı.</summary>
    private readonly StackPanel regionsPanel = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<string, (string Text, CouchSubs.Core.TextBox? Box)> regions = [];
    private readonly Border linesHost;
    private readonly Dictionary<int, Border> boxes = [];
    private double screenUntil;
    /// <summary>Oyun görüntüsü (zemin "Oyun görüntüsü"yken); altyazı katmanlarının arkasında, oranı korunarak.</summary>
    private readonly Image game = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private WriteableBitmap? gameBitmap;
    private long gameVersion = -1;
    private bool rendering;

    private double Now => clock.Elapsed.TotalSeconds;

    /// <summary>Yazı boyu TV'deki gibi 1080 satırlık ekrana göredir; pencerenin içerik yüksekliğine ölçeklenir.</summary>
    private double Scale => Math.Max(0.2, (linesHost.ActualHeight > 0 ? linesHost.ActualHeight : 720) / 1080);

    /// <summary>Pencereyi açar; zaten açıksa öne getirir.</summary>
    public static void Open(AppController controller)
    {
        if (open is { } window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            return;
        }
        open = new StreamWindow(controller);
        open.Show();
    }

    public static bool IsOpen => open is not null;

    /// <summary>Yayın programının pencere listesinde görünen ad; altyazıya göre değişmez (Türkçede "CouchSubs Yayın").</summary>
    public static string WindowTitle => Localizer.Current["CouchSubs Stream"];

    private StreamWindow(AppController controller)
    {
        this.controller = controller;
        Title = WindowTitle;
        // 1280x720 içerik: 16:9, yayın programında kaynak oranı bozulmaz.
        SizeToContent = SizeToContent.Manual;
        Width = 1296;
        Height = 759;
        MinWidth = 400;
        MinHeight = 260;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        // Video kaynağında renkli alt piksel yumuşatma kenarda renk saçağı bırakır; gri tonlu yumuşatma keskin kalır.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(game, BitmapScalingMode.Linear);
        var layers = new Grid();
        layers.Children.Add(regionsPanel);
        layers.Children.Add(linesPanel);
        linesHost = new Border { Child = layers };
        // Görüntü kenar boşluğu (altyazı payı) olmadan tüm pencereyi kaplar; altyazı katmanı üstündedir.
        var root = new Grid();
        root.Children.Add(game);
        root.Children.Add(linesHost);
        Content = root;
        ApplyBackground();
        linesHost.SizeChanged += (_, _) =>
        {
            linesHost.Padding = new Thickness(24 * Scale, 0, 24 * Scale, linesHost.ActualHeight * 0.07);
            regionsPanel.Margin = new Thickness(0, 16 * Scale, 0, 0);
            boxes.Clear();
            Refresh();
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        controller.StreamEvent += OnEvent;
        controller.StreamHidden += OnHidden;
        controller.PropertyChanged += OnChanged;
        controller.GameVideoWanted = true;
        UpdateGameRendering();
        Closed += (_, _) =>
        {
            controller.StreamEvent -= OnEvent;
            controller.StreamHidden -= OnHidden;
            controller.PropertyChanged -= OnChanged;
            controller.GameVideoWanted = false;
            StopGameRendering();
            timer.Stop();
            open = null;
        };
    }

    private void ApplyBackground()
    {
        var (red, green, blue) = controller.StreamBackground.Rgb();
        // Oyun görüntüsü varken zemin görüntünün kendisidir; kenar payları (16:9 dışı pencerede) siyah kalır.
        Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        linesHost.Background = Brushes.Transparent;
    }

    /// <summary>Oyun görüntüsü açıkken her ekran yenilemesinde yeni kare var mı bakılır; yoksa kopyalanmaz.</summary>
    private void UpdateGameRendering()
    {
        if (controller.GameVideoActive) StartGameRendering();
        else StopGameRendering();
    }

    private void StartGameRendering()
    {
        if (rendering) return;
        rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopGameRendering()
    {
        if (rendering) CompositionTarget.Rendering -= OnRendering;
        rendering = false;
        game.Visibility = Visibility.Collapsed;
        game.Source = null;
        gameBitmap = null;
        gameVersion = -1;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!controller.GameVideo.HasFrame)
        {
            // Akış durdu ya da henüz kare gelmedi: eski kare donup kalmasın.
            if (game.Visibility == Visibility.Visible) game.Visibility = Visibility.Collapsed;
            gameVersion = -1;
            return;
        }
        var newer = controller.GameVideo.ReadIfNewer(gameVersion, (pixels, width, height) =>
        {
            if (gameBitmap is null || gameBitmap.PixelWidth != width || gameBitmap.PixelHeight != height)
            {
                gameBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                game.Source = gameBitmap;
            }
            gameBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        });
        if (newer is not { } version) return;
        gameVersion = version;
        if (game.Visibility != Visibility.Visible) game.Visibility = Visibility.Visible;
        CountShownFrame();
    }

    private int shownFrames;
    private long shownSince = Environment.TickCount64;

    /// <summary>Takılma şikâyetinde bakılabilsin: gösterilen kare hızı 10 sn'de bir günlüğe yazılır.</summary>
    private void CountShownFrame()
    {
        shownFrames++;
        var elapsed = Environment.TickCount64 - shownSince;
        if (elapsed < 10_000) return;
        CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: {shownFrames * 1000.0 / elapsed:0.0} kare/sn gösterildi");
        shownFrames = 0;
        shownSince = Environment.TickCount64;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.GameVideoActive)) UpdateGameRendering();
        if (e.PropertyName == nameof(AppController.StreamBackground))
        {
            ApplyBackground();
            // Oyun görüntüsüyle düz zemin arasında kutu görünümü farklı: kutular yeniden kurulur.
            boxes.Clear();
            Refresh();
        }
        if (e.PropertyName != nameof(AppController.Settings)) return;
        // Yazı boyu, rengi ya da zemini değişmiş olabilir; kutular yeniden kurulur.
        boxes.Clear();
        Refresh();
    }

    /// <summary>Saydam penceredekiyle aynı kurallar (bkz. <see cref="SubtitleOverlayWindow"/>).</summary>
    private void OnEvent(SubtitleEvent e)
    {
        stack.StackPrevious = controller.Settings.Style.StackPrevious;
        switch (e.Kind)
        {
            case SubtitleEventKind.Subtitle:
                screenUntil = 0;
                stack.Show(e.Text, e.DurationMs, e.Paragraphs, Now, e.Replaces);
                break;
            case SubtitleEventKind.Screen:
                stack.Show(e.Text, e.DurationMs, paragraphs: true, Now);
                screenUntil = Now + e.DurationMs / 1000.0;
                break;
            case SubtitleEventKind.ScreenClear:
                if (screenUntil > Now) stack.Hide();
                screenUntil = 0;
                break;
            // Ek alan olayı altyazıya dokunmaz: önceden "temizle" sayılıp yayındaki altyazıyı siliyordu ve çeviri hiç
            // görünmüyordu.
            case SubtitleEventKind.Region:
                if (e.Id is null) break;
                regions.Remove(e.Id);
                if (e.Text.Length > 0) regions[e.Id] = (e.Text, e.Box);
                break;
            case SubtitleEventKind.RegionClear:
                regions.Clear();
                break;
            default:
                if (screenUntil <= Now) stack.Clear(Now);
                break;
        }
        Refresh();
    }

    private void OnHidden()
    {
        stack.Hide();
        regions.Clear();
        Refresh();
    }

    private void Refresh()
    {
        var now = Now;
        var lines = stack.Lines(now).ToList();
        regionsPanel.Children.Clear();
        foreach (var (text, area) in regions.Values.OrderBy(region => region.Box?.Top ?? 0)) regionsPanel.Children.Add(MakeRegionBox(text, area));
        linesPanel.Children.Clear();
        foreach (var id in boxes.Keys.Except(lines.Select(l => l.Id)).ToList()) boxes.Remove(id);
        foreach (var line in lines)
        {
            if (!boxes.TryGetValue(line.Id, out var box))
            {
                box = MakeBox(line);
                boxes[line.Id] = box;
            }
            if (line.Fading && box.Tag is not "fading")
            {
                box.Tag = "fading";
                box.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromSeconds(SubtitleStack.PreviousFade)));
            }
            linesPanel.Children.Add(box);
        }
        timer.Stop();
        if (stack.NextChange(now) is { } next)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Max(0.01, next - now));
            timer.Start();
        }
    }

    /// <summary>
    /// TV'deki altyazı kutusu; düz zeminde yarı saydamlık zeminle karışıp chroma key'i bozacağından kutu tam koyudur
    /// (ya da hiç yoktur) ve üst satırlar yazı renginden soldurulur.
    /// </summary>
    private Border MakeBox(SubtitleStack.Line line)
    {
        if (controller.StreamBackground == StreamBackground.Game) return MakeGameBox(line);
        var style = controller.Settings.Style;
        var scale = Scale;
        var current = line.Role == SubtitleStack.Role.Current;
        var wrapped = line.Wrapped;
        var size = Math.Max(1, OverlayPlacement.FontSize(style.FontSize) * scale * (current ? 1 : 0.8) * wrapped.Scale);
        var boxed = StreamBackgrounds.BoxOpacity(style.Background) > 0;
        var dim = line.Role switch { SubtitleStack.Role.Current => 1.0, SubtitleStack.Role.Previous => 0.6, _ => 0.45 };
        var color = style.TextColor == SubtitleTextColor.Yellow ? Color.FromRgb(0xFF, 0xE1, 0x35) : Colors.White;
        if (boxed) color.A = (byte)Math.Round(dim * 255);
        var text = new TextBlock
        {
            Text = wrapped.Text,
            FontSize = size,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = line.Paragraphs ? TextAlignment.Left : TextAlignment.Center,
            LineHeight = size * 1.3,
            Effect = boxed ? null : new DropShadowEffect { Color = Colors.Black, ShadowDepth = 2 * scale, BlurRadius = 4 * scale, Opacity = 1, Direction = 270 },
        };
        return new Border
        {
            Background = boxed ? Brushes.Black : Brushes.Transparent,
            CornerRadius = new CornerRadius(10 * scale),
            Padding = current ? new Thickness(26 * scale, 10 * scale, 26 * scale, 10 * scale) : new Thickness(20 * scale, 6 * scale, 20 * scale, 6 * scale),
            Margin = new Thickness(0, 14 * scale, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = text,
        };
    }

    // --- Oyun görüntüsü zemini: oyun altyazısına benzeyen görünüm ---
    // Zemin görüntünün kendisi olduğundan chroma key kuralı (tam opak kutu) gerekmez: kutu Ayarlar'daki koyulukla yarı
    // saydam, yazı oyun altyazıları gibi yumuşak gölgeli beyaz. Önceki satırlar soluklaştırılır, konuşan adı vurgulanır.

    /// <summary>Konuşan adı sönük ve küçük: oyunlar (ör. Spider-Man) hemen her satırı adla başlatır; parlak renk göze batıyordu.</summary>
    private static readonly Color SpeakerColor = Color.FromRgb(0xB9, 0xBC, 0xC4);
    private static readonly FontFamily GameFont = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>"Miles: ..." biçiminde baştaki konuşan adı (en çok 3 sözcük); yoksa null.</summary>
    internal static (string Speaker, string Line)? SplitSpeaker(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 1 || colon > 24 || colon + 1 >= text.Length) return null;
        var name = text[..colon];
        if (name.Contains('\n') || name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return null;
        if (!name.All(c => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-')) return null;
        return (name.Trim(), text[(colon + 1)..].TrimStart());
    }

    private Border MakeGameBox(SubtitleStack.Line line)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var current = line.Role == SubtitleStack.Role.Current;
        var wrapped = line.Wrapped;
        var size = Math.Max(1, OverlayPlacement.FontSize(style.FontSize) * scale * (current ? 1 : 0.82) * wrapped.Scale);
        // Ayarlardaki koyuluk (vars. %72) TV'deki kutuya göredir; oyunun üstünde biraz daha hafif durur.
        var boxOpacity = Math.Clamp(style.Background / 100.0, 0, 1) * 0.8;
        var boxed = boxOpacity > 0.02;
        var color = style.TextColor == SubtitleTextColor.Yellow ? Color.FromRgb(0xFF, 0xE1, 0x35) : Color.FromRgb(0xF7, 0xF7, 0xF5);
        var text = new TextBlock
        {
            FontFamily = GameFont,
            FontSize = size,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Medium,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = line.Paragraphs ? TextAlignment.Left : TextAlignment.Center,
            LineHeight = size * 1.28,
            // Açık sahnede de okunsun: kutusuzken belirgin, kutuluyken hafif gölge (tek efekt, akışı yormaz).
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = (boxed ? 1 : 1.6) * scale,
                BlurRadius = (boxed ? 4 : 7) * scale,
                Opacity = boxed ? 0.55 : 0.95,
            },
        };
        if (SplitSpeaker(wrapped.Text) is { } split)
        {
            text.Inlines.Add(new System.Windows.Documents.Run(split.Speaker + ": ")
            {
                Foreground = new SolidColorBrush(SpeakerColor),
                FontWeight = FontWeights.Normal,
                FontSize = size * 0.82,
            });
            text.Inlines.Add(new System.Windows.Documents.Run(split.Line));
        }
        else
        {
            text.Text = wrapped.Text;
        }
        return new Border
        {
            Background = boxed ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(boxOpacity * 255), 0x0B, 0x0B, 0x0D)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(6 * scale),
            Padding = current ? new Thickness(18 * scale, 7 * scale, 18 * scale, 8 * scale) : new Thickness(14 * scale, 4 * scale, 14 * scale, 5 * scale),
            Margin = new Thickness(0, 8 * scale, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = Math.Max(200, linesHost.ActualWidth * 0.8),
            Opacity = line.Role switch { SubtitleStack.Role.Current => 1.0, SubtitleStack.Role.Previous => 0.7, _ => 0.5 },
            Child = text,
        };
    }

    /// <summary>Oyun görüntüsü zemininde ek alan kutusu: altyazıyla aynı dilde, daha küçük ve yarı saydam.</summary>
    private Border MakeGameRegionBox(string text, CouchSubs.Core.TextBox? area)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var size = Math.Max(12, OverlayPlacement.FontSize(style.FontSize) * scale * 0.52);
        var middle = area is { } box ? (box.Left + box.Right) / 2 : 0.5;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xA6, 0x0B, 0x0B, 0x0D)),
            CornerRadius = new CornerRadius(6 * scale),
            Padding = new Thickness(12 * scale, 5 * scale, 12 * scale, 6 * scale),
            Margin = new Thickness(0, 0, 0, 8 * scale),
            HorizontalAlignment = middle < 0.4 ? HorizontalAlignment.Left : middle > 0.6 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = GameFont,
                FontSize = size,
                FontWeight = FontWeights.Medium,
                Foreground = style.TextColor == SubtitleTextColor.Yellow ? new SolidColorBrush(Color.FromRgb(0xFF, 0xE1, 0x35)) : new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF5)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.25,
            },
        };
    }

    /// <summary>Ek alanın çevirisi: altyazıdan küçük, tam koyu kutu (düz zeminde chroma key bozulmasın); alanın yanına yaslı.</summary>
    private Border MakeRegionBox(string text, CouchSubs.Core.TextBox? area)
    {
        if (controller.StreamBackground == StreamBackground.Game) return MakeGameRegionBox(text, area);
        var style = controller.Settings.Style;
        var scale = Scale;
        var size = Math.Max(12, OverlayPlacement.FontSize(style.FontSize) * scale * 0.55);
        var middle = area is { } box ? (box.Left + box.Right) / 2 : 0.5;
        return new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(8 * scale),
            Padding = new Thickness(14 * scale, 6 * scale, 14 * scale, 6 * scale),
            Margin = new Thickness(0, 0, 0, 10 * scale),
            HorizontalAlignment = middle < 0.4 ? HorizontalAlignment.Left : middle > 0.6 ? HorizontalAlignment.Right : HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Foreground = style.TextColor == SubtitleTextColor.Yellow ? new SolidColorBrush(Color.FromRgb(0xFF, 0xE1, 0x35)) : Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.25,
            },
        };
    }
}
