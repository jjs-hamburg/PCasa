using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using DxFormat = Vortice.DXGI.Format;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace PCasa.App.Services;

/// <summary>
/// Dedicated Direct3D 11 flip-model presentation for the slideshow.
/// A native HWND avoids the WPF compositor/animation scheduler entirely.
/// A single GPU thread owns ALL device resources and runs Present(1) at vsync.
/// Decoding and image-to-BGRA conversion happen outside the render loop.
/// </summary>
internal sealed class Direct3DSlideshowSurface : IDisposable
{
    private const string Shader = """
        Texture2D Photo : register(t0);
        SamplerState LinearSample : register(s0);
        struct Input { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 tint : COLOR0; };
        struct Output { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 tint : COLOR0; };
        Output VS(Input input)
        {
            Output output;
            output.pos = float4(input.pos, 0.0, 1.0);
            output.uv = input.uv;
            output.tint = input.tint;
            return output;
        }
        float4 PS(Output input) : SV_TARGET
        {
            return Photo.Sample(LinearSample, input.uv) * input.tint;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex(float x, float y, float u, float v,
        float red, float green, float blue, float alpha)
    {
        public float X = x;
        public float Y = y;
        public float U = u;
        public float V = v;
        public float Red = red;
        public float Green = green;
        public float Blue = blue;
        public float Alpha = alpha;
    }

    private sealed record Pixels(int Width, int Height, byte[] Data);

    private sealed class Texture : IDisposable
    {
        public readonly ID3D11Texture2D Surface;
        public readonly ID3D11ShaderResourceView View;
        public readonly int Width;
        public readonly int Height;

        public Texture(ID3D11Device device, ID3D11DeviceContext context, Pixels pixels)
        {
            Width = pixels.Width;
            Height = pixels.Height;
            Surface = device.CreateTexture2D(new Texture2DDescription(
                DxFormat.B8G8R8A8_UNorm, (uint)Width, (uint)Height, mipLevels: 1));
            context.UpdateSubresource(pixels.Data, Surface, 0, (uint)(Width * 4));
            View = device.CreateShaderResourceView(Surface);
        }

        public void Dispose()
        {
            View.Dispose();
            Surface.Dispose();
        }
    }

    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Dispatcher _dispatcher;
    private Thread? _worker;
    private volatile bool _stopping;
    private volatile bool _ready;
    private Exception? _startupError;
    private IntPtr _hwnd;
    private int _width;
    private int _height;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _target;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _layout;
    private ID3D11Buffer? _vertices;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _blend;
    private ID3D11RasterizerState? _rasterizer;
    private Texture? _active;
    private Texture? _next;
    private Texture? _background;
    private Texture? _white;
    private Texture? _black;
    private string _transition = "none";
    private int _duration;
    private long _startedAt;
    private Action? _completed;
    private bool _dirty = true;

    private bool _fullscreen;
    private double _galleryPercent = 86;
    private double _border;
    private bool _shadow;
    private double _shadowOffset;
    private double _shadowWidth;
    private double _shadowOpacity;

    public bool IsReady => _ready;
    public Exception? StartupError => _startupError;

    public Direct3DSlideshowSurface(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool Start(IntPtr hwnd, int width, int height,
        SlideshowSettings settings)
    {
        if (_worker is not null) return _ready;
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _fullscreen = settings.DisplayMode == "fullscreen";
        _galleryPercent = settings.GalleryPercent;
        _border = settings.DisplayMode == "gallery" && settings.WhiteBorder
            ? settings.BorderWidth : 0;
        _shadow = settings.DisplayMode == "gallery" && settings.Shadow;
        _shadowOffset = settings.ShadowOffset;
        _shadowWidth = settings.ShadowWidth;
        _shadowOpacity = settings.ShadowOpacity / 100.0;
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "PCasa Direct3D11 slideshow"
        };
        _worker.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
            return false;
        return _ready;
    }

    public void Resize(int width, int height)
    {
        Post(() =>
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;
            _context!.UnsetRenderTargets();
            _target?.Dispose();
            _target = null;
            _swapChain!.ResizeBuffers(2, (uint)width, (uint)height,
                DxFormat.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateBackBuffer();
            _background?.Dispose();
            _background = null;
            _dirty = true;
        });
    }

    public void SetBackground(BitmapSource image)
    {
        var pixels = CopyPixels(image);
        Post(() =>
        {
            _background?.Dispose();
            _background = new Texture(_device!, _context!, pixels);
            _dirty = true;
        });
    }

    /// <summary>Upload the next frozen picture ONCE; animate GPU geometry thereafter.</summary>
    public void Present(BitmapSource image, string effect, int milliseconds,
        Action completed)
    {
        var pixels = CopyPixels(image);
        Post(() =>
        {
            var incoming = new Texture(_device!, _context!, pixels);
            if (_active is null)
            {
                _active = incoming;
                _dirty = true;
                return;
            }
            EndTransition(reportCompletion: false);
            _next = incoming;
            _transition = effect;
            _duration = Math.Max(0, milliseconds);
            _completed = completed;
            _startedAt = Stopwatch.GetTimestamp();
            if (effect == "none" || _duration == 0)
                EndTransition(reportCompletion: true);
            _dirty = true;
        });
    }

    public void Replace(BitmapSource image)
    {
        var pixels = CopyPixels(image);
        Post(() =>
        {
            var newTexture = new Texture(_device!, _context!, pixels);
            if (_next is not null)
            {
                _next.Dispose();
                _next = newTexture;
            }
            else
            {
                _active?.Dispose();
                _active = newTexture;
            }
            _dirty = true;
        });
    }

    // Manual left/right during an unfinished transition must promote the
    // incoming picture before the next transition begins, never leave a
    // half-shifted outgoing frame behind.
    public void Complete() => Post(() => EndTransition(reportCompletion: false));

    public static double SlideTravel(double windowWidth, double oldWidth,
        double nextWidth)
    {
        if (!(windowWidth > 0) || !(oldWidth >= 0) || !(nextWidth >= 0))
            return 0;
        var oldRightMargin = (windowWidth - oldWidth) / 2;
        return (oldWidth + nextWidth) / 2 + oldRightMargin;
    }

    private static Pixels CopyPixels(BitmapSource source)
    {
        // WPF objects are accessed only from their originating dispatcher.
        // The graphics worker receives plain immutable BGRA bytes.
        BitmapSource converted = source.Format == WpfPixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, WpfPixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var data = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(data, stride, 0);
        return new Pixels(converted.PixelWidth, converted.PixelHeight, data);
    }

    private void Post(Action action)
    {
        if (_stopping || !_ready) return;
        _commands.Enqueue(action);
        _wake.Set();
    }

    private void Run()
    {
        try
        {
            InitializeDevice();
            _ready = true;
            _started.Set();
            while (!_stopping)
            {
                while (_commands.TryDequeue(out var action)) action();
                var animating = _next is not null;
                if (_dirty || animating)
                {
                    RenderFrame();
                    _dirty = false;
                }
                else _wake.WaitOne(1000);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            PCasaDiagnostics.Write("Slideshow.Direct3D11", ex);
            _started.Set();
        }
        finally
        {
            _ready = false;
            ReleaseDevice();
        }
    }

    private void InitializeDevice()
    {
        var status = D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
            out _device!, out _context!);
        if (status.Failure)
            D3D11CreateDevice(null, DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_0],
                out _device!, out _context!).CheckError();

        using var factory = CreateDXGIFactory1<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd,
            new SwapChainDescription1
            {
                Width = (uint)_width, Height = (uint)_height,
                Format = DxFormat.B8G8R8A8_UNorm,
                BufferCount = 2, BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput,
                SwapEffect = SwapEffect.FlipDiscard,
                SampleDescription = new SampleDescription(1, 0),
                AlphaMode = AlphaMode.Ignore
            });
        CreateBackBuffer();
        var vs = Compiler.Compile(Shader, "VS", "PCasaSlide.hlsl", "vs_5_0");
        var ps = Compiler.Compile(Shader, "PS", "PCasaSlide.hlsl", "ps_5_0");
        _vertexShader = _device.CreateVertexShader(vs.Span);
        _pixelShader = _device.CreatePixelShader(ps.Span);
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, DxFormat.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, DxFormat.R32G32_Float, 8, 0),
            new InputElementDescription("COLOR", 0, DxFormat.R32G32B32A32_Float, 16, 0)
        ], vs.Span);
        _vertices = _device.CreateBuffer(6u * 32u, BindFlags.VertexBuffer);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _blend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _rasterizer = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None
        });
        _white = new Texture(_device, _context, new Pixels(1, 1,
            [255, 255, 255, 255]));
        _black = new Texture(_device, _context, new Pixels(1, 1,
            [0, 0, 0, 255]));
    }

    private void CreateBackBuffer()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _target = _device!.CreateRenderTargetView(backBuffer);
    }

    private (float X, float Y, float W, float H) Frame(Texture image)
    {
        var usableW = _fullscreen ? _width : _width * _galleryPercent / 100.0;
        var usableH = _fullscreen ? _height : _height * _galleryPercent / 100.0;
        var scale = Math.Min(
            Math.Max(1, usableW - 2 * _border) / image.Width,
            Math.Max(1, usableH - 2 * _border) / image.Height);
        var w = (float)(image.Width * scale + 2 * _border);
        var h = (float)(image.Height * scale + 2 * _border);
        return ((_width - w) / 2f, (_height - h) / 2f, w, h);
    }

    private void RenderFrame()
    {
        var ctx = _context!;
        ctx.OMSetRenderTargets(_target!);
        ctx.OMSetBlendState(_blend);
        ctx.RSSetViewport(new Viewport(0, 0, _width, _height));
        ctx.RSSetState(_rasterizer);
        ctx.IASetInputLayout(_layout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffers(0, [_vertices!], [32], [0]);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.PSSetSampler(0, _sampler);
        ctx.ClearRenderTargetView(_target!, new Color4(.11f, .11f, .12f, 1));
        if (_background is not null)
            Draw(_background, 0, 0, _width, _height, 1);

        var t = _next is null ? 1f :
            Math.Clamp((float)(Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds /
                Math.Max(1, _duration)), 0f, 1f);
        if (_active is not null)
        {
            var a = Frame(_active);
            float activeShift = 0;
            if (_next is not null && _transition == "slide")
            {
                var b = Frame(_next);
                activeShift = (float)(-SlideTravel(_width, a.W, b.W) * t);
            }
            DrawPhoto(_active, a, activeShift, 1, 1);
        }
        if (_next is not null)
        {
            var b = Frame(_next);
            float shift = 0, alpha = 1, zoom = 1;
            if (_transition == "slide")
            {
                var a = Frame(_active!);
                shift = (float)(SlideTravel(_width, a.W, b.W) * (1 - t));
            }
            else if (_transition == "fade") alpha = t;
            else if (_transition == "zoom")
            {
                alpha = t;
                zoom = .97f + .03f * t;
            }
            DrawPhoto(_next, b, shift, alpha, zoom);
        }
        _swapChain!.Present(1, PresentFlags.None).CheckError();
        if (_next is not null && t >= 1)
            EndTransition(reportCompletion: true);
    }

    private void DrawPhoto(Texture image, (float X, float Y, float W, float H) rect,
        float shift, float alpha, float zoom)
    {
        var w = rect.W * zoom;
        var h = rect.H * zoom;
        var x = rect.X + (rect.W - w) / 2 + shift;
        var y = rect.Y + (rect.H - h) / 2;
        if (_shadow)
        {
            // Low-cost GPU translucent shadow layers, not WPF DropShadowEffect.
            var spread = (float)Math.Clamp(_shadowWidth, 1, 40);
            for (var layer = 3; layer >= 1; layer--)
            {
                var pad = spread * layer / 3;
                Draw(_black!, x + (float)_shadowOffset - pad,
                    y + (float)_shadowOffset - pad,
                    w + 2 * pad, h + 2 * pad,
                    alpha * (float)_shadowOpacity * .17f);
            }
        }
        if (_border > 0)
            Draw(_white!, x, y, w, h, alpha);
        var inset = (float)_border * zoom;
        Draw(image, x + inset, y + inset,
            Math.Max(1, w - 2 * inset), Math.Max(1, h - 2 * inset), alpha);
    }

    private void Draw(Texture image, float x, float y, float width, float height,
        float alpha)
    {
        if (width <= 0 || height <= 0 || alpha <= 0) return;
        float x0 = x * 2 / _width - 1;
        float x1 = (x + width) * 2 / _width - 1;
        float y0 = 1 - y * 2 / _height;
        float y1 = 1 - (y + height) * 2 / _height;
        var vertices = new[]
        {
            new Vertex(x0,y0,0,0,1,1,1,alpha),
            new Vertex(x1,y0,1,0,1,1,1,alpha),
            new Vertex(x1,y1,1,1,1,1,1,alpha),
            new Vertex(x0,y0,0,0,1,1,1,alpha),
            new Vertex(x1,y1,1,1,1,1,1,alpha),
            new Vertex(x0,y1,0,1,1,1,1,alpha)
        };
        _context!.UpdateSubresource(vertices, _vertices!);
        _context.PSSetShaderResource(0, image.View);
        _context.Draw(6, 0);
    }

    private void EndTransition(bool reportCompletion)
    {
        if (_next is null) return;
        _active?.Dispose();
        _active = _next;
        _next = null;
        var callback = _completed;
        _completed = null;
        _dirty = true;
        if (reportCompletion && callback is not null)
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
    }

    private void ReleaseDevice()
    {
        _next?.Dispose();
        _active?.Dispose();
        _background?.Dispose();
        _white?.Dispose();
        _black?.Dispose();
        _context?.UnsetRenderTargets();
        _target?.Dispose();
        _rasterizer?.Dispose();
        _blend?.Dispose();
        _sampler?.Dispose();
        _vertices?.Dispose();
        _layout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_worker is not null && !_worker.Join(TimeSpan.FromSeconds(3)))
            PCasaDiagnostics.WriteMessage("Slideshow.Direct3D11",
                "Renderer still completing GPU Present during shutdown.");
        _wake.Dispose();
        _started.Dispose();
    }
}
