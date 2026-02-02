using Avalonia.Controls;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// メインウィンドウ
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
