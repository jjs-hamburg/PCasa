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
        struct Input { float3 pos : POSITION; float2 uv : TEXCOORD0; float4 tint : COLOR0; };
        struct Output { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 tint : COLOR0; };
        Output VS(Input input)
        {
            Output output;
            // Clip W carries perspective depth. Ordinary slideshow quads use W=1.
            output.pos = float4(input.pos.xy, 0.0, input.pos.z);
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
    private struct Vertex(float x, float y, float w, float u, float v,
        float red, float green, float blue, float alpha)
    {
        public float X = x;
        public float Y = y;
        public float W = w;
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
    private int _drawnTransitionFrames;

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
            _drawnTransitionFrames = 0;
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

    /// <summary>Travel required for the OLD (possibly wide) photo and
    /// its right-hand shadow to disappear completely off the left edge.</summary>
    public static double SlideExitTravel(double windowWidth, double oldWidth,
        double shadowRightExtent = 0)
    {
        if (!(windowWidth > 0) || !(oldWidth >= 0)) return 0;
        return (windowWidth + oldWidth) / 2 +
            Math.Max(0, shadowRightExtent) + 1;
    }

    // Only the right/bottom sides grow: no negative padding or symmetrical
    // blur layer may extend a shadow beyond the upper or left photo edge.
    public static (float X, float Y, float Width, float Height) ShadowRect(
        float x, float y, float width, float height,
        float offset, float rightBottomExpansion) =>
        (x + Math.Max(0, offset),
         y + Math.Max(0, offset),
         width + Math.Max(0, rightBottomExpansion),
         height + Math.Max(0, rightBottomExpansion));

    private float ShadowExtent => _shadow
        ? (float)(Math.Max(0, _shadowOffset) + Math.Max(0, _shadowWidth)) : 0;

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
            new InputElementDescription("POSITION", 0, DxFormat.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, DxFormat.R32G32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, DxFormat.R32G32B32A32_Float, 20, 0)
        ], vs.Span);
        _vertices = _device.CreateBuffer(6u * 36u, BindFlags.VertexBuffer);
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
        ctx.IASetVertexBuffers(0, [_vertices!], [36], [0]);
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
            var b = _next is null ? a : Frame(_next);
            if (_next is null)
            {
                DrawPhoto(_active, a, 0, 1, 1);
            }
            else if (_transition == "cards")
            {
                // Incoming card lies underneath. The outgoing card glides
                // away; no WPF storyboard or additional image decoding.
                DrawPhoto(_next, b, 0, 1, .95f + .05f * t);
                DrawPhoto(_active, a, (float)(_width * .85 * t),
                    Math.Clamp(1f - t * 1.3f, 0, 1), 1f - .08f * t);
            }
            else if (_transition == "projector")
            {
                // Film projector: held frame, a brief black shutter interval,
                // then the next frame appears with a restrained light pulse.
                // Entire effect is GPU geometry/alpha, not WPF animation.
                if (t < .44f)
                    DrawPhoto(_active, a, 0, 1, 1f + .012f * t);
                else if (t >= .58f)
                {
                    DrawPhoto(_next, b, 0, 1, 1.008f - .008f * t);
                    var flicker = (float)(Math.Sin((t - .58f) * 40) *
                        Math.Exp(-(t - .58f) * 12));
                    if (flicker > 0)
                        Draw(_white!, 0, 0, _width, _height, .11f * flicker);
                }
                if (t >= .40f && t < .59f)
                    Draw(_black!, 0, 0, _width, _height, 1);
            }
            else if (_transition == "cube")
            {
                // Genuine two-face 3D rotation: shared hinge/depth and
                // GPU perspective interpolation. Different photo aspect
                // ratios are independently centered on equal-size panels.
                DrawCubeTransition(_active, _next, t);
            }
            else
            {
                // The slide uses two independent distances with ONE clock:
                // the next image starts with the requested right-hand gap,
                // while a wider outgoing image really exits the viewport.
                var oldShift = _transition == "slide"
                    ? (float)(-Math.Max(SlideTravel(_width, a.W, b.W),
                        SlideExitTravel(_width, a.W, ShadowExtent)) * t)
                    : 0;
                var oldAlpha = _transition == "zoomfade" ? 1 - t : 1;
                var oldZoom = _transition == "zoomfade" ? 1f + .045f * t : 1;
                if (_transition != "slide" || t < 1)
                    DrawPhoto(_active, a, oldShift, oldAlpha, oldZoom);

                float shiftX = 0, shiftY = 0, alpha = 1, zoom = 1;
                switch (_transition)
                {
                    case "slide":
                        shiftX = (float)(SlideTravel(_width, a.W, b.W) * (1 - t));
                        break;
                    case "fade":
                        alpha = t;
                        break;
                    case "zoom":
                        alpha = t;
                        zoom = .97f + .03f * t;
                        break;
                    case "zoomfade":
                        alpha = t;
                        zoom = 1.05f - .05f * t;
                        break;
                    case "wipe-left":
                        shiftX = _width * (1 - t);
                        break;
                    case "wipe-right":
                        shiftX = -_width * (1 - t);
                        break;
                    case "wipe-up":
                        shiftY = _height * (1 - t);
                        break;
                    case "wipe-down":
                        shiftY = -_height * (1 - t);
                        break;
                }
                DrawPhoto(_next, b, shiftX, alpha, zoom, shiftY);
            }
        }
        _swapChain!.Present(1, PresentFlags.None).CheckError();
        if (_next is not null) _drawnTransitionFrames++;
        if (_next is not null && t >= 1)
            EndTransition(reportCompletion: true);
    }

    private void DrawPhoto(Texture image, (float X, float Y, float W, float H) rect,
        float shift, float alpha, float zoom, float shiftY = 0,
        float horizontalScale = 1)
    {
        var w = rect.W * zoom * horizontalScale;
        var h = rect.H * zoom;
        if (w <= 0 || h <= 0 || alpha <= 0) return;
        var x = rect.X + (rect.W - w) / 2 + shift;
        var y = rect.Y + (rect.H - h) / 2 + shiftY;
        if (_shadow && _shadowOpacity > 0)
        {
            var offset = (float)Math.Max(0, _shadowOffset);
            var spread = (float)Math.Max(0, _shadowWidth);
            // A zero-width shadow is the sharp translated silhouette, not
            // a 1px blur. Positive width grows ONLY right and down.
            if (spread <= 0)
            {
                var sharp = ShadowRect(x, y, w, h, offset, 0);
                Draw(_black!, sharp.X, sharp.Y, sharp.Width, sharp.Height,
                    alpha * (float)_shadowOpacity);
            }
            else
            {
                // Twelve GPU-only nested steps produce a much softer
                // one-sided falloff than the old three hard rectangles.
                // No expensive WPF DropShadowEffect runs per frame.
                for (var layer = 12; layer >= 1; layer--)
                {
                    var shadow = ShadowRect(x, y, w, h,
                        offset, spread * layer / 12);
                    Draw(_black!, shadow.X, shadow.Y,
                        shadow.Width, shadow.Height,
                        alpha * (float)_shadowOpacity * .065f);
                }
                var core = ShadowRect(x, y, w, h, offset, 0);
                Draw(_black!, core.X, core.Y, core.Width, core.Height,
                    alpha * (float)_shadowOpacity * .30f);
            }
        }
        if (_border > 0)
            Draw(_white!, x, y, w, h, alpha);
        var insetX = (float)_border * zoom * horizontalScale;
        var insetY = (float)_border * zoom;
        Draw(image, x + insetX, y + insetY,
            Math.Max(1, w - 2 * insetX), Math.Max(1, h - 2 * insetY), alpha);
    }

    // The two photographs live on distinct faces of one rigid cuboid.
    // Face width and height are fixed for the ENTIRE transition; the depth
    // equals its width. Only each photograph's inner rectangle differs.
    private void DrawCubeTransition(Texture outgoing, Texture incoming, float progress)
    {
        var faceWidth = (float)(_width * (_fullscreen ? 1.0 : _galleryPercent / 100.0));
        var faceHeight = (float)(_height * (_fullscreen ? 1.0 : _galleryPercent / 100.0));
        var t = Math.Clamp(progress, 0f, 1f);
        // Back-facing surfaces must not bleed past the silhouette. The
        // camera is centered in front of the cube, 3.5 face widths away.
        if (CubeFaceIsVisible(incomingFace: false, t))
            DrawCubeFace(outgoing, incomingFace: false, t, faceWidth, faceHeight);
        if (CubeFaceIsVisible(incomingFace: true, t))
            DrawCubeFace(incoming, incomingFace: true, t, faceWidth, faceHeight);
    }

    internal static bool CubeFaceIsVisible(bool incomingFace, float progress)
    {
        var angle = (float)(-Math.PI * .5 * Math.Clamp(progress, 0f, 1f));
        // Signed camera-facing dot product divided by face width.
        return incomingFace
            ? -3.5f * MathF.Sin(angle) > .5f
            : 3.5f * MathF.Cos(angle) > .5f;
    }

    // Pure geometry for regression tests. At t=0 the outgoing face is
    // front-facing; at t=1 the incoming face occupies the exact same plane.
    // Both faces share their hinge at outgoing(u=1) / incoming(u=0).
    internal static (float X, float Y, float ClipW) CubePoint(
        float faceWidth, float faceHeight, float u, float v,
        bool incomingFace, float progress)
    {
        var half = faceWidth * .5f;
        var angle = (float)(-Math.PI * .5 * Math.Clamp(progress, 0f, 1f));
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var x = incomingFace ? half : (u - .5f) * faceWidth;
        var z = incomingFace ? half - u * faceWidth : half;
        var y = (v - .5f) * faceHeight;
        var rotatedX = x * cos + z * sin;
        var rotatedZ = -x * sin + z * cos;
        // Normalize to W=1 at both end frames; never clip the nearer hinge.
        var distance = faceWidth * 3.5f;
        var clipW = (distance - rotatedZ) / (distance - half);
        return (rotatedX, y, clipW);
    }

    internal static (float U, float V, float Width, float Height) CubeImageRect(
        float faceWidth, float faceHeight, float pixelWidth, float pixelHeight,
        float border)
    {
        var roomWidth = Math.Max(1f, faceWidth - 2f * border);
        var roomHeight = Math.Max(1f, faceHeight - 2f * border);
        var scale = MathF.Min(roomWidth / pixelWidth, roomHeight / pixelHeight);
        var w = pixelWidth * scale;
        var h = pixelHeight * scale;
        return ((faceWidth - w) / (2f * faceWidth),
            (faceHeight - h) / (2f * faceHeight),
            w / faceWidth, h / faceHeight);
    }

    private void DrawCubeFace(Texture photo, bool incomingFace, float progress,
        float faceWidth, float faceHeight)
    {
        // The perspective itself defines the cube. A dark opaque-looking
        // panel plus an offset photo shadow made the two rotated faces
        // visually collide, especially with portrait/landscape changes.
        // Render only each correctly centered photograph and its frame;
        // other slideshow effects retain the user's shadow settings.
        var inner = CubeImageRect(faceWidth, faceHeight,
            photo.Width, photo.Height, (float)_border);
        var frameX = (float)(_border / faceWidth);
        var frameY = (float)(_border / faceHeight);
        if (_border > 0)
            DrawCubeQuad(_white!, incomingFace, progress, faceWidth, faceHeight,
                inner.U - frameX, inner.V - frameY,
                inner.Width + 2 * frameX, inner.Height + 2 * frameY, 1);
        DrawCubeQuad(photo, incomingFace, progress, faceWidth, faceHeight,
            inner.U, inner.V, inner.Width, inner.Height, 1);
    }

    private void DrawCubeQuad(Texture texture, bool incomingFace, float progress,
        float faceWidth, float faceHeight, float u, float v,
        float width, float height, float alpha)
    {
        if (width <= 0 || height <= 0 || alpha <= 0) return;
        Vertex Corner(float x, float y, float tu, float tv)
        {
            var point = CubePoint(faceWidth, faceHeight, x, y, incomingFace, progress);
            return new Vertex(point.X * 2f / _width,
                -point.Y * 2f / _height, point.ClipW, tu, tv, 1, 1, 1, alpha);
        }
        var topLeft = Corner(u, v, 0, 0);
        var topRight = Corner(u + width, v, 1, 0);
        var bottomRight = Corner(u + width, v + height, 1, 1);
        var bottomLeft = Corner(u, v + height, 0, 1);
        var vertices = new[]
        {
            topLeft, topRight, bottomRight,
            topLeft, bottomRight, bottomLeft
        };
        _context!.UpdateSubresource(vertices, _vertices!);
        _context.PSSetShaderResource(0, texture.View);
        _context.Draw(6, 0);
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
            new Vertex(x0,y0,1,0,0,1,1,1,alpha),
            new Vertex(x1,y0,1,1,0,1,1,1,alpha),
            new Vertex(x1,y1,1,1,1,1,1,1,alpha),
            new Vertex(x0,y0,1,0,0,1,1,1,alpha),
            new Vertex(x1,y1,1,1,1,1,1,1,alpha),
            new Vertex(x0,y1,1,0,1,1,1,1,alpha)
        };
        _context!.UpdateSubresource(vertices, _vertices!);
        _context.PSSetShaderResource(0, image.View);
        _context.Draw(6, 0);
    }

    private void EndTransition(bool reportCompletion)
    {
        if (_next is null) return;
        var seconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        if (seconds > 0.01)
            PCasaDiagnostics.WriteMessage("Slideshow.Direct3D11.Frames",
                $"effect={_transition}; frames={_drawnTransitionFrames}; " +
                $"seconds={seconds:F2}; fps={_drawnTransitionFrames / seconds:F1}; " +
                $"completed={reportCompletion}");
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
