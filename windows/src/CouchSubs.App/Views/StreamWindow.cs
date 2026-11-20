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
public sealed partial class StreamWindow : Window
{
    private static StreamWindow? open;

    private readonly AppController controller;
    private readonly SubtitleStack stack = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new();
    private readonly StackPanel linesPanel = new() { VerticalAlignment = VerticalAlignment.Bottom };
    /// <summary>Ek alanların (görev, telefon mesajı, bildirim) çevirileri; saydam penceredeki gibi üstte, alanın yanına yaslı.</summary>
    private readonly StackPanel regionsPanel = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<string, SubtitleEvent> regions = [];
    private readonly Border linesHost;
    private readonly Dictionary<int, Border> boxes = [];
    private double screenUntil;
    /// <summary>Oyun görüntüsü (zemin "Oyun görüntüsü"yken); altyazı katmanlarının arkasında, oranı korunarak.</summary>
    private readonly Image game = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private WriteableBitmap? gameBitmap;
    private readonly Grid root;

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

    /// <summary>
    /// Discord durumu açıkken pencerenin ikonu Discord uygulamasının ikonudur (oyunun logosu): Discord'un pencere
    /// seçiminde, görev çubuğunda ve Alt+Tab'da CouchSubs logosu görünmez. Yoksa uygulamanın kendi ikonu.
    /// </summary>
    /// <summary>Oyun görüntüsüyle (Discord yayını) başlık oyunun adı; düz zeminde sabit ad kalır, OBS'nin pencere
    /// yakalaması pencereyi "CouchSubs Yayın" adıyla bulur.</summary>
    private string CurrentTitle =>
        controller.StreamBackground == StreamBackground.Game ? controller.DiscordAppName ?? WindowTitle : WindowTitle;

    private void ApplyGameIcon()
    {
        if (controller.StreamBackground != StreamBackground.Game || controller.DiscordAppIcon is not { Length: > 0 } png)
        {
            ClearValue(IconProperty);
            return;
        }
        try
        {
            using var stream = new System.IO.MemoryStream(png);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            image.Freeze();
            Icon = image;
        }
        catch (Exception error) when (error is NotSupportedException or System.IO.IOException or ArgumentException)
        {
            ClearValue(IconProperty);
        }
    }

    private StreamWindow(AppController controller, bool overlay = false)
    {
        this.controller = controller;
        this.overlay = overlay;
        // Discord durumu açıkken başlık Discord uygulamasının adıdır: Discord pencere yayınını başlıkla gösterir
        // ("Yayında: Grand Theft Auto VI"). Kapalıysa sabit ad; yayın programı pencereyi bu adla bulur.
        Title = CurrentTitle;
        ApplyGameIcon();
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
        ApplySharpness();
        var layers = new Grid();
        layers.Children.Add(regionsPanel);
        layers.Children.Add(linesPanel);
        linesHost = new Border { Child = layers };
        // Görüntü kenar boşluğu (altyazı payı) olmadan tüm pencereyi kaplar; altyazı katmanı üstündedir. Örtü
        // (oyunun İngilizce altyazısını kapatan kutu) ikisinin arasındadır.
        root = new Grid();
        root.Children.Add(game);
        root.Children.Add(cover);
        root.Children.Add(linesHost);
        Content = root;
        InitGameLayout();
        if (!overlay) InitMenu();
        // Katman ayarları en sonda: yukarıdaki pencere ayarlarının (ortala, boyutlandırılabilir) üstüne yazılır.
        if (overlay) InitOverlay();
        ApplyBackground();
        linesHost.SizeChanged += (_, _) =>
        {
            linesHost.Padding = new Thickness(24 * Scale, 0, 24 * Scale, positioned ? 0 : linesHost.ActualHeight * 0.07);
            regionsPanel.Margin = new Thickness(0, 16 * Scale, 0, 0);
            boxes.Clear();
            Refresh();
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        staleCheck.Tick += (_, _) =>
        {
            DrawLatestFrame();
        };
        KeyDown += OnKey;
        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) ToggleFullScreen();
        };
        controller.StreamEvent += OnEvent;
        controller.StreamHidden += OnHidden;
        controller.PropertyChanged += OnChanged;
        if (!overlay)
        {
            controller.GameVideoWanted = true;
            UpdateGameRendering();
        }
        Closed += (_, _) =>
        {
            controller.StreamEvent -= OnEvent;
            controller.StreamHidden -= OnHidden;
            controller.PropertyChanged -= OnChanged;
            if (overlay) return;
            StopGameMode();
            controller.GameVideoWanted = false;
            StopGameRendering();
            DropDirectX(null);
            timer.Stop();
            open = null;
        };
    }

    // --- Tam ekran: F11 ya da çift tık açar/kapatır, Esc kapatır. Kenarlıksız pencere bulunduğu ekranı kaplar;
    // başlığı değişmediği için yayın programındaki pencere yakalama sürer. ---

    private bool fullScreen;
    private WindowState savedState;
    private Rect savedBounds;

    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11 || (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Alt))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.G)
        {
            ToggleGameMode();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && fullScreen)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        // Ses: pencerede düğme olmaz (yayında görünürdü); M sessiz, + / - düzey.
        else if (e.Key == System.Windows.Input.Key.M)
        {
            controller.StreamGameMuted = !controller.StreamGameMuted;
            e.Handled = true;
        }
        else if (e.Key is System.Windows.Input.Key.OemPlus or System.Windows.Input.Key.Add)
        {
            controller.StreamGameMuted = false;
            controller.StreamGameVolume += 10;
            e.Handled = true;
        }
        else if (e.Key is System.Windows.Input.Key.OemMinus or System.Windows.Input.Key.Subtract)
        {
            controller.StreamGameVolume -= 10;
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        if (!fullScreen)
        {
            savedState = WindowState;
            savedBounds = new Rect(Left, Top, Width, Height);
            // Önce normal durum: büyütülmüş pencere kenarlık kalkınca görev çubuğunun altında kalırdı.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            fullScreen = true;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Left = savedBounds.Left;
            Top = savedBounds.Top;
            Width = savedBounds.Width;
            Height = savedBounds.Height;
            WindowState = savedState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            fullScreen = false;
        }
    }

    private void ApplyBackground()
    {
        if (overlay)
        {
            // Katman: yalnız altyazı ve kutular; görüntü altındaki oyun penceresinde.
            Background = Brushes.Transparent;
            linesHost.Background = Brushes.Transparent;
            return;
        }
        var (red, green, blue) = controller.StreamBackground.Rgb();
        // Oyun görüntüsü varken zemin görüntünün kendisidir; kenar payları (16:9 dışı pencerede) siyah kalır.
        Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        linesHost.Background = Brushes.Transparent;
    }

    /// <summary>
    /// Oyun görüntüsü açıkken her yeni kare gelince çizilir (ekran yenilemesine bağlanınca 60 kare/sn akışın ancak
    /// ~45'i gösteriliyordu: yakın gelen iki kare aynı yenilemeye düşüp biri atlanıyordu). Arayüz geride kalırsa
    /// bekleyen tek bir çizim isteği tutulur, en yeni kare çizilir.
    /// </summary>
    private void UpdateGameRendering()
    {
        if (overlay) return;
        if (controller.GameVideoActive) StartGameRendering();
        else StopGameRendering();
    }

    private void StartGameRendering()
    {
        if (rendering) return;
        rendering = true;
        // Fork: kareler ekran yenilemesinde, akışın kendi aralığıyla eşit arayla çizilir (VideoFrameBuffer.ReadPaced).
        // Eskiden her kare geldiği anda çiziliyordu; ağdan düzensiz gelen kareler görüntüyü sıçratıyordu.
        lastRendering = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        // Akış durunca (FrameWritten gelmez) eski kare donup kalmasın: seyrek bir yoklama bunu fark eder.
        staleCheck.Start();
    }

    private void StopGameRendering()
    {
        if (rendering) CompositionTarget.Rendering -= OnRendering;
        staleCheck.Stop();
        // Başka oyuna geçilmiş olabilir: renk yeniden okunur.
        rendering = false;
        game.Visibility = Visibility.Collapsed;
        game.Source = null;
        gameBitmap = null;
        gameVersion = -1;
    }

    private TimeSpan lastRendering;
    /// <summary>Ekranın yenileme aralığı (ms), Rendering olaylarından öğrenilir; 60 Hz ile başlar.</summary>
    private double refreshMs = 1000.0 / 60;
    private int renderTicks;

    /// <summary>Her ekran yenilemesinde (arayüz iş parçacığı): sırası gelen kare varsa çizilir.</summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (!rendering) return;
        if (e is RenderingEventArgs args)
        {
            // Aynı yenileme için olay iki kez gelebilir; yalnız ilki sayılır.
            if (args.RenderingTime == lastRendering) return;
            if (lastRendering != TimeSpan.Zero)
            {
                var gap = (args.RenderingTime - lastRendering).TotalMilliseconds;
                if (gap is > 1 and < 50) refreshMs = (refreshMs * 0.9) + (gap * 0.1);
            }
            lastRendering = args.RenderingTime;
        }
        renderTicks++;
        DrawLatestFrame();
    }

    private readonly DispatcherTimer staleCheck = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>Konuşan adının yazı rengi: oyunda o karakterin adı ayrı renkteyse o renk, değilse cümlenin rengi;
    /// renk uyumu kapalıysa sönük gri. Renkler çeviri hattında okunur (AppController.GameColors); TV de aynısını kullanır.</summary>
    private Color SpeakerBrushColor(SubtitleStyle style, string speaker)
    {
        if (controller.StreamMatchGameColor && controller.GameColors.Speaker(speaker) is { } c) return Color.FromRgb(c.R, c.G, c.B);
        // Fork: ad her zaman cümleden ayrı renkte (TV ile aynı): yazı sarıysa açık mavi, değilse yumuşak sarı.
        return style.TextColor == SubtitleTextColor.Yellow ? Color.FromRgb(0x8E, 0xC9, 0xFF) : Color.FromRgb(0xF2, 0xC3, 0x5B);
    }

    /// <summary>Çevirinin yazı rengi: kullanıcı sarı seçtiyse sarı, değilse (açıksa) oyunun rengi, yoksa kırık beyaz.</summary>
    private Color GameTextBrushColor(SubtitleStyle style)
    {
        if (style.TextColor == SubtitleTextColor.Yellow) return Color.FromRgb(0xFF, 0xE1, 0x35);
        var c = controller.StreamMatchGameColor ? controller.GameColors.Text : GameTextColor.White;
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private SharpenEffect? sharpen;

    /// <summary>
    /// Fork: keskinlik düzeyi değişti: süzgeç takılır ya da kaldırılır (kapalıyken hiç iş yapılmaz). Süzgeç
    /// yüklenemezse (eski ekran kartı sürücüsü) görüntü süzgeçsiz gösterilir, pencere kapanmaz.
    /// </summary>
    private void ApplySharpness()
    {
        var level = controller.StreamSharpness;
        try
        {
            if (level == 0)
            {
                game.Effect = null;
                return;
            }
            sharpen ??= new SharpenEffect();
            sharpen.Set(gameBitmap?.PixelWidth ?? 1920, gameBitmap?.PixelHeight ?? 1080, SharpenEffect.Strength(level));
            game.Effect = sharpen;
        }
        catch (Exception error)
        {
            game.Effect = null;
            CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: keskinleştirme açılamadı: {error.GetBaseException().Message}");
        }
    }

    private void DrawLatestFrame()
    {
        if (!controller.GameVideo.HasFrame)
        {
            // Akış durdu ya da henüz kare gelmedi: eski kare donup kalmasın.
            if (game.Visibility == Visibility.Visible) game.Visibility = Visibility.Collapsed;
            gameVersion = -1;
            return;
        }
        var renderer = DirectXRenderer();
        var newer = controller.GameVideo.ReadPaced(refreshMs, (pixels, width, height) =>
        {
            if (gameBitmap is null || gameBitmap.PixelWidth != width || gameBitmap.PixelHeight != height)
            {
                gameBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                if (renderer is null) game.Source = gameBitmap;
                sharpen?.Set(width, height, SharpenEffect.Strength(controller.StreamSharpness));
                if (blurred.Visibility == Visibility.Visible) blurred.Source = gameBitmap;
            }
            // Fork: DirectX motorunda kare ekran kartında büyütülür; WriteableBitmap yalnız bulanık örtü için yazılır.
            if (renderer is not null && TryRenderDirectX(renderer, pixels, width, height))
            {
                if (NeedsBitmapForBlur) gameBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
                return;
            }
            if (game.Source != gameBitmap) game.Source = gameBitmap;
            gameBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        }, controller.StreamLowLatency);
        if (newer is not { } version) return;
        gameVersion = version;
        waitTotal += controller.GameVideo.LastWaitMs;
        if (game.Visibility != Visibility.Visible) game.Visibility = Visibility.Visible;
        CountShownFrame();
    }

    private D3DVideoRenderer? directX;
    /// <summary>DirectX motoru bu pencerede kurulamadı ya da çökmüştü: eski yol kullanılır.</summary>
    private bool directXFailed;

    /// <summary>Fork: DirectX motoru açıksa (yayın penceresi menüsü) çizici; kapalıysa ya da kurulamadıysa null.</summary>
    private D3DVideoRenderer? DirectXRenderer()
    {
        if (!controller.StreamDirectX || directXFailed)
        {
            if (directX is not null) DropDirectX(null);
            return null;
        }
        if (directX is not null) return directX;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        directX = D3DVideoRenderer.Create(handle, line => CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: {line}"));
        if (directX is null) directXFailed = true;
        else CouchSubs.RemotePlay.RemotePlayLog.Note("yayın penceresi: DirectX görüntü motoru açıldı");
        return directX;
    }

    private bool TryRenderDirectX(D3DVideoRenderer renderer, byte[] pixels, int width, int height)
    {
        try
        {
            var picture = PictureRect();
            var dpi = VisualTreeHelper.GetDpi(this);
            var targetWidth = (int)Math.Round(picture.Width * dpi.DpiScaleX);
            var targetHeight = (int)Math.Round(picture.Height * dpi.DpiScaleY);
            // Ekrandan büyük ya da kaynaktan küçük hedef gerekmez.
            if (targetWidth < width || targetHeight < height) (targetWidth, targetHeight) = (width, height);
            renderer.Render(pixels, width, height, targetWidth, targetHeight, controller.StreamSuperResolution);
            if (game.Source != renderer.Image)
            {
                game.Source = renderer.Image;
                sharpen?.Set(targetWidth, targetHeight, SharpenEffect.Strength(controller.StreamSharpness));
            }
            return true;
        }
        catch (Exception error)
        {
            DropDirectX(error);
            return false;
        }
    }

    private void DropDirectX(Exception? error)
    {
        if (error is not null)
        {
            directXFailed = true;
            CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: DirectX motoru kapandı, eski yola dönüldü: {error.GetBaseException().Message}");
        }
        if (directX is not null && game.Source == directX.Image) game.Source = gameBitmap;
        directX?.Dispose();
        directX = null;
    }

    private int shownFrames;
    /// <summary>Fork: gösterilen karelerin kuyrukta bekleme süreleri toplamı (günlükte ortalaması).</summary>
    private double waitTotal;
    private long shownSince = Environment.TickCount64;
    private long versionSince = -1;

    /// <summary>
    /// Takılma şikâyetinde bakılabilsin: 10 sn'de bir PS5'ten gelen ve pencerede gösterilen kare hızı günlüğe yazılır
    /// (ikisi farklıysa kareler pencerede, aynıysa akışın kendisinde azdır).
    /// </summary>
    private void CountShownFrame()
    {
        shownFrames++;
        if (versionSince < 0) versionSince = gameVersion;
        var elapsed = Environment.TickCount64 - shownSince;
        if (elapsed < 10_000) return;
        var received = (gameVersion - versionSince) * 1000.0 / elapsed;
        CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: {received:0.0} kare/sn geldi, {shownFrames * 1000.0 / elapsed:0.0} gösterildi, " +
                                                $"ekran {renderTicks * 1000.0 / elapsed:0} Hz (eşit aralıklı)" +
                                                (directX is { } d ? $", DirectX{(d.SuperResolutionActive ? " + RTX büyütme" : "")}" : "") +
                                                $", kuyrukta {(shownFrames > 0 ? waitTotal / shownFrames : 0):0.0} ms" +
                                                (controller.StreamLowLatency ? " (düşük gecikme)" : " (akıcı)"));
        waitTotal = 0;
        shownFrames = 0;
        renderTicks = 0;
        shownSince = Environment.TickCount64;
        versionSince = gameVersion;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.GameVideoActive)) UpdateGameRendering();
        if (e.PropertyName == nameof(AppController.StreamSharpness)) ApplySharpness();
        if (e.PropertyName == nameof(AppController.StreamDirectX)) directXFailed = false;
        if (e.PropertyName == nameof(AppController.GameColors))
        {
            boxes.Clear();
            Refresh();
        }
        if (e.PropertyName is nameof(AppController.DiscordAppName) or nameof(AppController.StreamBackground)) Title = CurrentTitle;
        if (e.PropertyName is nameof(AppController.DiscordAppIcon) or nameof(AppController.StreamBackground)) ApplyGameIcon();
        if (e.PropertyName is nameof(AppController.StreamCover) or nameof(AppController.StreamMatchGameColor)
            or nameof(AppController.StreamPlacement) or nameof(AppController.StreamMatchGameSize) or nameof(AppController.StreamFont)
            or nameof(AppController.StreamFreePosition))
        {
            boxes.Clear();
            Refresh();
        }
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
                if (e.Box is { } english)
                {
                    englishBox = english;
                    gameSize.Offer(english);
                    UpdateAnchor(english);
                }
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
                if (e.Text.Length > 0) regions[e.Id] = e;
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
        // Oyun görüntüsü zemininde çeviri oyunun altyazısının yerine ya da serbest yere konur (StreamWindow.Game.cs).
        UpdateGameLayoutMode(lines.Count);
        regionsPanel.Children.Clear();
        regionLayer.Children.Clear();
        var onPicture = controller.StreamBackground == StreamBackground.Game && controller.GameVideoActive;
        foreach (var region in regions.Values.OrderBy(region => region.Box?.Top ?? 0))
        {
            var (text, area) = (region.Text, region.Box);
            // Fork: oyun görüntüsü üstünde TV'nin kurallarıyla: ayrı duran yazılar (GTA 6 etkileşim menüsü satırları)
            // her biri kendi yerinde; tek kutu İngilizce yazının ekran kenarına yakın yanına yaslı, yazı boyu satırın
            // yüksekliğinden, tuş simgeleri çevirinin içinde.
            if (onPicture && region.Blocks is { Count: > 0 } blocks)
            {
                foreach (var block in blocks) regionLayer.Children.Add(MakeTvRegion(block.Text, block.Box, block.LineHeight, block.Icons, block: true));
                continue;
            }
            if (onPicture && area is { } placed)
            {
                regionLayer.Children.Add(MakeTvRegion(text, placed, region.LineHeight, region.Icons));
                continue;
            }
            else regionsPanel.Children.Add(MakeRegionBox(text, area));
        }
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
        // Fork: ek alan kutularının arkası bulanık (yerleştikten sonra).
        QueueBlurAreas();
        // Yerleştirme kutuların ölçüsünü bilmeli: yerleşimden sonra yapılır.
        if (positioned) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionGameSubtitle);
        else HideCover();
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
