// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Yayın penceresinin "Oyun görüntüsü" zemini: çeviri oyunun kendi altyazısına uysun, sırıtmasın.
/// <list type="bullet">
/// <item>Yer: oyunun İngilizce altyazısının yerinde (örtüyle) ya da kullanıcının pencerede sürükleyip bıraktığı serbest
/// yerde (iki altyazı birden görünür).</item>
/// <item>Örtü: koyu kutu (TV'deki "Üstünü örtsün"; Türkçe uzunsa kutu büyür), bulanık (İngilizce yazı bulanıklaşır,
/// çeviri kutusuz gölgeli yazı; oyuna en yakın görünüm) ya da yok (çeviri İngilizcenin hemen üstünde).</item>
/// <item>Görünüm: yazı oyunun altyazısının renginde ve boyunda; satırlar alt alta bölünmez, görüntü genişliğince yan
/// yana uzar; yazı tipi seçilebilir.</item>
/// </list>
/// </summary>
public sealed partial class StreamWindow
{
    /// <summary>Örtünün İngilizce satır kutusundan taşma payı (kare oranı; TV'deki COVER_PAD).</summary>
    private const double CoverPad = 0.012;
    /// <summary>Çevirinin en çok kaplayacağı genişlik (görüntüye oranla): cümle yan yana uzar, ama çok uzunsa ekranı
    /// boydan boya kaplamasın diye (oyun altyazıları gibi) alt satıra geçer.</summary>
    private const double MaxLineWidth = 0.6;

    private readonly Border cover = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    /// <summary>Son okunan İngilizce altyazının karedeki yeri (oranlar).</summary>
    private CouchSubs.Core.TextBox? englishBox;
    private readonly GameTextSize gameSize = new();
    /// <summary>Çeviri oyun görüntüsü üstünde bir yere konuyor mu (değilse eski alt-orta düzen).</summary>
    private bool positioned;
    /// <summary>İngilizce altyazı örtülüyor mu (koyu ya da bulanık).</summary>
    private bool covering;
    /// <summary>
    /// Çevirinin dayandığı alt kenar ve yatay orta (kareye oranla). İngilizce altyazı bir satırdan ikiye çıkınca ya da
    /// OCR kutusu birkaç piksel oynayınca çeviri inip çıkıyordu; yalnız belirgin değişimde (altyazı başka yere
    /// taşınınca) güncellenir. Çeviri alttan dayanır: önceki satırlar yukarı yığılır, son satır yerinde kalır.
    /// </summary>
    private double? anchorBottom, anchorCenterX;
    private const double AnchorMoveY = 0.03, AnchorMoveX = 0.08;

    private void UpdateAnchor(CouchSubs.Core.TextBox box)
    {
        if (anchorBottom is not { } bottom || Math.Abs(box.Bottom - bottom) > AnchorMoveY) anchorBottom = box.Bottom;
        var center = (box.Left + box.Right) / 2;
        if (anchorCenterX is not { } x || Math.Abs(center - x) > AnchorMoveX) anchorCenterX = center;
    }

    /// <summary>Sürüklenirken çevirinin ortası (görüntüye oranla); bırakılınca kaydedilir.</summary>
    private (double X, double Y)? dragPosition;
    private Point dragGrab;

    private bool GameMode => controller.StreamBackground == StreamBackground.Game && (game.Visibility == Visibility.Visible || overlay);

    /// <summary>
    /// Bulanık örtü: oyun görüntüsünün aynı yerleşimli ikinci bir kopyası, yalnız örtü alanında (kırpılıp) bulanık
    /// çizilir. Aynı yerleşim olduğundan bulanık parça hep tam altındaki sahnenin parçasıdır (VisualBrush ile parça
    /// kayıyor, yazının üstünde ayrı bir bulanık kutu görünüyordu).
    /// </summary>
    private readonly Image blurred = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

    /// <summary>Oyun görüntüsü zemininde ek alan çevirileri (telefon mesajı, görev paneli…) kendi alanlarının üstünde,
    /// İngilizcesini örterek (TV'deki gibi); düz zeminde eskisi gibi üstte sıralı.</summary>
    private readonly Grid regionLayer = new() { IsHitTestVisible = false };

    private void InitGameLayout()
    {
        RenderOptions.SetBitmapScalingMode(blurred, BitmapScalingMode.Linear);
        root.Children.Insert(root.Children.IndexOf(game) + 1, blurred);
        root.Children.Insert(root.Children.IndexOf(linesHost), regionLayer);
        linesPanel.MouseLeftButtonDown += OnDragStart;
        linesPanel.MouseMove += OnDragMove;
        linesPanel.MouseLeftButtonUp += OnDragEnd;
        linesPanel.LostMouseCapture += (_, _) => FinishDrag();
    }

    /// <summary>Oyun görüntüsü zemininde düzen kipini seçer; kip değişince kutular yeniden kurulur.</summary>
    private void UpdateGameLayoutMode(int lineCount)
    {
        var free = GameMode && controller.StreamPlacement == StreamPlacement.Free;
        var onGame = GameMode && !free && englishBox is not null;
        var place = lineCount > 0 && (free || onGame);
        var cov = place && onGame && controller.StreamCover != StreamCover.None;
        linesPanel.Cursor = GameMode ? Cursors.SizeAll : null;
        if (place == positioned && cov == covering) return;
        positioned = place;
        covering = cov;
        boxes.Clear();
        linesPanel.HorizontalAlignment = place ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        linesPanel.VerticalAlignment = place ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        linesPanel.Margin = new Thickness(0);
        linesHost.Padding = place ? new Thickness(0) : new Thickness(24 * Scale, 0, 24 * Scale, linesHost.ActualHeight * 0.07);
    }

    /// <summary>Oyun görüntüsünün penceredeki dikdörtgeni (Uniform ölçekte kenarlarda siyah pay kalabilir).</summary>
    private Rect PictureRect()
    {
        double frameWidth = gameBitmap?.PixelWidth ?? 1920, frameHeight = gameBitmap?.PixelHeight ?? 1080;
        double width = root.ActualWidth, height = root.ActualHeight;
        if (width <= 0 || height <= 0) return new Rect(0, 0, 1280, 720);
        var scale = Math.Min(width / frameWidth, height / frameHeight);
        double w = frameWidth * scale, h = frameHeight * scale;
        return new Rect((width - w) / 2, (height - h) / 2, w, h);
    }

    private static Rect ToPicture(CouchSubs.Core.TextBox box, Rect picture, double pad) => new(
        new Point(picture.X + ((box.Left - pad) * picture.Width), picture.Y + ((box.Top - pad) * picture.Height)),
        new Point(picture.X + ((box.Right + pad) * picture.Width), picture.Y + ((box.Bottom + pad) * picture.Height)));

    /// <summary>Çeviriyi yerine koyar ve gerekiyorsa örtüyü yerleştirir (kutuların ölçüsü bilindikten sonra).</summary>
    private void PositionGameSubtitle()
    {
        if (!positioned || linesPanel.Children.Count == 0)
        {
            HideCover();
            return;
        }
        var picture = PictureRect();
        // Ölçü kenar boşluğu olmadan alınır: DesiredSize Margin'i de içerir; önceki yerleştirmenin boşluğu ölçüye
        // girince her yenilemede yer değişiyor, çeviri bir yukarı bir aşağı gidiyordu.
        linesPanel.Margin = new Thickness(0);
        linesPanel.Measure(new Size(picture.Width, double.PositiveInfinity));
        var size = linesPanel.DesiredSize;
        Rect? english = null;
        double centerX, top;
        if (controller.StreamPlacement == StreamPlacement.Free || englishBox is not { } box)
        {
            var (fx, fy) = dragPosition ?? controller.StreamFreePosition;
            centerX = picture.X + (fx * picture.Width);
            top = picture.Y + (fy * picture.Height) - (size.Height / 2);
        }
        else
        {
            var rect = ToPicture(box, picture, CoverPad);
            english = rect;
            centerX = picture.X + ((anchorCenterX ?? ((box.Left + box.Right) / 2)) * picture.Width);
            // Örtü yoksa çeviri İngilizcenin hemen üstünde; varsa alt kenarı İngilizce altyazının (kararlı) alt kenarında.
            top = controller.StreamCover == StreamCover.None
                ? rect.Top - size.Height - (6 * Scale)
                : picture.Y + (((anchorBottom ?? box.Bottom) + (CoverPad / 2)) * picture.Height) - size.Height;
        }
        var left = Math.Clamp(centerX - (size.Width / 2), picture.Left, Math.Max(picture.Left, picture.Right - size.Width));
        top = Math.Clamp(top, picture.Top, Math.Max(picture.Top, picture.Bottom - size.Height));
        linesPanel.Margin = new Thickness(left, top, 0, 0);

        if (!covering || english is not { } covered)
        {
            HideCover();
            return;
        }
        // Örtü İngilizce satırı ve çeviriyi birlikte kapsar (tek kutu).
        var area = Rect.Union(covered, new Rect(left, top, size.Width, size.Height));
        if (controller.Settings.Style.BackgroundShape == SubtitleBackgroundShape.Band)
            area = new Rect(picture.Left, area.Top, picture.Width, area.Height);
        area.Intersect(picture);
        if (area.IsEmpty)
        {
            HideCover();
            return;
        }
        if (controller.StreamCover == StreamCover.Blur) ApplyBlurCover(area);
        else ApplyDarkCover();
        cover.Margin = new Thickness(area.Left, area.Top, 0, 0);
        cover.Width = area.Width;
        cover.Height = area.Height;
        cover.Visibility = Visibility.Visible;
    }

    private void ApplyDarkCover()
    {
        var style = controller.Settings.Style;
        // Örtü İngilizceyi gizlemeli: koyuluk ayarı düşük olsa da en az %90.
        var opacity = Math.Max(0.9, Math.Clamp(style.Background / 100.0, 0, 1));
        cover.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0x0B, 0x0B, 0x0D));
        cover.CornerRadius = new CornerRadius(style.BackgroundShape == SubtitleBackgroundShape.Band ? 0 : 6 * Scale);
        blurred.Visibility = Visibility.Collapsed;
    }

    private void HideCover()
    {
        cover.Visibility = Visibility.Collapsed;
        blurred.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Bulanık örtü: alttaki sahnenin aynı parçası güçlü bulanıklıkla çizilir (yazı okunmaz, sahne sürer), üstüne çok
    /// hafif bir gölge konur ki çeviri açık sahnede de okunsun; kutu sırıtmaz.
    /// </summary>
    private void ApplyBlurCover(Rect area)
    {
        var radius = 8 * Scale;
        blurred.Source = gameBitmap;
        blurred.Clip = new RectangleGeometry(area, radius, radius);
        blurred.Effect = new BlurEffect { Radius = Math.Max(12, area.Height * 0.45), KernelType = KernelType.Gaussian };
        blurred.Visibility = Visibility.Visible;
        cover.Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
        cover.CornerRadius = new CornerRadius(radius);
    }

    // --- Görünüm ---

    /// <summary>Bu oturumda iki noktayla görülmüş konuşan adları: OCR iki noktayı kaçırınca ("Peter İki kelime…") ad
    /// yine tanınsın.</summary>
    private readonly HashSet<string> seenSpeakers = new(StringComparer.OrdinalIgnoreCase);

    private static (string Speaker, string Line)? SplitSpeaker(string text, IEnumerable<string>? known = null) =>
        StreamSubtitleText.SplitSpeaker(text, known);

    private (string Speaker, string Line)? SpeakerOf(string text)
    {
        var split = SplitSpeaker(text, seenSpeakers);
        if (split is { } found) seenSpeakers.Add(found.Speaker);
        return split;
    }

    private static List<string> Lines(string text) => StreamSubtitleText.Lines(text);

    /// <summary>Çevirinin yazı boyu: oyunun altyazısınınkine uydurulduysa onun satır yüksekliğinden, yoksa ayardan.</summary>
    private double GameFontSize(SubtitleStyle style)
    {
        var fallback = OverlayPlacement.FontSize(style.FontSize) * Scale;
        if (!controller.StreamMatchGameSize || gameSize.Current is not { } line) return fallback;
        // OCR kutusu harflerin üstünden altına kadardır; yazı boyu bunun ~%92'si. Uç değerler ayarın yarısı ile iki katı.
        var size = line * PictureRect().Height * 0.92;
        return Math.Clamp(size, fallback * 0.5, fallback * 2);
    }

    private Border MakeGameBox(SubtitleStack.Line line)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var current = line.Role == SubtitleStack.Role.Current;
        var size = Math.Max(1, GameFontSize(style) * (current ? 1 : 0.82));
        // Zemin yalnız koyu örtü seçiliyken ve örtülmüyorken (serbest yer) olur; bulanık ve "yok"ta kutusuz yazı.
        var boxOpacity = Math.Clamp(style.Background / 100.0, 0, 1) * 0.8;
        var boxed = controller.StreamCover == StreamCover.Dark && !covering && boxOpacity > 0.02;
        var text = new TextBlock
        {
            FontFamily = new FontFamily(controller.StreamFont),
            FontSize = size,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Medium,
            Foreground = new SolidColorBrush(GameTextBrushColor(style)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = line.Paragraphs ? TextAlignment.Left : TextAlignment.Center,
            LineHeight = size * 1.22,
            // Kutusuzken oyun altyazıları gibi belirgin gölge (açık sahnede de okunsun); kutuluyken hafif.
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = (boxed ? 1 : 1.4) * scale,
                BlurRadius = (boxed ? 4 : 6) * scale,
                Opacity = boxed ? 0.55 : 1,
            },
        };
        if (line.Paragraphs)
        {
            text.Text = line.Text;
        }
        else
        {
            // Her konuşan kendi satırında; ad kendi renginde (oyundaki gibi), cümleden biraz küçük ki ayırt edilsin.
            var first = true;
            foreach (var part in Lines(line.Text))
            {
                if (!first) text.Inlines.Add(new System.Windows.Documents.LineBreak());
                first = false;
                if (SpeakerOf(part) is { } split)
                {
                    text.Inlines.Add(new System.Windows.Documents.Run(split.Speaker + ": ")
                    {
                        Foreground = new SolidColorBrush(SpeakerBrushColor(style, split.Speaker)),
                        FontWeight = controller.StreamMatchGameColor ? text.FontWeight : FontWeights.Normal,
                        FontSize = size * 0.85,
                    });
                    text.Inlines.Add(new System.Windows.Documents.Run(split.Line));
                }
                else
                {
                    text.Inlines.Add(new System.Windows.Documents.Run(part));
                }
            }
        }
        return new Border
        {
            Background = boxed ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(boxOpacity * 255), 0x0B, 0x0B, 0x0D)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(6 * scale),
            Padding = current ? new Thickness(16 * scale, 5 * scale, 16 * scale, 6 * scale) : new Thickness(12 * scale, 3 * scale, 12 * scale, 4 * scale),
            Margin = new Thickness(0, 4 * scale, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = Math.Max(200, PictureRect().Width * MaxLineWidth),
            Opacity = line.Role switch { SubtitleStack.Role.Current => 1.0, SubtitleStack.Role.Previous => 0.7, _ => 0.5 },
            Child = text,
        };
    }

    /// <summary>
    /// Ek alanın çevirisi alanın kendi yerinde: kutu İngilizce yazının kapladığı yeri örter (en az o kadar), Türkçe
    /// uzunsa aşağı uzar; yazı alanın satır boyuna yakın.
    /// </summary>
    private Border MakePlacedRegion(string text, CouchSubs.Core.TextBox area)
    {
        var style = controller.Settings.Style;
        var picture = PictureRect();
        var rect = ToPicture(area, picture, 0.004);
        var size = Math.Clamp(GameFontSize(style) * 0.6, 12, Math.Max(12, rect.Height * 0.9));
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Math.Max(0, rect.Left), Math.Max(0, rect.Top), 0, 0),
            MinWidth = rect.Width,
            MinHeight = rect.Height,
            MaxWidth = Math.Max(rect.Width, picture.Width * 0.35),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x14)),
            CornerRadius = new CornerRadius(6 * Scale),
            Padding = new Thickness(10 * Scale, 6 * Scale, 10 * Scale, 6 * Scale),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(controller.StreamFont),
                FontSize = size,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(GameTextBrushColor(style)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.25,
            },
        };
    }

    /// <summary>
    /// Fork: ek alan çevirisi TV'deki gibi (tv-webos/app.js showRegion ve placeItems ile aynı kurallar). Ölçüler TV'de
    /// 1080 piksellik ekran içindir; burada oyun görüntüsünün yüksekliğine göre büyütülür.
    /// <paramref name="block"/> değilse kutu İngilizce yazının ekran kenarına yakın yanına yaslanır (sağ yarıdaysa
    /// sağına, alt yarıdaysa altına), yazı boyu satır yüksekliğinin 0,9'u (18-44); blok ise kendi yazısının tam
    /// üstünde, ortalı ve satır yüksekliğinin 0,95'i. Tuş simgeleri çevirinin içinde.
    /// </summary>
    private Border MakeTvRegion(string text, CouchSubs.Core.TextBox area, double? line, IReadOnlyList<CouchSubs.Core.IconPlacement>? icons,
                                bool block = false)
    {
        const double pad = 0.004;
        var picture = PictureRect();
        var k = picture.Height / 1080.0;
        var lineHeight = (line is > 0 ? line.Value : (block ? area.Bottom - area.Top : 30.0 / 1080)) * picture.Height;
        var size = block
            ? Math.Clamp(lineHeight * 0.95, 14 * k, 96 * k)
            : Math.Clamp(lineHeight * 0.9, 18 * k, 44 * k);
        var textBlock = new TextBlock
        {
            FontSize = Math.Max(10, size),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = block ? TextAlignment.Center : TextAlignment.Left,
            LineHeight = size * (block ? 1.15 : 1.2),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Opacity = 0.9, Color = Colors.Black },
        };
        FillWithIcons(textBlock, text, icons, size);
        var rect = ToPicture(area, picture, pad);
        var boxWidth = rect.Width;
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(block ? (byte)0xF7 : (byte)0xE6, 0, 0, 0)),
            CornerRadius = new CornerRadius(6 * k),
            Padding = block ? new Thickness(6 * k, 2 * k, 6 * k, 2 * k) : new Thickness(10 * k, 4 * k, 10 * k, 4 * k),
            Child = textBlock,
        };
        if (block)
        {
            // Fork: tek satırlık İngilizce (menü satırı) tek satır kalır: Türkçe uzunsa kutu ekranın içine doğru
            // genişler (sağdaki menüde sola), yine sığmazsa yazı küçülür. Sözcük ortadan bölünmez, alt satıra taşıp
            // alttaki satırın üstüne binmez. Çok satırlı yazı kendi genişliğinde kırılır, gerekirse küçülür.
            var singleLine = rect.Height < lineHeight * 1.7;
            var room = Math.Max(rect.Width, picture.Width * (singleLine ? 0.28 : 0.35));
            var padX = 12 * k;
            textBlock.TextWrapping = singleLine ? TextWrapping.NoWrap : TextWrapping.Wrap;
            textBlock.TextAlignment = TextAlignment.Center;
            var floor = Math.Max(10, 12 * k);
            for (var guard = 0; guard < 40; guard++)
            {
                textBlock.Measure(new Size(singleLine ? double.PositiveInfinity : Math.Max(1, room - padX), double.PositiveInfinity));
                var tooWide = textBlock.DesiredSize.Width + padX > room;
                var tooTall = !singleLine && textBlock.DesiredSize.Height > Math.Max(rect.Height * 1.5, lineHeight * 2.6);
                if ((!tooWide && !tooTall) || textBlock.FontSize <= floor) break;
                var smaller = Math.Max(floor, textBlock.FontSize * 0.92);
                textBlock.FontSize = smaller;
                textBlock.LineHeight = smaller * 1.15;
                foreach (var image in textBlock.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Select(c => c.Child).OfType<Image>())
                    image.Height = smaller * 1.15;
            }
            var width = Math.Max(rect.Width, Math.Min(room, textBlock.DesiredSize.Width + padX));
            border.VerticalAlignment = VerticalAlignment.Top;
            border.MinHeight = rect.Height;
            border.Width = width;
            var top = Math.Max(0, rect.Top);
            if ((area.Left + area.Right) / 2 > 0.5)
            {
                // Sağdaki menü: kutunun sağ kenarı İngilizce yazının sağında kalır (tuş simgesi açıkta).
                border.HorizontalAlignment = HorizontalAlignment.Left;
                border.Margin = new Thickness(Math.Max(picture.Left, Math.Min(picture.Right, rect.Right) - width), top, 0, 0);
            }
            else
            {
                border.HorizontalAlignment = HorizontalAlignment.Left;
                border.Margin = new Thickness(Math.Max(picture.Left, rect.Left), top, 0, 0);
            }
            return border;
        }
        double layerWidth = root.ActualWidth, layerHeight = root.ActualHeight;
        border.MinWidth = boxWidth;
        border.MaxWidth = Math.Max(boxWidth, picture.Width * 0.3);
        if ((area.Left + area.Right) / 2 > 0.5)
        {
            border.HorizontalAlignment = HorizontalAlignment.Right;
            border.Margin = new Thickness(0, 0, Math.Max(0, layerWidth - Math.Min(picture.Right, rect.Right)), 0);
        }
        else
        {
            border.HorizontalAlignment = HorizontalAlignment.Left;
            border.Margin = new Thickness(Math.Max(picture.Left, rect.Left), 0, 0, 0);
        }
        var margin = border.Margin;
        if ((area.Top + area.Bottom) / 2 > 0.5)
        {
            border.VerticalAlignment = VerticalAlignment.Bottom;
            margin.Bottom = Math.Max(0, layerHeight - Math.Min(picture.Bottom, rect.Bottom));
        }
        else
        {
            border.VerticalAlignment = VerticalAlignment.Top;
            margin.Top = Math.Max(picture.Top, rect.Top);
        }
        border.Margin = margin;
        return border;
    }

    /// <summary>Çeviriyi tuş simgeleriyle yazar (simge yazı boyunda, satırın ortasında; TV'deki fillText gibi).</summary>
    private static void FillWithIcons(TextBlock target, string text, IReadOnlyList<CouchSubs.Core.IconPlacement>? icons, double size)
    {
        var list = icons?.Where(icon => icon.Image.StartsWith("data:image/", StringComparison.Ordinal)).OrderBy(icon => icon.At).ToList();
        if (list is not { Count: > 0 })
        {
            target.Text = text;
            return;
        }
        var position = 0;
        foreach (var icon in list)
        {
            var at = Math.Max(position, Math.Min(text.Length, icon.At));
            if (at > position) target.Inlines.Add(new System.Windows.Documents.Run(text[position..at]));
            if (IconImage(icon.Image) is { } image)
                target.Inlines.Add(new System.Windows.Documents.InlineUIContainer(new Image
                {
                    Source = image,
                    Height = size * 1.15,
                    Margin = new Thickness(size * 0.2, 0, size * 0.2, 0),
                }) { BaselineAlignment = BaselineAlignment.Center });
            position = at;
        }
        if (position < text.Length) target.Inlines.Add(new System.Windows.Documents.Run(text[position..]));
    }

    private static readonly Dictionary<string, BitmapImage?> iconCache = new(StringComparer.Ordinal);

    /// <summary>"data:image/png;base64,…" simgesi (önbellekli; bozuksa null).</summary>
    private static BitmapImage? IconImage(string dataUrl)
    {
        if (iconCache.TryGetValue(dataUrl, out var known)) return known;
        BitmapImage? image = null;
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma > 0)
            {
                var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
                image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new System.IO.MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
            }
        }
        catch (Exception) { image = null; }
        if (iconCache.Count > 200) iconCache.Clear();
        iconCache[dataUrl] = image;
        return image;
    }

    /// <summary>Oyun görüntüsü zemininde ek alan kutusu: altyazıyla aynı dilde, daha küçük ve yarı saydam.</summary>
    private Border MakeGameRegionBox(string text, CouchSubs.Core.TextBox? area)
    {
        var style = controller.Settings.Style;
        var scale = Scale;
        var size = Math.Max(12, GameFontSize(style) * 0.6);
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
                FontFamily = new FontFamily(controller.StreamFont),
                FontSize = size,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(GameTextBrushColor(style)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.25,
            },
        };
    }

    // --- Sürükle-bırak: oyun görüntüsü zemininde çeviri fareyle istenen yere taşınır; taşınınca yer "serbest" olur
    // (oyunun altyazısı da görünür kalır, iki altyazı birden). Ayarlar'daki "Konumu sıfırla" eski yere döndürür. ---

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!GameMode || e.ClickCount > 1) return;
        dragGrab = e.GetPosition(linesPanel);
        if (linesPanel.CaptureMouse()) e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!linesPanel.IsMouseCaptured) return;
        var picture = PictureRect();
        var at = e.GetPosition(root);
        // Tutulan nokta fareyle birlikte gider; kaydedilen, çevirinin ortası.
        var centerX = at.X - dragGrab.X + (linesPanel.ActualWidth / 2);
        var centerY = at.Y - dragGrab.Y + (linesPanel.ActualHeight / 2);
        dragPosition = (Math.Clamp((centerX - picture.X) / picture.Width, 0, 1), Math.Clamp((centerY - picture.Y) / picture.Height, 0, 1));
        if (controller.StreamPlacement != StreamPlacement.Free) controller.StreamPlacement = StreamPlacement.Free;
        else PositionGameSubtitle();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!linesPanel.IsMouseCaptured) return;
        linesPanel.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (dragPosition is not { } position) return;
        dragPosition = null;
        controller.StreamFreePosition = position;
    }
}
