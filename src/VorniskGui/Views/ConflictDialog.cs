using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VorniskCli.Core;

namespace VorniskGui.Views;

/// <summary>
/// "Ask" conflict modal: destination exists → Skip / Overwrite / Rename, with Apply-to-all.
/// Built in code (small, static layout — no .axaml needed). Returns null when closed with X;
/// the caller maps that to Skip.
/// </summary>
public sealed class ConflictDialog : Window
{
    public ConflictDialog(string destPath)
    {
        Title = "File exists";
        Width = 460; SizeToContent = SizeToContent.Height;
        MinHeight = 170;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E293B"));

        var applyAll = new CheckBox { Content = "Apply to all remaining conflicts", Foreground = Brushes.Gainsboro, Margin = new Thickness(0, 8, 0, 0) };

        Button Make(string text, ConflictMode mode, bool accent = false)
        {
            var b = new Button { Content = text, MinWidth = 92, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            if (accent) b.Classes.Add("accent");
            b.Click += (_, _) => Close(new ConflictDecision(mode, applyAll.IsChecked == true));
            return b;
        }

        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "The destination file already exists:", Foreground = Brushes.White, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = destPath, Foreground = new SolidColorBrush(Color.Parse("#94A3B8")), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                applyAll,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { Make("Skip", ConflictMode.Skip, accent: true), Make("Overwrite", ConflictMode.Overwrite), Make("Rename", ConflictMode.Rename) }
                },
            }
        };
    }
}
