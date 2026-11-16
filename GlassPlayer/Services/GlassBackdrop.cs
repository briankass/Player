using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GlassPlayer.Services;

/// <summary>
/// Mica / Acrylic backdrop with adjustable tint and luminosity (the built-in MicaBackdrop and
/// DesktopAcrylicBackdrop don't expose these, and their defaults are nearly opaque).
/// Effect: 0 Mica, 1 Mica Alt, 2 Acrylic, 3 Acrylic thin.
/// </summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    public int Effect { get; }
    public bool KeepActive { get; }

    private object? _controller; // MicaController or DesktopAcrylicController
    private SystemBackdropConfiguration? _config;
    private Color _tint = Color.FromArgb(255, 32, 32, 32);
    private float _tintOpacity = 0.15f;
    private float _luminosity = 0.6f;

    public GlassBackdrop(int effect, bool keepActive)
    {
        Effect = effect;
        KeepActive = keepActive;
    }

    public void Update(Color tint, double tintOpacity, double luminosity)
    {
        _tint = tint;
        _tintOpacity = (float)Math.Clamp(tintOpacity, 0, 1);
        _luminosity = (float)Math.Clamp(luminosity, 0, 1);
        ApplyValues();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller != null) return;

        if (KeepActive)
        {
            var dark = (xamlRoot.Content as FrameworkElement)?.ActualTheme != ElementTheme.Light;
            _config = new SystemBackdropConfiguration
            {
                IsInputActive = true, // don't fall back to a flat colour when the window loses focus
                Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
            };
        }
        else
        {
            _config = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        }

        switch (Effect)
        {
            case 0:
            case 1:
                var mica = new MicaController { Kind = Effect == 1 ? MicaKind.BaseAlt : MicaKind.Base };
                mica.SetSystemBackdropConfiguration(_config);
                _controller = mica;
                ApplyValues();
                mica.AddSystemBackdropTarget(connectedTarget);
                break;
            default:
                var acrylic = new DesktopAcrylicController { Kind = Effect == 3 ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
                acrylic.SetSystemBackdropConfiguration(_config);
                _controller = acrylic;
                ApplyValues();
                acrylic.AddSystemBackdropTarget(connectedTarget);
                break;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        switch (_controller)
        {
            case MicaController m:
                m.RemoveSystemBackdropTarget(disconnectedTarget);
                m.Dispose();
                break;
            case DesktopAcrylicController a:
                a.RemoveSystemBackdropTarget(disconnectedTarget);
                a.Dispose();
                break;
        }
        _controller = null;
    }

    private void ApplyValues()
    {
        var fallback = Color.FromArgb(255, _tint.R, _tint.G, _tint.B);
        switch (_controller)
        {
            case MicaController m:
                m.TintColor = _tint;
                m.TintOpacity = _tintOpacity;
                m.LuminosityOpacity = _luminosity;
                m.FallbackColor = fallback;
                break;
            case DesktopAcrylicController a:
                a.TintColor = _tint;
                a.TintOpacity = _tintOpacity;
                a.LuminosityOpacity = _luminosity;
                a.FallbackColor = fallback;
                break;
        }
    }
}
