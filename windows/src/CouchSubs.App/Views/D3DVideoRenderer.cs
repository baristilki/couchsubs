// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using D3D9Format = Vortice.Direct3D9.Format;
using D3D9PresentParameters = Vortice.Direct3D9.PresentParameters;
using D3D9SwapEffect = Vortice.Direct3D9.SwapEffect;
using D3D9Usage = Vortice.Direct3D9.Usage;
using D3D11QueryType = Vortice.Direct3D11.QueryType;

namespace CouchSubs.App.Views;

/// <summary>
/// Fork: yayın penceresinde oyun görüntüsünü ekran kartında büyütür. Kare (BGRA) D3D11 dokusuna yüklenir, D3D11 video
/// işlemcisi pencerenin piksel boyuna büyütür; NVIDIA kartta RTX Video Super Resolution (yapay zekâ ile büyütme)
/// açılır. Sonuç D3D9Ex ile paylaşılan bir yüzeye yazılır ve WPF'te <see cref="D3DImage"/> olarak gösterilir:
/// altyazı ve kutular eskisi gibi görüntünün üstünde kalır. Kurulamazsa (eski kart, sürücü) <see cref="Create"/> null
/// döner ve pencere eski yolla (WriteableBitmap) çizer.
/// </summary>
public sealed class D3DVideoRenderer : IDisposable
{
    /// <summary>NVIDIA video işlemcisi uzantısı (RTX Video Super Resolution); Chrome ve mpv'nin kullandığı kimlik.</summary>
    private static readonly Guid NvidiaPostProcessing = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");

    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaStreamExtension
    {
        public uint Version;
        public uint Method;
        public uint Enable;
    }

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly IDirect3D9Ex d3d9;
    private readonly IDirect3DDevice9Ex device9;
    private readonly ID3D11Query done;

    private ID3D11Texture2D? input;
    private ID3D11VideoProcessorInputView? inputView;
    private int inputWidth, inputHeight;

    private ID3D11Texture2D? output;
    private ID3D11VideoProcessorOutputView? outputView;
    private IDirect3DTexture9? sharedTexture;
    private IDirect3DSurface9? sharedSurface;
    private int outputWidth, outputHeight;

    private ID3D11VideoProcessorEnumerator? enumerator;
    private ID3D11VideoProcessor? processor;
    private bool superResolution;

    public D3DImage Image { get; } = new();

    /// <summary>Son karede NVIDIA büyütmesi istendi mi (sürücü uzantıyı kabul etti mi).</summary>
    public bool SuperResolutionActive { get; private set; }

    private D3DVideoRenderer(ID3D11Device device, ID3D11DeviceContext context, ID3D11VideoDevice videoDevice,
                             ID3D11VideoContext videoContext, IDirect3D9Ex d3d9, IDirect3DDevice9Ex device9)
    {
        this.device = device;
        this.context = context;
        this.videoDevice = videoDevice;
        this.videoContext = videoContext;
        this.d3d9 = d3d9;
        this.device9 = device9;
        done = device.CreateQuery(new QueryDescription(D3D11QueryType.Event));
    }

    /// <summary>Ekran kartında video işlemcisi kurulabilirse çizici; kurulamazsa null (hata günlüğe yazılır).</summary>
    public static D3DVideoRenderer? Create(nint window, Action<string> log)
    {
        try
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var result = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels, out var device, out var context);
            if (result.Failure || device is null || context is null)
            {
                log($"DirectX: aygıt açılamadı ({result.Code:X8})");
                return null;
            }
            var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            var videoContext = context.QueryInterface<ID3D11VideoContext>();
            var d3d9 = D3D9.Direct3DCreate9Ex();
            var present = new D3D9PresentParameters
            {
                Windowed = true,
                SwapEffect = D3D9SwapEffect.Discard,
                DeviceWindowHandle = window,
                PresentationInterval = PresentInterval.Default,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = D3D9Format.Unknown,
            };
            var device9 = d3d9.CreateDeviceEx(0, DeviceType.Hardware, window,
                CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, present);
            return new D3DVideoRenderer(device, context, videoDevice, videoContext, d3d9, device9);
        }
        catch (Exception error)
        {
            log($"DirectX: kurulamadı: {error.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Kareyi yükler ve <paramref name="targetWidth"/> x <paramref name="targetHeight"/> piksele büyütür
    /// (<paramref name="superResolution"/>: NVIDIA RTX büyütmesi). Arayüz iş parçacığında çağrılır.
    /// </summary>
    public unsafe void Render(byte[] bgra, int width, int height, int targetWidth, int targetHeight, bool superResolution)
    {
        if (width <= 0 || height <= 0 || targetWidth <= 0 || targetHeight <= 0) return;
        if (!Image.IsFrontBufferAvailable) return;
        EnsureInput(width, height);
        EnsureOutput(targetWidth, targetHeight);
        EnsureProcessor(superResolution);
        fixed (byte* pixels = bgra)
            context.UpdateSubresource(input!, 0, null, (nint)pixels, (uint)(width * 4), (uint)(width * height * 4));

        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView! };
        videoContext.VideoProcessorBlt(processor!, outputView!, 0, 1, [stream]);
        // Paylaşılan yüzey WPF'in aygıtında okunmadan önce çizim bitmiş olmalı.
        context.End(done);
        context.Flush();
        var spins = 0;
        while (context.GetData(done, IntPtr.Zero, 0, AsyncGetDataFlags.None) != Result.Ok && spins++ < 200_000) Thread.SpinWait(20);

        Image.Lock();
        try
        {
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, sharedSurface!.NativePointer);
            Image.AddDirtyRect(new Int32Rect(0, 0, outputWidth, outputHeight));
        }
        finally
        {
            Image.Unlock();
        }
    }

    private void EnsureInput(int width, int height)
    {
        if (input is not null && width == inputWidth && height == inputHeight) return;
        inputView?.Dispose();
        input?.Dispose();
        input = device.CreateTexture2D(new Texture2DDescription(Vortice.DXGI.Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget));
        inputWidth = width;
        inputHeight = height;
        ResetProcessor();
    }

    private void EnsureOutput(int width, int height)
    {
        if (output is not null && width == outputWidth && height == outputHeight) return;
        outputView?.Dispose();
        output?.Dispose();
        sharedSurface?.Dispose();
        sharedTexture?.Dispose();
        // D3D9 dokusu paylaşılır, D3D11 aynı belleği açar (WPF yalnız D3D9 yüzeyi gösterebilir).
        nint shared = 0;
        sharedTexture = device9.CreateTexture((uint)width, (uint)height, 1, D3D9Usage.RenderTarget, D3D9Format.A8R8G8B8, Pool.Default, ref shared);
        sharedSurface = sharedTexture.GetSurfaceLevel(0);
        output = device.OpenSharedResource<ID3D11Texture2D>(shared);
        outputWidth = width;
        outputHeight = height;
        ResetProcessor();
    }

    private void ResetProcessor()
    {
        outputView?.Dispose();
        outputView = null;
        inputView?.Dispose();
        inputView = null;
        processor?.Dispose();
        processor = null;
        enumerator?.Dispose();
        enumerator = null;
    }

    private void EnsureProcessor(bool wantSuperResolution)
    {
        if (processor is null)
        {
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)inputWidth,
                InputHeight = (uint)inputHeight,
                OutputWidth = (uint)outputWidth,
                OutputHeight = (uint)outputHeight,
                InputFrameRate = new Vortice.DXGI.Rational(60, 1),
                OutputFrameRate = new Vortice.DXGI.Rational(60, 1),
                Usage = VideoUsage.OptimalQuality,
            };
            videoDevice.CreateVideoProcessorEnumerator(content, out enumerator).CheckError();
            videoDevice.CreateVideoProcessor(enumerator, 0, out processor).CheckError();
            videoDevice.CreateVideoProcessorInputView(input!, enumerator, new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            }, out inputView).CheckError();
            videoDevice.CreateVideoProcessorOutputView(output!, enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            }, out outputView).CheckError();
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, inputWidth, inputHeight));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, outputWidth, outputHeight));
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, outputWidth, outputHeight));
            superResolution = !wantSuperResolution; // aşağıda uzantı yeniden yazılsın
        }
        if (superResolution == wantSuperResolution) return;
        superResolution = wantSuperResolution;
        SuperResolutionActive = SetSuperResolution(wantSuperResolution);
    }

    private unsafe bool SetSuperResolution(bool on)
    {
        var data = new NvidiaStreamExtension { Version = 1, Method = 2, Enable = on ? 1u : 0u };
        try
        {
            var result = videoContext.VideoProcessorSetStreamExtension(processor!, 0, NvidiaPostProcessing,
                (uint)sizeof(NvidiaStreamExtension), (nint)(&data));
            return on && result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        ResetProcessor();
        input?.Dispose();
        output?.Dispose();
        sharedSurface?.Dispose();
        sharedTexture?.Dispose();
        done.Dispose();
        device9.Dispose();
        d3d9.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        context.Dispose();
        device.Dispose();
    }
}
