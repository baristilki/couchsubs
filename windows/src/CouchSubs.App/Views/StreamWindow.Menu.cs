// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Yayın penceresinin sağ tık menüsü: tam ekran, oyun sesi (sessiz, düzey), oyunun altyazısının örtüsü ve çevirinin
/// yeri. Menü ayrı bir açılır pencere olduğundan Discord/OBS'in pencere yakalamasına girmez; pencerenin kendisinde
/// düğme olmaz (yayında görünürdü). Koyu, yarı saydam.
/// </summary>
public sealed partial class StreamWindow
{
    private static readonly Brush MenuBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x17, 0x1B)));
    private static readonly Brush MenuBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void InitMenu()
    {
        ContextMenu = new ContextMenu
        {
            Background = MenuBackground,
            BorderBrush = MenuBorder,
            Foreground = Brushes.White,
            Padding = new Thickness(4),
        };
        // Her açılışta güncel durumla yeniden kurulur (sessiz, düzey, örtü seçimi).
        ContextMenuOpening += (_, _) => BuildMenu();
        BuildMenu();
    }

    private void BuildMenu()
    {
        var text = Localizer.Current;
        var menu = ContextMenu!;
        menu.Items.Clear();
        menu.Items.Add(Item(text[fullScreen ? "Exit full screen (Esc)" : "Full screen (F11)"], ToggleFullScreen));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(text["Mute game sound (M)"], () => controller.StreamGameMuted = !controller.StreamGameMuted,
            isChecked: controller.StreamGameMuted));
        var volume = text.Format("Game sound: %lld%%", controller.StreamGameVolume);
        var level = new MenuItem { Header = volume, Foreground = Brushes.White };
        level.Items.Add(Item(text["Louder (+)"], () =>
        {
            controller.StreamGameMuted = false;
            controller.StreamGameVolume += 10;
        }, keepOpen: true));
        level.Items.Add(Item(text["Quieter (-)"], () => controller.StreamGameVolume -= 10, keepOpen: true));
        foreach (var step in new[] { 100, 75, 50, 25 })
            level.Items.Add(Item($"{step}%", () =>
            {
                controller.StreamGameMuted = false;
                controller.StreamGameVolume = step;
            }, isChecked: !controller.StreamGameMuted && controller.StreamGameVolume == step));
        menu.Items.Add(Styled(level));
        // Fork: oyun görüntüsünün keskinliği (ekran kartında).
        var sharp = Styled(new MenuItem { Header = text["Picture sharpness"] });
        foreach (var (step, label) in new[] { (0, "Off"), (1, "Slight"), (2, "Medium"), (3, "Strong") })
            sharp.Items.Add(Item(text[label], () => controller.StreamSharpness = step, isChecked: controller.StreamSharpness == step));
        menu.Items.Add(sharp);
        menu.Items.Add(new Separator());
        var cover = Styled(new MenuItem { Header = text["Game subtitle"] });
        foreach (var (choice, label) in new[]
                 {
                     (StreamCover.Blur, "Blur it (no background)"),
                     (StreamCover.Dark, "Cover with a dark box"),
                     (StreamCover.None, "Leave it (translation above)"),
                 })
            cover.Items.Add(Item(text[label], () => controller.StreamCover = choice, isChecked: controller.StreamCover == choice));
        menu.Items.Add(cover);
        menu.Items.Add(Item(text["Reset position"], () =>
        {
            controller.StreamFreePosition = StreamLayout.DefaultFreePosition;
            controller.StreamPlacement = StreamPlacement.OnGameSubtitle;
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(text["Close the window"], Close));
    }

    /// <summary>Alt menüler de koyu ve yarı saydam açılsın.</summary>
    private static MenuItem Styled(MenuItem item)
    {
        item.Foreground = Brushes.White;
        item.Resources[SystemColors.MenuBrushKey] = MenuBackground;
        item.Resources[SystemColors.MenuTextBrushKey] = Brushes.White;
        return item;
    }

    private MenuItem Item(string header, Action action, bool? isChecked = null, bool keepOpen = false)
    {
        var item = new MenuItem
        {
            Header = header,
            Foreground = Brushes.White,
            IsCheckable = isChecked is not null,
            IsChecked = isChecked == true,
            StaysOpenOnClick = keepOpen,
        };
        item.Click += (_, _) =>
        {
            action();
            // Düzey değişince başlıktaki yüzde de güncellensin (menü açık kalıyorsa).
            if (keepOpen) Dispatcher.BeginInvoke(BuildMenuHeaderOnly);
        };
        return item;
    }

    /// <summary>Menü açıkken yalnız düzey satırının yazısı güncellenir (alt menü kapanmasın diye yeniden kurulmaz).</summary>
    private void BuildMenuHeaderOnly()
    {
        if (ContextMenu is null) return;
        var header = Localizer.Current.Format("Game sound: %lld%%", controller.StreamGameVolume);
        foreach (var item in ContextMenu.Items.OfType<MenuItem>())
            if (item.Items.Count > 0 && item.Header is string current && current.Contains('%')) item.Header = header;
    }
}
