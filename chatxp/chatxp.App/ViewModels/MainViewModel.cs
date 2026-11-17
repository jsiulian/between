using System.Collections.ObjectModel;
using chatxp.core;
using chatxp.core.Storage;
using chatxp.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace chatxp.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ChatSessionService _session;

    [ObservableProperty] private string _myId = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _status = "offline";
    [ObservableProperty] private string _signalServer = "";
    [ObservableProperty] private bool _applyingServer;
    [ObservableProperty] private bool _peerIsTyping;
    [ObservableProperty] private string _chatHeader = "Chat";
    [ObservableProperty] private bool _hasActiveChat;
    [ObservableProperty] private bool _isNewContactFormOpen;
    [ObservableProperty] private bool _isEditContactFormOpen;

    // Add/Edit contact form fields (shared form state).
    [ObservableProperty] private string _formNickname = "";
    [ObservableProperty] private string _formPeerId = "";
    [ObservableProperty] private string _formServer = "";
    [ObservableProperty] private string _editingContactId = "";
    [ObservableProperty] private string _formError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnected;

    /// <summary>Footer connection status text. Notified via IsConnected.</summary>
    public string ConnectionLabel => IsConnected ? "encrypted peer-to-peer" : "offline";

    /// <summary>Chat lines of the active conversation (plaintext, display only).</summary>
    public ObservableCollection<ChatLine> Lines { get; } = new();

    /// <summary>All contacts (nickname + full key kept internally for reconnects).</summary>
    public ObservableCollection<ContactView> Contacts { get; } = new();

    /// <summary>Pending contact requests — cannot be dismissed without a decision.</summary>
    public ObservableCollection<chatxp.App.Services.PendingRequestView> Requests { get; } = new();

    /// <summary>Diagnostic log lines.</summary>
    public ObservableCollection<string> LogLines => _logView;
    private readonly ObservableCollection<string> _logView = new();

    public MainViewModel()
    {
        _session = new ChatSessionService();
        MyId = _session.Crypto.Base64PublicKey;
        SignalServer = _session.SignalingServer;

        void OnUi(Action action) =>
            Avalonia.Threading.Dispatcher.UIThread.Invoke(action);

        _session.WebRtc.OnConnectionStateChanged += state => OnUi(() =>
        {
            Status = state switch
            {
                "connected" => "Connected",
                "connecting" => "Connecting…",
                "failed" => "Failed",
                "closed" => "Closed",
                _ => state,
            };
            IsConnected = _session.WebRtc.IsPeerConnected;
        });

        _session.WebRtc.OnDisconnected += reason => OnUi(() =>
        {
            _logView.Add($"[{DateTimeOffset.Now:HH:mm:ss}] ENDED: {reason}");
            HasActiveChat = false;
            ChatHeader = "Chat";
        });

        // Contact requests land in the Notifications area and STAY until decided.
        _session.ContactRequestReceived += peerId => OnUi(() =>
        {
            if (Requests.All(r => r.PeerId != peerId))
                Requests.Add(new PendingRequestView(peerId));
            Status = "New contact request — see Notifications";
        });

        // When the auto-accept path (known contact) connects, open the chat.
        _session.MessageReceived += tuple => OnUi(() =>
        {
            // Find the contact nickname for the chat header.
            var peer = tuple.row.From == _session.Crypto.Base64PublicKey
                ? tuple.row.To : tuple.row.From;
            var nickname = _session.NicknameFor(peer) ?? ShortId(peer);
            ChatHeader = $"Chat with {nickname}";
            HasActiveChat = true;
            Lines.Add(new ChatLine(tuple.row.Id, tuple.row.EventId, tuple.plaintext,
                false, tuple.row.TimestampUnixMs, tuple.row.ReplyingTo));
            PeerIsTyping = false;
        });

        _session.MessageDeleted += eventId => OnUi(() =>
        {
            var line = Lines.FirstOrDefault(l => l.EventId == eventId);
            if (line is not null) Lines.Remove(line);
        });

        _session.PeerTyping += () => OnUi(() =>
        {
            PeerIsTyping = true;
            _ = ResetTypingAfterDelayAsync();
        });

        // Diagnostics bridge.
        foreach (var existing in _session.Log.Entries)
            _logView.Add(FormatLogEntry(existing));
        _session.Log.Added += entry => OnUi(() =>
        {
            _logView.Add(FormatLogEntry(entry));
            if (entry.Level == "ERROR")
                Status = "Error — see Diagnostics tab";
        });

        RefreshContacts();
    }

    private void RefreshContacts()
    {
        Contacts.Clear();
        foreach (var c in _session.GetAllContacts())
            Contacts.Add(new ContactView(c.WebRtcId, c.HumanName, ShortId(c.WebRtcId),
                c.SignalingServer));
    }

    private static string FormatLogEntry(chatxp.App.Services.DiagnosticLog.Entry entry) =>
        $"[{entry.Time:HH:mm:ss}] {(entry.Level == "ERROR" ? "ERROR" : "INFO ")}: {entry.Message}";

    private static string ShortId(string id) =>
        id.Length <= 16 ? id : id[..10] + "…" + id[^6..];

    private async Task ResetTypingAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        PeerIsTyping = false;
    }

    // ------------------------------------------------------------ contacts

    [RelayCommand]
    private void OpenAddContact()
    {
        FormNickname = "";
        FormPeerId = "";
        FormServer = _session.SignalingServer; // sensible default: current server
        FormError = "";
        IsNewContactFormOpen = true;
        IsEditContactFormOpen = false;
    }

    [RelayCommand]
    private void CancelContactForm()
    {
        IsNewContactFormOpen = false;
        IsEditContactFormOpen = false;
        FormError = "";
    }

    [RelayCommand]
    private void SaveNewContact()
    {
        if (string.IsNullOrWhiteSpace(FormNickname) || string.IsNullOrWhiteSpace(FormPeerId))
        {
            FormError = "Nickname and peer ID are both required.";
            return;
        }
        if (!IsServerBlankOrValid(FormServer, out var serverError))
        {
            FormError = serverError;
            return;
        }
        try
        {
            _session.AddContact(FormNickname, FormPeerId.Trim(),
                string.IsNullOrWhiteSpace(FormServer) ? null : FormServer.Trim());
            IsNewContactFormOpen = false;
            RefreshContacts();
        }
        catch (Exception ex)
        {
            FormError = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenEditContact(string peerId)
    {
        var contact = _session.GetAllContacts().FirstOrDefault(c => c.WebRtcId == peerId);
        if (contact is null) return;
        EditingContactId = peerId;
        FormNickname = contact.HumanName;
        FormServer = contact.SignalingServer ?? "";
        FormError = "";
        IsEditContactFormOpen = true;
        IsNewContactFormOpen = false;
    }

    [RelayCommand]
    private void SaveEditContact()
    {
        if (string.IsNullOrWhiteSpace(FormNickname))
        {
            FormError = "Nickname cannot be empty.";
            return;
        }
        if (!IsServerBlankOrValid(FormServer, out var serverError))
        {
            FormError = serverError;
            return;
        }
        _session.EditContact(EditingContactId, FormNickname,
            string.IsNullOrWhiteSpace(FormServer) ? null : FormServer.Trim());
        IsEditContactFormOpen = false;
        RefreshContacts();
        if (_session.ConnectedPeerId == EditingContactId)
            ChatHeader = $"Chat with {FormNickname}";
    }

    /// <summary>Server field is optional (blank = default) but must be a ws:// URL if given.</summary>
    private static bool IsServerBlankOrValid(string server, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(server)) return true;
        var trimmed = server.Trim();
        if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            error = "Signaling server must start with ws:// or wss:// (or be left blank for the default).";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void DeleteContact(string peerId)
    {
        _session.DeleteContact(peerId);
        RefreshContacts();
    }

    [RelayCommand]
    private async Task ConnectToContact(string peerId)
    {
        var nickname = _session.NicknameFor(peerId) ?? ShortId(peerId);
        ChatHeader = $"Chat with {nickname}";
        HasActiveChat = true;
        Lines.Clear();
        // Load stored (encrypted) history and decrypt for display.
        foreach (var (row, plaintext) in _session.LoadHistory(peerId))
            Lines.Add(new ChatLine(row.Id, row.EventId, plaintext,
                row.From == _session.Crypto.Base64PublicKey,
                row.TimestampUnixMs, row.ReplyingTo));
        Status = "Connecting…";
        await _session.ConnectToContactAsync(peerId);
    }

    // ------------------------------------------------------------ requests

    [RelayCommand]
    private void ShowNotifications()
    {
        // Deep-link to the Notifications tab (TabItem #2).
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            (desktop.MainWindow as Views.MainWindow)?.SelectNotificationsTab();
        }
    }

    [RelayCommand]
    private async Task AcceptRequest(string peerId)
    {
        Requests.Remove(Requests.FirstOrDefault(r => r.PeerId == peerId)!);
        // First-time accept: nickname unknown — use a default and let the user
        // rename later from Contacts. (Requirement: contact exists to resume chats.)
        var nickname = _session.NicknameFor(peerId) ?? $"Contact-{ShortId(peerId)}";
        _session.AcceptContactRequest(peerId, nickname);
        RefreshContacts();
        ChatHeader = $"Chat with {nickname}";
        HasActiveChat = true;
        Lines.Clear();
        foreach (var (row, plaintext) in _session.LoadHistory(peerId))
            Lines.Add(new ChatLine(row.Id, row.EventId, plaintext,
                row.From == _session.Crypto.Base64PublicKey,
                row.TimestampUnixMs, row.ReplyingTo));
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void IgnoreRequest(string peerId)
    {
        Requests.Remove(Requests.FirstOrDefault(r => r.PeerId == peerId)!);
        _session.IgnoreContactRequest(peerId);
        Status = "Request ignored";
    }

    // ------------------------------------------------------------ chat

    [RelayCommand]
    private void Send()
    {
        if (string.IsNullOrWhiteSpace(InputText) || !IsConnected) return;
        var result = _session.SendText(InputText.Trim());
        if (result is not null)
        {
            var (evt, localId) = result.Value;
            Lines.Add(new ChatLine(localId, evt.Id, InputText.Trim(),
                true, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), evt.ReplyingTo));
            InputText = "";
        }
    }

    [RelayCommand]
    private void TypingIndicator()
    {
        if (IsConnected)
            _session.WebRtc.Send(Event.TypingEvent());
    }

    // ------------------------------------------------------------ misc

    [RelayCommand]
    private async Task CopyMyIdAsync()
    {
        try
        {
            var clipboard = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Clipboard;

            if (clipboard is null)
            {
                Status = "Clipboard unavailable";
                return;
            }

            var transfer = new Avalonia.Input.DataTransfer();
            var item = new Avalonia.Input.DataTransferItem();
            item.SetText(MyId);
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
            Status = "ID copied to clipboard";
        }
        catch (Exception ex)
        {
            Status = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyServerAsync()
    {
        if (ApplyingServer) return;
        ApplyingServer = true;
        try
        {
            var error = await _session.ApplySignalingServerAsync(SignalServer);
            Status = error ?? $"Signaling server set to {ShortId(SignalServer)}";
        }
        finally
        {
            ApplyingServer = false;
        }
    }

    [RelayCommand]
    private void ClearLog() => _logView.Clear();

    public void Dispose() => _session.Dispose();
}
