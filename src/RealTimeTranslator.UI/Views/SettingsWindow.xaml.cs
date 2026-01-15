using System.Windows;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// SettingsWindow.xaml の相互作用ロジック
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.SettingsSaved += OnSettingsSaved;
        Closed += OnClosed;
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        _viewModel.SettingsSaved -= OnSettingsSaved;
        Closed -= OnClosed;
    }
}
