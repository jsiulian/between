using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using chatxp.App.ViewModels;

namespace chatxp.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _wired;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    /// <summary>Programmatically selects the Notifications tab (badge deep-link).</summary>
    public void SelectNotificationsTab()
    {
        if (this.FindControl<TabControl>("MainTabs") is { } tabs)
            tabs.SelectedIndex = 1; // Contacts=0, Notifications=1, Settings=2, Diagnostics=3
    }

    /// <summary>Enter to send in the chat box.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- scrolling

    // Wires AFTER the window is shown: at that point the data context exists and
    // (importantly) the chat overlay's controls are realized — scrolling works.
    private void OnOpened(object? sender, EventArgs e)
    {
        if (_wired) return;
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;

        _vm.Lines.CollectionChanged += OnLinesChanged;
        _vm.PropertyChanged += (s, args) =>
        {
            // Chat surface became visible (open/reopen): scroll history to bottom.
            if (args.PropertyName == nameof(MainViewModel.HasActiveChat) && _vm.HasActiveChat)
                ScrollMessagesToBottom();
        };
        // If the chat was somehow already open at startup, scroll once.
        if (_vm.HasActiveChat)
            ScrollMessagesToBottom();
        _wired = true;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            ScrollMessagesToBottom();
    }

    /// <summary>Scrolls the messages list to the newest line. Deferred to the next
    /// layout pass so newly added/realized items have size before we scroll.</summary>
    private void ScrollMessagesToBottom()
    {
        // Post at Loaded priority: runs after the pending layout pass gives the
        // new items their measured sizes, but before user-interactive work.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<ListBox>("MessagesList") is not { } list) return;
            if (list.ItemCount == 0) return;
            list.ScrollIntoView(list.ItemCount - 1);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }
}
