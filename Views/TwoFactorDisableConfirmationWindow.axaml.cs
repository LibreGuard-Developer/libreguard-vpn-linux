using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Libreguard.Vpn.Linux.Views;

public sealed partial class TwoFactorDisableConfirmationWindow : Window
{
    public TwoFactorDisableConfirmationWindow()
    {
        InitializeComponent();
    }

    private void HandleCancelClick(object? sender, RoutedEventArgs e)
        => Close(false);

    private void HandleConfirmClick(object? sender, RoutedEventArgs e)
        => Close(true);
}
