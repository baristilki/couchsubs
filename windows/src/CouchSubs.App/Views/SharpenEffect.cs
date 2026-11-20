// SPDX-License-Identifier: AGPL-3.0-only

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace CouchSubs.App.Views;

/// <summary>
/// Fork: oyun görüntüsünü ekran kartında keskinleştiren süzgeç (Shaders/Sharpen.fx, kontrasta duyarlı). Yayın
/// penceresinde 1080p görüntü 1440p ekrana büyütülünce yumuşuyordu.
/// </summary>
public sealed class SharpenEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/CouchSubs;component/Shaders/Sharpen.ps"),
    };

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(SharpenEffect), 0);

    /// <summary>x, y: kaynak görüntünün bir pikseli (1/en, 1/boy); z: güç 0..1.</summary>
    public static readonly DependencyProperty SettingsProperty =
        DependencyProperty.Register(nameof(Settings), typeof(Point4D), typeof(SharpenEffect),
            new UIPropertyMetadata(new Point4D(1.0 / 1920, 1.0 / 1080, 0.5, 0), PixelShaderConstantCallback(0)));

    public SharpenEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SettingsProperty);
    }

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point4D Settings
    {
        get => (Point4D)GetValue(SettingsProperty);
        set => SetValue(SettingsProperty, value);
    }

    /// <summary>Kaynak görüntünün boyu ve güç (0..1).</summary>
    public void Set(int width, int height, double strength) =>
        Settings = new Point4D(1.0 / Math.Max(1, width), 1.0 / Math.Max(1, height), Math.Clamp(strength, 0, 1), 0);

    /// <summary>Menüdeki düzey: 0 kapalı, 1 hafif, 2 orta, 3 güçlü.</summary>
    public static double Strength(int level) => level switch { 1 => 0.3, 2 => 0.6, >= 3 => 1.0, _ => 0 };
}
