// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using CouchSubs.Core;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CouchSubs.App.Views;

/// <summary>
/// Fork: oyun modu. PS5 görüntüsünü WPF'siz, tam ekran bir pencerede en düşük gecikmeyle gösterir: kendi iş
/// parçacığında kare gelir gelmez D3D11 video işlemcisiyle (NVIDIA RTX büyütmesiyle) ekran boyuna büyütür ve
/// DXGI "flip" zinciriyle hemen ekrana basar (WPF ve masaüstü birleştirmesinin 1-2 karesi yok; G-SYNC uyumlu).
/// Altyazı ve kutular üstte saydam yayın penceresi katmanıyla gösterilir (<see cref="StreamWindow"/>, katman modu).
/// Esc ya da F11 kapatır.
/// </summary>
public sealed class GameModeWindow : IDisposable
{
    private readonly VideoFrameBuffer frames;
    private readonly Func<bool> superResolution;
    private readonly Action<string> log;
    private readonly Action closed;
    private readonly RawRect bounds;
    private readonly AutoResetEvent frameReady = new(false);
    private readonly Thread thread;
    private volatile bool stopping;
    /// <summary>Arkası bulanıklaşacak kutular (piksel; altyazı ve ek alan kutuları), katman penceresinden.</summary>
    private volatile BlurArea[] blurAreas = [];
    private nint window;
    private WndProc? procedure;

    private GameModeWindow(VideoFrameBuffer frames, RawRect bounds, Func<bool> superResolution, Action<string> log, Action closed)
    {
        this.frames = frames;
        this.bounds = bounds;
        this.superResolution = superResolution;
        this.log = log;
        this.closed = closed;
        thread = new Thread(Run) { IsBackground = true, Name = "CouchSubs oyun modu", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>
    /// Oyun modunu <paramref name="bounds"/> ekran dikdörtgeninde (fiziksel piksel) açar. <paramref name="closed"/>
    /// pencere kapanınca (Esc, hata) oyun modunun iş parçacığında çağrılır.
    /// </summary>
    public static GameModeWindow Open(VideoFrameBuffer frames, RawRect bounds, Func<bool> superResolution, Action<string> log, Action closed)
    {
        var mode = new GameModeWindow(frames, bounds, superResolution, log, closed);
        frames.FrameWritten += mode.OnFrame;
        mode.thread.Start();
        return mode;
    }

    private long lastArrivalTicks;

    private void OnFrame()
    {
        Volatile.Write(ref lastArrivalTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        frameReady.Set();
    }

    /// <summary>Verilen ana kadar bekler: 1 ms'lik uykular, son milisaniye dönerek (kare zamanı hassas olsun).</summary>
    private static void WaitUntil(double dueMs)
    {
        static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        while (dueMs - Now() > 1.5) Thread.Sleep(1);
        while (Now() < dueMs) Thread.SpinWait(50);
    }

    /// <summary>Fork: arkası bulanık gösterilecek kutu (oyun penceresinin pikselleri; köşe yarıçapı, karartma 0..1).</summary>
    public readonly record struct BlurArea(RawRect Rect, float Corner, float Darken);

    /// <summary>Altyazı katmanındaki kutuların yerleri (arayüz iş parçacığından); sonraki karede uygulanır.</summary>
    public void SetBlurAreas(IReadOnlyList<BlurArea> areas) => blurAreas = [.. areas];

    public void Dispose()
    {
        stopping = true;
        frameReady.Set();
        frames.FrameWritten -= OnFrame;
    }

    private void Run()
    {
        Scaler? scaler = null;
        try
        {
            window = CreateWindow();
            scaler = new Scaler(window, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            log("oyun modu açıldı (DirectX tam ekran, en düşük gecikme)");
            var version = -1L;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long since = 0, shown = 0;
            double work = 0, waited = 0;
            // Fork: düzensiz gelen kareler küçük bir payla eşit aralıkla gösterilir (hareket halinde takılma olmasın).
            var smoother = new ArrivalSmoother();
            timeBeginPeriod(1);
            while (!stopping)
            {
                var signaled = frameReady.WaitOne(50);
                if (!PumpMessages()) break;
                if (signaled)
                {
                    var arrived = Volatile.Read(ref lastArrivalTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    var due = smoother.Due(arrived);
                    var now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (due > now)
                    {
                        waited += due - now;
                        WaitUntil(due);
                    }
                }
                var started = clock.Elapsed.TotalMilliseconds;
                var newer = frames.ReadIfNewer(version, (pixels, width, height) => scaler.Draw(pixels, width, height, superResolution()));
                if (newer is { } read)
                {
                    version = read;
                    scaler.Present(blurAreas);
                    shown++;
                    work += clock.Elapsed.TotalMilliseconds - started;
                }
                // Takılma şikâyetinde bakılabilsin: 10 sn'de bir gösterilen kare hızı ve kare başına çizim süresi.
                if (clock.ElapsedMilliseconds - since >= 10_000)
                {
                    var seconds = (clock.ElapsedMilliseconds - since) / 1000.0;
                    log($"oyun modu: {shown / seconds:0.0} kare/sn, kare başına {(shown > 0 ? work / shown : 0):0.0} ms (yükleme + büyütme + gösterme)" +
                        $", akıcılık payı {smoother.MarginMs:0.0} ms (ortalama bekleme {(shown > 0 ? waited / shown : 0):0.0} ms)");
                    since = clock.ElapsedMilliseconds;
                    shown = 0;
                    work = 0;
                    waited = 0;
                }
            }
        }
        catch (Exception error)
        {
            log($"oyun modu kapandı: {error.GetBaseException().Message}");
        }
        finally
        {
            timeEndPeriod(1);
            frames.FrameWritten -= OnFrame;
            scaler?.Dispose();
            if (window != 0) DestroyWindow(window);
            window = 0;
            closed();
        }
    }

    /// <summary>Pencere iletilerini işler; pencere kapandıysa (Esc) false.</summary>
    private bool PumpMessages()
    {
        while (PeekMessageW(out var message, 0, 0, 0, 1))
        {
            if (message.message == WmQuit) return false;
            TranslateMessage(message);
            DispatchMessageW(message);
        }
        return !stopping;
    }

    private nint CreateWindow()
    {
        procedure = WindowProcedure;
        var instance = GetModuleHandleW(null);
        var className = "CouchSubsGameMode";
        var type = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = instance,
            hCursor = LoadCursorW(0, 32512),
            hbrBackground = GetStockObject(4),
            lpszClassName = className,
        };
        RegisterClassExW(ref type);
        var handle = CreateWindowExW(WsExAppWindow, className, "CouchSubs oyun modu", WsPopup | WsVisible,
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0, 0, instance, 0);
        if (handle == 0) throw new InvalidOperationException($"pencere açılamadı ({Marshal.GetLastWin32Error()})");
        SetForegroundWindow(handle);
        return handle;
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmKeyDown when wParam is VkEscape or VkF11:
                PostQuitMessage(0);
                return 0;
            case WmClose:
                PostQuitMessage(0);
                return 0;
            case WmSetCursor when (lParam & 0xFFFF) == 1:
                // İmleç oyunun üstünde görünmesin.
                SetCursor(0);
                return 1;
        }
        return DefWindowProcW(handle, message, wParam, lParam);
    }

    /// <summary>D3D11 video işlemcisi (BGRA kare → ekran boyu, NVIDIA RTX büyütmesi) ve DXGI flip zinciri.</summary>
    private sealed class Scaler : IDisposable
    {
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
        private readonly IDXGISwapChain1 swapChain;
        private readonly ID3D11Texture2D target;
        private readonly bool tearing;
        private readonly int width, height;
        private ID3D11Texture2D? input;
        private ID3D11VideoProcessorEnumerator? enumerator;
        private ID3D11VideoProcessor? processor;
        private ID3D11VideoProcessorInputView? inputView;
        private ID3D11VideoProcessorOutputView? outputView;
        private int inputWidth, inputHeight;
        private bool? superResolution;
        private readonly ID3D11ShaderResourceView targetView;
        private readonly ID3D11VertexShader blurVertex;
        private readonly ID3D11PixelShader blurPixel;
        private readonly ID3D11Buffer blurConstants;
        private readonly ID3D11SamplerState sampler;
        private readonly ID3D11RasterizerState scissor;
        private readonly ID3D11BlendState blend;

        [StructLayout(LayoutKind.Sequential)]
        private struct BlurConstants
        {
            public System.Numerics.Vector4 Rect;
            public System.Numerics.Vector4 Shape;
            public System.Numerics.Vector4 Texel;
        }

        public Scaler(nint window, int width, int height)
        {
            this.width = width;
            this.height = height;
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out var created, out var immediate).CheckError();
            device = created!;
            context = immediate!;
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = context.QueryInterface<ID3D11VideoContext>();
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice1>()) dxgiDevice.SetMaximumFrameLatency(1);
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) tearing = factory5?.PresentAllowTearing == true;
            swapChain = factory.CreateSwapChainForHwnd(device, window, new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
            }, null, null);
            factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            // Mip zinciri: bulanık kutular küçültülmüş seviyelerden ucuzca okunur.
            target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 0,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.GenerateMips));
            targetView = device.CreateShaderResourceView(target, null);
            blurVertex = device.CreateVertexShader(GameModeShaders.BlurVertex, null);
            blurPixel = device.CreatePixelShader(GameModeShaders.BlurPixel, null);
            blurConstants = device.CreateBuffer((uint)Marshal.SizeOf<BlurConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            scissor = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true });
            blend = device.CreateBlendState(BlendDescription.NonPremultiplied);
        }

        public unsafe void Draw(byte[] bgra, int frameWidth, int frameHeight, bool wantSuperResolution)
        {
            EnsureProcessor(frameWidth, frameHeight);
            fixed (byte* pixels = bgra)
                context.UpdateSubresource(input!, 0, null, (nint)pixels, (uint)(frameWidth * 4), (uint)(frameWidth * frameHeight * 4));
            if (superResolution != wantSuperResolution)
            {
                superResolution = wantSuperResolution;
                var data = new NvidiaStreamExtension { Version = 1, Method = 2, Enable = wantSuperResolution ? 1u : 0u };
                try { videoContext.VideoProcessorSetStreamExtension(processor!, 0, NvidiaPostProcessing, (uint)sizeof(NvidiaStreamExtension), (nint)(&data)); }
                catch (Exception) { }
            }
            videoContext.VideoProcessorBlt(processor!, outputView!, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = inputView! }]);
        }

        public void Present(BlurArea[] areas)
        {
            using (var back = swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                context.CopySubresourceRegion(back, 0, 0, 0, 0, target, 0, null);
                if (areas.Length > 0) DrawBlur(back, areas);
            }
            // Dikey eşitleme beklenmez: kare hemen ekrana (G-SYNC açıksa yırtılmasız).
            swapChain.Present(0, tearing ? PresentFlags.AllowTearing : PresentFlags.None);
        }

        /// <summary>Kutuların arkasını küçültülmüş kareden bulanık çizer (yuvarlak köşe, hafif karartma).</summary>
        private void DrawBlur(ID3D11Texture2D back, BlurArea[] areas)
        {
            context.GenerateMips(targetView);
            using var output = device.CreateRenderTargetView(back, null);
            context.OMSetRenderTargets(output, null);
            context.RSSetViewport(0, 0, width, height, 0, 1);
            context.RSSetState(scissor);
            context.OMSetBlendState(blend);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(blurVertex);
            context.PSSetShader(blurPixel);
            context.PSSetShaderResource(0, targetView);
            context.PSSetSampler(0, sampler);
            context.PSSetConstantBuffer(0, blurConstants);
            foreach (var area in areas)
            {
                var rect = area.Rect;
                if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) continue;
                var constants = new BlurConstants
                {
                    Rect = new(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    Shape = new(Math.Max(12, (rect.Bottom - rect.Top) * 0.45f), area.Corner, area.Darken, 0),
                    Texel = new(1f / width, 1f / height, 0, 0),
                };
                context.UpdateSubresource(ref constants, blurConstants);
                context.RSSetScissorRect(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                    Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
                context.Draw(3, 0);
            }
            // Gölgelendirici kaynağı bırakılır: sonraki karede doku video işlemcisine yazılacak.
            context.PSSetShaderResource(0, null!);
            context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        }

        private void EnsureProcessor(int frameWidth, int frameHeight)
        {
            if (processor is not null && frameWidth == inputWidth && frameHeight == inputHeight) return;
            outputView?.Dispose();
            inputView?.Dispose();
            processor?.Dispose();
            enumerator?.Dispose();
            input?.Dispose();
            inputWidth = frameWidth;
            inputHeight = frameHeight;
            superResolution = null;
            input = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)frameWidth, (uint)frameHeight, 1, 1,
                BindFlags.ShaderResource | BindFlags.RenderTarget));
            enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputWidth = (uint)frameWidth,
                InputHeight = (uint)frameHeight,
                OutputWidth = (uint)width,
                OutputHeight = (uint)height,
                InputFrameRate = new Rational(60, 1),
                OutputFrameRate = new Rational(60, 1),
                Usage = VideoUsage.OptimalSpeed,
            });
            processor = videoDevice.CreateVideoProcessor(enumerator, 0);
            inputView = videoDevice.CreateVideoProcessorInputView(input, enumerator, new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
            outputView = videoDevice.CreateVideoProcessorOutputView(target, enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
            // Görüntü oranı korunur (16:9 olmayan ekranda kenarlar siyah).
            var scale = Math.Min((double)width / frameWidth, (double)height / frameHeight);
            int w = (int)Math.Round(frameWidth * scale), h = (int)Math.Round(frameHeight * scale);
            int x = (width - w) / 2, y = (height - h) / 2;
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, frameWidth, frameHeight));
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(x, y, x + w, y + h));
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, width, height));
        }

        public void Dispose()
        {
            outputView?.Dispose();
            inputView?.Dispose();
            processor?.Dispose();
            enumerator?.Dispose();
            input?.Dispose();
            blend.Dispose();
            scissor.Dispose();
            sampler.Dispose();
            blurConstants.Dispose();
            blurPixel.Dispose();
            blurVertex.Dispose();
            targetView.Dispose();
            target.Dispose();
            swapChain.Dispose();
            videoContext.Dispose();
            videoDevice.Dispose();
            context.Dispose();
            device.Dispose();
        }
    }

    // --- Win32 ---
    private const uint WsPopup = 0x80000000, WsVisible = 0x10000000, WsExAppWindow = 0x00040000;
    private const uint WmKeyDown = 0x0100, WmClose = 0x0010, WmQuit = 0x0012, WmSetCursor = 0x0020;
    private const nint VkEscape = 0x1B, VkF11 = 0x7A;

    private delegate nint WndProc(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width,
                                               int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint handle, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg message, nint handle, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(in Msg message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance, nint name);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
