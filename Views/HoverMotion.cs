using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace EveCommandCenter.Views;

public static class HoverMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(HoverMotion), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.MouseEnter -= Enter; element.MouseLeave -= Leave;
        if ((bool)args.NewValue) { element.RenderTransformOrigin = new System.Windows.Point(.5, .5); element.RenderTransform = new ScaleTransform(1, 1); element.MouseEnter += Enter; element.MouseLeave += Leave; }
    }
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e) => Animate((FrameworkElement)sender, 1.015);
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) => Animate((FrameworkElement)sender, 1);
    private static void Animate(FrameworkElement element, double target)
    {
        if (element.RenderTransform is not ScaleTransform scale) return;
        if (!SystemParameters.ClientAreaAnimation) target = 1;
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
