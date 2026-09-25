using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Frameglass;

/// <summary>Animated presentation surface for live overlay editing; its FPS is not a game benchmark.</summary>
public sealed class OverlayTestWindow : Window
{
    public OverlayTestWindow()
    {
        Title = "Frame Trace · Overlay test";
        Width = 900; Height = 520; MinWidth = 480; MinHeight = 300;
        Background = new SolidColorBrush(Color.FromRgb(15, 23, 33));
        Icon = System.Windows.Application.Current.MainWindow.Icon;
        Grid scene = new() { Margin = new Thickness(32), ClipToBounds = true };
        StackPanel explanation = new();
        explanation.Children.Add(new TextBlock { Text = "FRAME TRACE / OVERLAY TEST", FontSize = 24, Foreground = Brushes.Aquamarine, FontWeight = FontWeights.Bold });
        explanation.Children.Add(new TextBlock { Text = "Keep this window open and edit in Overlay Studio.\nChanges appear on your selected display immediately.\nSave & apply keeps them for your games.", Foreground = Brushes.White, FontSize = 16, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap });
        explanation.Children.Add(new TextBlock { Text = "Live Frame Trace rendering + real hardware readings.\nThis is a preview, not a game performance benchmark.\nClose this window or choose Stop test to return to game detection.", Foreground = Brushes.LightSlateGray, FontSize = 14, Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap });
        scene.Children.Add(explanation);
        TranslateTransform motion = new();
        Rectangle bar = new() { Width = 120, Height = 8, Fill = Brushes.Aquamarine, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40), RenderTransform = motion };
        scene.Children.Add(bar); Content = scene;
        Loaded += (_, _) => motion.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 280, TimeSpan.FromSeconds(2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        Closed += (_, _) => motion.BeginAnimation(TranslateTransform.XProperty, null);
    }
}
