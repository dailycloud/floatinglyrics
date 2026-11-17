using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace FloatingLyrics.Controls;

/// <summary>
/// A liquid-glass panel rendered with Win2D on top of the desktop wallpaper.
///
/// The optical part runs in a custom Direct2D pixel shader (Shaders/LiquidGlass.hlsl):
/// the slab has a quarter-circle bevel; light through the bevel is bent toward the centre and
/// split by wavelength over sixteen spectral samples, which gives real rainbow fringes on
/// high-contrast edges behind the rim, while the flat interior shows the frosted backdrop.
/// Bevel highlights, Fresnel brightening, inner shadow and a sweeping glint are computed
/// analytically from the shape's signed distance field.
///
/// Appearing is not a scale/fly-in: the glass is already in place and *materialises* —
/// the bevel grows inward from the rim, refraction, dispersion and frost ramp up, the rim
/// lights up and a glint sweeps across. Because the backdrop is an exact copy of the wallpaper,
/// at zero presence the glass is indistinguishable from the desktop itself.
///
/// The control is larger than the glass shape by <see cref="ShapeMargin"/> on every side so
/// the shadow is never clipped.
/// </summary>
public sealed partial class LiquidGlassSurface : Grid
{
    public const float ShapeMargin = 44f;
    private const double GlintDurationSeconds = 1.05;

    private static byte[]? shaderBytes;

    private readonly CanvasControl canvas;
    private readonly Stopwatch glintClock = new();
    private PixelShaderEffect? glassShader;
    private CanvasDevice? glassShaderDevice;
    private float presence = 1;

    public LiquidGlassSurface()
    {
        IsHitTestVisible = false;
        canvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = Color.FromArgb(0, 0, 0, 0),
            IsHitTestVisible = false
        };
        canvas.Draw += OnDraw;
        canvas.CreateResources += OnCreateResources;
        Children.Add(canvas);
        Unloaded += (_, _) => ReleaseShader();
    }

    // ---- shape -------------------------------------------------------------------------

    /// <summary>Glass shape width in DIPs (the control itself is wider by 2 × <see cref="ShapeMargin"/>).</summary>
    public float ShapeWidth { get; private set; } = 200;

    /// <summary>Glass shape height in DIPs.</summary>
    public float ShapeHeight { get; private set; } = 100;

    public float ShapeCornerRadius { get; set; } = 28;

    /// <summary>
    /// 0 = not there (pure wallpaper), 1 = fully materialised glass. Values slightly above 1
    /// come from spring overshoot and make the lens briefly "wobble".
    /// </summary>
    public float Presence
    {
        get => presence;
        set
        {
            // A fresh appearance starts the glint sweep.
            if (presence < 0.04f && value >= 0.04f && value > presence)
            {
                glintClock.Restart();
            }

            presence = value;
        }
    }

    /// <summary>True while a time-based effect (the glint) still needs new frames.</summary>
    public bool NeedsFrames => glintClock.IsRunning && glintClock.Elapsed.TotalSeconds < GlintDurationSeconds;

    // ---- material ----------------------------------------------------------------------

    /// <summary>Backdrop blur of the interior in DIPs.</summary>
    public float Blur { get; set; } = 14;

    /// <summary>0..1, strength of the lens bending at the rim.</summary>
    public float Refraction { get; set; } = 0.65f;

    /// <summary>0..1, wavelength split of the refraction (rainbow fringes on the rim).</summary>
    public float Dispersion { get; set; } = 0.35f;

    /// <summary>0..1, intensity of the bevel highlight and rim light.</summary>
    public float Specular { get; set; } = 0.8f;

    /// <summary>Gentle magnification of the flat interior (0 = none).</summary>
    public float Magnify { get; set; } = 0.018f;

    public Color Tint { get; set; } = Color.FromArgb(255, 18, 22, 30);

    /// <summary>0..1 opacity of <see cref="Tint"/>.</summary>
    public float TintOpacity { get; set; } = 0.22f;

    /// <summary>Saturation boost of the backdrop (1 = unchanged).</summary>
    public float Saturation { get; set; } = 1.45f;

    public float ShadowOpacity { get; set; } = 0.32f;

    // ---- light -------------------------------------------------------------------------

    /// <summary>Cursor light position relative to the glass shape's top-left (DIPs); null = off.</summary>
    public Vector2? LightPosition { get; set; }

    /// <summary>0..1 intensity of the cursor light (animated in/out by the owner).</summary>
    public float LightIntensity { get; set; }

    // ---- placement ---------------------------------------------------------------------

    /// <summary>Screen position (physical px) of the glass shape's top-left corner.</summary>
    public Vector2 ShapeScreenOrigin { get; set; }

    /// <summary>Physical pixels per DIP.</summary>
    public float RasterScale { get; set; } = 1;

    public void SetShapeSize(double width, double height)
    {
        var w = (float)Math.Max(8, width);
        var h = (float)Math.Max(8, height);
        if (Math.Abs(w - ShapeWidth) < 0.01f && Math.Abs(h - ShapeHeight) < 0.01f &&
            Math.Abs(Width - (w + 2 * ShapeMargin)) < 0.01)
        {
            return;
        }

        ShapeWidth = w;
        ShapeHeight = h;
        Width = w + 2 * ShapeMargin;
        Height = h + 2 * ShapeMargin;
    }

    public void Invalidate() => canvas.Invalidate();

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (args.Reason == CanvasCreateResourcesReason.NewDevice)
        {
            ReleaseShader();
            DesktopWallpaperService.Instance.OnDeviceLost();
        }
    }

    private void ReleaseShader()
    {
        glassShader?.Dispose();
        glassShader = null;
        glassShaderDevice = null;
    }

    private PixelShaderEffect? GetShader(CanvasDevice device)
    {
        if (glassShader is not null && ReferenceEquals(glassShaderDevice, device))
        {
            return glassShader;
        }

        ReleaseShader();
        shaderBytes ??= LoadShaderBytes();
        if (shaderBytes is null)
        {
            return null;
        }

        glassShader = new PixelShaderEffect(shaderBytes)
        {
            Source1Mapping = SamplerCoordinateMapping.Offset,
            Source2Mapping = SamplerCoordinateMapping.Offset,
            Source1BorderMode = EffectBorderMode.Hard,
            Source2BorderMode = EffectBorderMode.Hard
        };
        glassShaderDevice = device;
        return glassShader;
    }

    private static byte[]? LoadShaderBytes()
    {
        try
        {
            using var stream = typeof(LiquidGlassSurface).Assembly.GetManifestResourceStream("FloatingLyrics.Shaders.LiquidGlass.bin");
            if (stream is null)
            {
                DebugTrace.Write("LiquidGlass: shader resource missing");
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"LiquidGlass: shader load failed {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------------------------ draw

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            DrawGlass(sender, args.DrawingSession);
        }
        catch (Exception ex) when (sender.Device.IsDeviceLost(ex.HResult))
        {
            sender.Device.RaiseDeviceLost();
        }
        catch (Exception ex)
        {
            DebugTrace.Write($"LiquidGlass: draw failed {ex.Message}");
        }
    }

    private void DrawGlass(CanvasControl sender, CanvasDrawingSession ds)
    {
        var p = Math.Clamp(Presence, 0f, 1.25f);
        var visibility = Math.Clamp(p, 0f, 1f);
        if (visibility <= 0.002f)
        {
            return;
        }

        var w = ShapeWidth;
        var h = ShapeHeight;
        var shape = new Rect(ShapeMargin, ShapeMargin, w, h);
        var radius = Math.Min(ShapeCornerRadius, Math.Min(w, h) / 2f);
        var specular = Math.Clamp(Specular, 0f, 1f);

        using var geometry = CanvasGeometry.CreateRoundedRectangle(sender, shape, radius, radius);

        // The backdrop is an exact copy of the wallpaper, so the glass is invisible at p≈0 even
        // at full opacity; the quick opacity ramp only hides sub-pixel resampling differences.
        using var layer = ds.CreateLayer(SmoothStep(0f, 0.12f, p));

        DrawShadow(sender, ds, geometry, visibility);
        DrawOptics(sender, ds, shape, radius, p, visibility);

        using (ds.CreateLayer(1f, geometry))
        {
            // Soft vertical sheen: light from above, a touch of depth at the bottom.
            using (var sheen = new CanvasLinearGradientBrush(
                       sender,
                       [
                           new CanvasGradientStop { Position = 0f, Color = Color.FromArgb((byte)(26 * specular * visibility), 255, 255, 255) },
                           new CanvasGradientStop { Position = 0.38f, Color = Color.FromArgb(0, 255, 255, 255) },
                           new CanvasGradientStop { Position = 1f, Color = Color.FromArgb((byte)(14 * visibility), 0, 0, 0) }
                       ])
                   {
                       StartPoint = new Vector2(0, (float)shape.Y),
                       EndPoint = new Vector2(0, (float)(shape.Y + h))
                   })
            {
                ds.FillGeometry(geometry, sheen);
            }

            DrawCursorLight(sender, ds, geometry, shape);
        }

        DrawRim(sender, ds, shape, radius, specular * visibility);
    }

    private void DrawShadow(CanvasControl sender, CanvasDrawingSession ds, CanvasGeometry geometry, float visibility)
    {
        if (ShadowOpacity <= 0.001f)
        {
            return;
        }

        using var silhouette = new CanvasCommandList(sender);
        using (var cds = silhouette.CreateDrawingSession())
        {
            cds.FillGeometry(geometry, Colors.Black);
        }

        var alpha = (byte)Math.Clamp(ShadowOpacity * 255f * visibility, 0, 255);
        using var broad = new ShadowEffect { Source = silhouette, BlurAmount = 18f * (0.4f + (0.6f * visibility)), ShadowColor = Color.FromArgb(alpha, 0, 0, 0) };
        using var contact = new ShadowEffect { Source = silhouette, BlurAmount = 4f, ShadowColor = Color.FromArgb((byte)(alpha * 0.45f), 0, 0, 0) };

        // The shadow must not darken the glass itself: draw it outside the shape only.
        using var bounds = CanvasGeometry.CreateRectangle(sender, 0, 0, (float)Width, (float)Height);
        using var outside = bounds.CombineWith(geometry, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
        using (ds.CreateLayer(1f, outside))
        {
            ds.DrawImage(broad, 0, 12f * visibility);
            ds.DrawImage(contact, 0, 2);
        }
    }

    private void DrawOptics(CanvasControl sender, CanvasDrawingSession ds, Rect shape, float radius, float p, float visibility)
    {
        var wallpaper = DesktopWallpaperService.Instance.Bitmap;
        var shader = GetShader(sender.Device);
        if (wallpaper is null || wallpaper.Device != sender.Device || shader is null)
        {
            // No wallpaper yet: neutral frosted fill so the panel never looks broken.
            using var fallback = CanvasGeometry.CreateRoundedRectangle(sender, shape, radius, radius);
            ds.FillGeometry(fallback, Color.FromArgb((byte)(150 * visibility), 46, 54, 68));
            return;
        }

        var dipToPx = sender.Dpi / 96f;
        var scale = Math.Max(0.25f, RasterScale);
        var origin = DesktopWallpaperService.Instance.Origin;

        // Wallpaper pixels → control DIPs. The glass shape's top-left in control space is
        // (ShapeMargin, ShapeMargin), and it sits at ShapeScreenOrigin on screen.
        var toLocal =
            Matrix3x2.CreateTranslation(origin.X - ShapeScreenOrigin.X, origin.Y - ShapeScreenOrigin.Y) *
            Matrix3x2.CreateScale(1f / scale) *
            Matrix3x2.CreateTranslation(ShapeMargin, ShapeMargin);

        using var aligned = new Transform2DEffect
        {
            Source = wallpaper,
            TransformMatrix = toLocal,
            InterpolationMode = CanvasImageInterpolation.Linear,
            BorderMode = EffectBorderMode.Hard
        };

        var saturation = 1f + ((Saturation - 1f) * visibility);
        using var blurred = new GaussianBlurEffect
        {
            Source = aligned,
            BlurAmount = Math.Max(0.01f, Math.Max(0f, Blur) * visibility),
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced
        };
        using var frosted = new ColorMatrixEffect
        {
            Source = blurred,
            ColorMatrix = BuildSaturationMatrix(saturation, 0.03f * visibility),
            AlphaMode = CanvasAlphaMode.Premultiplied,
            ClampOutput = true
        };
        // "Sharp" rim source gets a whisper of blur: steep lensing magnifies it, and a perfectly
        // sharp source would show stair-stepping and ghost copies between spectral samples.
        using var softened = new GaussianBlurEffect
        {
            Source = aligned,
            BlurAmount = 1.4f,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Speed
        };
        using var sharp = new ColorMatrixEffect
        {
            Source = softened,
            ColorMatrix = BuildSaturationMatrix(1f + ((saturation - 1f) * 0.6f), 0.015f * visibility),
            AlphaMode = CanvasAlphaMode.Premultiplied,
            ClampOutput = true
        };

        // The bevel grows inward from the rim while the glass materialises ("faces appear").
        var fullBevel = Math.Clamp((radius * 0.95f) + 8f, 10f, Math.Min(ShapeWidth, ShapeHeight) * 0.45f);
        var grow = 1f - ((1f - Math.Clamp(p, 0f, 1f)) * (1f - Math.Clamp(p, 0f, 1f)));
        var bevel = fullBevel * Math.Max(0.02f, grow + Math.Max(0f, p - 1f));

        var refraction = Math.Clamp(Refraction, 0f, 1f);
        var maxShift = Math.Clamp(Math.Min(ShapeWidth, ShapeHeight) * 0.24f, 14f, 60f) * refraction * p;
        var dispersion = Math.Clamp(Dispersion, 0f, 1f) * visibility;
        var magnify = Magnify * refraction * p;

        var glint = -2f;
        if (NeedsFrames)
        {
            var t = (float)(glintClock.Elapsed.TotalSeconds / GlintDurationSeconds);
            glint = -0.3f + (1.6f * EaseInOut(t));
        }

        var tint = Tint;
        var lightDir = Vector2.Normalize(new Vector2(0.62f, 0.78f));

        shader.Source1 = frosted;
        shader.Source2 = sharp;
        var reach = (maxShift * (1f + (0.73f * dispersion))) + (magnify * Math.Max(ShapeWidth, ShapeHeight) * 0.5f);
        var maxOffset = (int)Math.Ceiling(reach * dipToPx) + 4;
        shader.MaxSamplerOffset = maxOffset;
        shader.Properties["shapeOrigin"] = new Vector2((float)shape.X * dipToPx, (float)shape.Y * dipToPx);
        shader.Properties["shapeSize"] = new Vector2(ShapeWidth * dipToPx, ShapeHeight * dipToPx);
        shader.Properties["radius"] = radius * dipToPx;
        shader.Properties["bevel"] = bevel * dipToPx;
        shader.Properties["refraction"] = maxShift * dipToPx;
        shader.Properties["dispersion"] = dispersion;
        shader.Properties["magnify"] = magnify;
        shader.Properties["rimClarity"] = 0.8f;
        shader.Properties["specular"] = Math.Clamp(Specular, 0f, 1f) * visibility;
        shader.Properties["glint"] = glint;
        shader.Properties["saturationBoost"] = 0.35f * visibility;
        shader.Properties["tint"] = new Vector4(tint.R / 255f, tint.G / 255f, tint.B / 255f, Math.Clamp(TintOpacity, 0f, 1f) * visibility);
        shader.Properties["lightDir"] = lightDir;

        using (ds.CreateLayer(1f, shape))
        {
            ds.DrawImage(shader);
        }
    }

    private void DrawCursorLight(CanvasControl sender, CanvasDrawingSession ds, CanvasGeometry geometry, Rect shape)
    {
        if (LightPosition is not { } light || LightIntensity <= 0.01f)
        {
            return;
        }

        var center = new Vector2((float)shape.X, (float)shape.Y) + light;
        var intensity = Math.Clamp(LightIntensity, 0f, 1f);
        var radius = Math.Clamp((float)Math.Max(shape.Width, shape.Height) * 0.42f, 120f, 320f);
        using var glow = new CanvasRadialGradientBrush(
            sender,
            Color.FromArgb((byte)(28 * intensity), 255, 255, 255),
            Color.FromArgb(0, 255, 255, 255))
        {
            Center = center,
            RadiusX = radius,
            RadiusY = radius
        };
        ds.FillGeometry(geometry, glow);
    }

    private void DrawRim(CanvasControl sender, CanvasDrawingSession ds, Rect shape, float radius, float a)
    {
        var x = (float)shape.X;
        var y = (float)shape.Y;
        var w = (float)shape.Width;
        var h = (float)shape.Height;

        // Hairline that separates the glass from very bright wallpapers.
        using (var outer = CanvasGeometry.CreateRoundedRectangle(sender, x - 0.5f, y - 0.5f, w + 1f, h + 1f, radius + 0.5f, radius + 0.5f))
        {
            ds.DrawGeometry(outer, Color.FromArgb((byte)(26 * Math.Min(1f, a + 0.2f)), 0, 0, 0), 1f);
        }

        // Directional rim: bright where the light hits (top-left) and its reflection (bottom-right).
        using (var rimBrush = new CanvasLinearGradientBrush(
                   sender,
                   [
                       new CanvasGradientStop { Position = 0f, Color = Color.FromArgb((byte)(230 * a), 255, 255, 255) },
                       new CanvasGradientStop { Position = 0.22f, Color = Color.FromArgb((byte)(80 * a), 255, 255, 255) },
                       new CanvasGradientStop { Position = 0.5f, Color = Color.FromArgb((byte)(34 * a), 255, 255, 255) },
                       new CanvasGradientStop { Position = 0.8f, Color = Color.FromArgb((byte)(64 * a), 255, 255, 255) },
                       new CanvasGradientStop { Position = 1f, Color = Color.FromArgb((byte)(160 * a), 255, 255, 255) }
                   ])
               {
                   StartPoint = new Vector2(x, y),
                   EndPoint = new Vector2(x + w, y + h)
               })
        using (var rim = CanvasGeometry.CreateRoundedRectangle(sender, x + 0.6f, y + 0.6f, w - 1.2f, h - 1.2f, radius - 0.6f, radius - 0.6f))
        {
            ds.DrawGeometry(rim, rimBrush, 1.1f);
        }

        // Light catching the rim near the cursor.
        if (LightPosition is { } light && LightIntensity > 0.01f)
        {
            var center = new Vector2(x, y) + light;
            using var catchBrush = new CanvasRadialGradientBrush(
                sender,
                Color.FromArgb((byte)(255 * Math.Clamp(LightIntensity, 0f, 1f)), 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255))
            {
                Center = center,
                RadiusX = 150,
                RadiusY = 150
            };
            using var rim = CanvasGeometry.CreateRoundedRectangle(sender, x + 0.6f, y + 0.6f, w - 1.2f, h - 1.2f, radius - 0.6f, radius - 0.6f);
            ds.DrawGeometry(rim, catchBrush, 1.6f);
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static float EaseInOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t < 0.5f ? 4f * t * t * t : 1f - (MathF.Pow((-2f * t) + 2f, 3f) / 2f);
    }

    private static Matrix5x4 BuildSaturationMatrix(float saturation, float lift)
    {
        const float lr = 0.2126f;
        const float lg = 0.7152f;
        const float lb = 0.0722f;
        var s = saturation;
        var inv = 1f - s;
        return new Matrix5x4
        {
            M11 = (inv * lr) + s, M12 = inv * lr, M13 = inv * lr, M14 = 0,
            M21 = inv * lg, M22 = (inv * lg) + s, M23 = inv * lg, M24 = 0,
            M31 = inv * lb, M32 = inv * lb, M33 = (inv * lb) + s, M34 = 0,
            M41 = 0, M42 = 0, M43 = 0, M44 = 1,
            M51 = lift, M52 = lift, M53 = lift, M54 = 0
        };
    }
}
