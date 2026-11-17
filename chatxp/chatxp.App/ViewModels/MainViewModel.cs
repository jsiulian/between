using System.Collections.ObjectModel;
using chatxp.core;
using chatxp.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace chatxp.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ChatSessionService _session;

    [ObservableProperty] private string _myId = "";
    [ObservableProperty] private string _connectToId = "";
    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _status = "offline";
    [ObservableProperty] private string _peerId = "";
    [ObservableProperty] private bool _peerIsTyping;
    [ObservableProperty] private bool _incomingVisible;
    [ObservableProperty] private string _signalServer = "";
    [ObservableProperty] private bool _applyingServer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnected;

    /// <summary>Footer connection status text. Notified via IsConnected.</summary>
    public string ConnectionLabel => IsConnected ? "encrypted peer-to-peer" : "offline";

    /// <summary>Chat lines of the active conversation (plaintext, for display only —
    /// decrypted content never touches the database).</summary>
    public ObservableCollection<ChatLine> Lines { get; } = new();

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
            _logView.Add($"[{DateTimeOffset.Now:HH:mm:ss}] ENDED: {reason}"));

        _session.WebRtc.OnIncomingConnection += (fromId, sdp) => OnUi(() =>
        {
            PeerId = fromId;
            Status = $"Incoming connection from {ShortId(fromId)} — accept?";
            IncomingVisible = true;
            _pendingIncoming = fromId;
        });

        _session.MessageReceived += tuple => OnUi(() =>
        {
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

        // Diagnostics: bridge the session's log sink into the UI (marshal to UI thread).
        foreach (var existing in _session.Log.Entries)
            _logView.Add(FormatLogEntry(existing));
        _session.Log.Added += entry => OnUi(() =>
        {
            _logView.Add(FormatLogEntry(entry));
            if (entry.Level == "ERROR")
                Status = "Error — see Diagnostics tab";
        });
    }

    private static string FormatLogEntry(chatxp.App.Services.DiagnosticLog.Entry entry) =>
        $"[{entry.Time:HH:mm:ss}] {(entry.Level == "ERROR" ? "ERROR" : "INFO ")}: {entry.Message}";

    /// <summary>Diagnostic log lines for the Diagnostics tab (plain strings).</summary>
    public ObservableCollection<string> LogLines => _logView;
    private readonly ObservableCollection<string> _logView = new();

    private string? _pendingIncoming;

    private static string ShortId(string id) =>
        id.Length <= 16 ? id : id[..10] + "…" + id[^6..];

    private async Task ResetTypingAfterDelayAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        PeerIsTyping = false;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ConnectToId)) return;
        PeerId = ConnectToId.Trim();
        _session.SetConnectedPeer(PeerId);
        Lines.Clear();
        Status = "Connecting…";
        await _session.ConnectToPeerAsync(PeerId);
    }

    [RelayCommand]
    private async Task AcceptIncomingAsync()
    {
        if (_pendingIncoming is null) return;
        _session.SetConnectedPeer(_pendingIncoming);
        Lines.Clear();
        PeerId = _pendingIncoming;
        IncomingVisible = false;
        await _session.WebRtc.AcceptIncomingAsync(_pendingIncoming);
        _pendingIncoming = null;
    }

    [RelayCommand]
    private Task RejectIncomingAsync()
    {
        if (_pendingIncoming is null) return Task.CompletedTask;
        var id = _pendingIncoming;
        _pendingIncoming = null;
        IncomingVisible = false;
        return _session.WebRtc.RejectIncomingAsync(id);
    }

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
    private async Task CopyMyIdAsync()
    {
        try
        {
            // IClipboard is NOT registered as an Application feature on desktop —
            // TryGetFeature returns null there. Resolve it from the TopLevel instead.
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

    [RelayCommand]
    private void TypingIndicator()
    {
        if (IsConnected)
            _session.WebRtc.Send(Event.TypingEvent());
    }

    public void Dispose() => _session.Dispose();
}
