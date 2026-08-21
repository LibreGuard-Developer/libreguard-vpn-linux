using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Libreguard.Vpn.Linux.Controls;

public static class RippleBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(RippleBehavior));

    public static bool GetIsEnabled(Control element)
        => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    static RippleBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>(HandleIsEnabledChanged);
    }

    private static void HandleIsEnabledChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.NewValue is true)
        {
            control.PointerPressed += HandlePointerPressed;
        }
        else
        {
            control.PointerPressed -= HandlePointerPressed;
        }
    }

    private static void HandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !control.IsEffectivelyEnabled)
        {
            return;
        }

        var overlay = FindRippleOverlay(control);
        if (overlay is null || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
        {
            return;
        }

        var origin = control.TranslatePoint(new Point(0, 0), overlay);
        if (origin is null)
        {
            return;
        }

        var pointer = e.GetPosition(control);
        var diameter = Math.Max(control.Bounds.Width, control.Bounds.Height) * 2.1;
        var host = new Canvas
        {
            Width = control.Bounds.Width,
            Height = control.Bounds.Height,
            ClipToBounds = true,
            IsHitTestVisible = false
        };

        var rippleScale = new ScaleTransform(0.08, 0.08);
        var ripple = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Opacity = 0.28,
            Fill = ResolveRippleBrush(control),
            RenderTransform = rippleScale,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            IsHitTestVisible = false
        };

        Canvas.SetLeft(host, origin.Value.X);
        Canvas.SetTop(host, origin.Value.Y);
        Canvas.SetLeft(ripple, pointer.X - diameter / 2.0);
        Canvas.SetTop(ripple, pointer.Y - diameter / 2.0);

        host.Children.Add(ripple);
        overlay.Children.Add(host);
        AnimateRipple(overlay, host, ripple, rippleScale);
    }

    private static Canvas? FindRippleOverlay(Control control)
        => TopLevel.GetTopLevel(control)?.FindControl<Canvas>("RippleOverlayLayer");

    private static IBrush ResolveRippleBrush(Control control)
    {
        if (control.Classes.Contains("primary") ||
            control.Classes.Contains("destructive") ||
            control.Classes.Contains("nav"))
        {
            return Brushes.White;
        }

        return new SolidColorBrush(Color.FromRgb(21, 112, 239));
    }

    private static void AnimateRipple(Canvas overlay, Canvas host, Ellipse ripple, ScaleTransform transform)
    {
        const double durationMilliseconds = 360.0;
        var startedAt = DateTimeOffset.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };

        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / durationMilliseconds, 0.0, 1.0);
            var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);

            transform.ScaleX = eased;
            transform.ScaleY = eased;
            ripple.Opacity = 0.28 * (1.0 - progress);

            if (progress < 1.0)
            {
                return;
            }

            timer.Stop();
            overlay.Children.Remove(host);
        };

        timer.Start();
    }
}
