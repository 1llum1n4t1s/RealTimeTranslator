using Avalonia.Controls;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// 設定ウィンドウ
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel? _viewModel;

    /// <summary>
    /// XAML ランタイムローダー用のパラメータなしコンストラクタ
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = null;
    }

    /// <summary>
    /// 実行時用コンストラクタ
    /// </summary>
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

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.SettingsSaved -= OnSettingsSaved;
        }
        Closed -= OnClosed;
    }
}
