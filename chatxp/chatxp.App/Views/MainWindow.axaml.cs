using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using chatxp.App.ViewModels;

namespace chatxp.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Enter to send in the chat box (Shift+Enter for future multiline).</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
