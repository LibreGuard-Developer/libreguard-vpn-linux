using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Libreguard.Vpn.Linux.Views;

public sealed partial class ExitConfirmationWindow : Window
{
    public ExitConfirmationWindow()
    {
        InitializeComponent();
    }

    private void HandleCancelClick(object? sender, RoutedEventArgs e)
        => Close(false);

    private void HandleConfirmClick(object? sender, RoutedEventArgs e)
        => Close(true);
}
