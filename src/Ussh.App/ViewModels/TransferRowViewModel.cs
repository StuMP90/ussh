using CommunityToolkit.Mvvm.ComponentModel;
using Ussh.Core.Files;

namespace Ussh.App.ViewModels;

/// <summary>One line in the transfer queue, refreshed on a timer from its <see cref="TransferItem"/>.</summary>
public sealed partial class TransferRowViewModel : ObservableObject
{
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _speed;

    public TransferRowViewModel(TransferItem item)
    {
        Item = item;
        Refresh();
    }

    public TransferItem Item { get; }
    public string Arrow => Item.Direction == TransferDirection.Upload ? "↑" : "↓";
    public string Name => Item.Name;
    public string Destination => Item.DestinationPath;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _isError;

    public void Refresh()
    {
        var now = DateTime.UtcNow;
        var bytes = Item.TransferredBytes;
        var elapsed = (now - _lastSample).TotalSeconds;
        if (elapsed >= 0.25)
        {
            var instant = Math.Max(0, bytes - _lastBytes) / elapsed;
            _speed = _speed == 0 ? instant : _speed * 0.7 + instant * 0.3; // smoothed
            _lastBytes = bytes;
            _lastSample = now;
        }

        var total = Math.Max(Item.TotalBytes, 0);
        Percent = total == 0 ? (Item.State == TransferState.Completed ? 100 : 0) : Math.Min(100, bytes * 100.0 / total);
        SizeText = $"{FileRowViewModel.FormatSize(bytes)} / {FileRowViewModel.FormatSize(total)}";
        var running = Item.State == TransferState.Running;
        SpeedText = running && _speed > 0 ? FormatBitRate(_speed) : "";
        StatusText = Item.State switch
        {
            TransferState.Queued => "Queued",
            TransferState.Running when _speed > 0 && total > bytes => $"{Percent:0}% · {FormatEta((total - bytes) / _speed)} left",
            TransferState.Running => $"{Percent:0}%",
            TransferState.Retrying => Item.Error ?? "Retrying…",
            TransferState.Completed => "Done",
            TransferState.Failed => "Failed: " + Item.Error,
            TransferState.Cancelled => "Cancelled",
            TransferState.Skipped => $"Skipped ({Item.Error ?? "already exists"})",
            _ => Item.State.ToString(),
        };
        CanCancel = Item.IsActive;
        CanRetry = Item.State is TransferState.Failed or TransferState.Cancelled;
        IsError = Item.State == TransferState.Failed;
    }

    /// <summary>Network-style bit rate from bytes per second (decimal: 1 Mbps = 1,000,000 bits/s).</summary>
    public static string FormatBitRate(double bytesPerSecond)
    {
        var bits = bytesPerSecond * 8;
        return bits >= 1e9 ? $"{bits / 1e9:0.##} Gbps"
            : bits >= 1e6 ? $"{bits / 1e6:0.#} Mbps"
            : bits >= 1e3 ? $"{bits / 1e3:0} kbps"
            : $"{bits:0} bps";
    }

    private static string FormatEta(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m"
        : seconds >= 60 ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s"
        : $"{Math.Max(1, (int)seconds)}s";
}
