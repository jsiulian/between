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

    /// <summary>Programmatically selects the Notifications tab (badge deep-link).</summary>
    public void SelectNotificationsTab()
    {
        if (this.FindControl<TabControl>("MainTabs") is { } tabs)
            tabs.SelectedIndex = 2; // Contacts=0, Chat=1, Notifications=2
    }

    /// <summary>Enter to send in the chat box.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
