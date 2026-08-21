using Avalonia.Controls;
using Libreguard.Vpn.Linux.ViewModels;

namespace Libreguard.Vpn.Linux.Views;

public sealed partial class TwoFactorSetupWindow : Window
{
    private TwoFactorSetupDialogViewModel? _viewModel;

    public TwoFactorSetupWindow()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
    }

    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= HandleCloseRequested;
        }

        _viewModel = DataContext as TwoFactorSetupDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += HandleCloseRequested;
        }
    }

    private void HandleCloseRequested(object? sender, bool result)
        => Close(result);
}
