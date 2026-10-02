using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Ussh.App.Services;

/// <summary>Small modal dialogs, built in code so they need no extra views.</summary>
public sealed class DialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool danger = false) =>
        OnUiThread(() => ShowAsync(title, message, confirmText, cancelText, danger));

    public Task AlertAsync(string title, string message) =>
        OnUiThread(() => ShowAsync(title, message, "OK", null, false));

    /// <summary>Asks for a secret (masked). Returns null if cancelled. Nothing is stored.</summary>
    public Task<string?> PromptSecretAsync(string title, string message, string? error, string watermark) =>
        OnUiThread(async () =>
        {
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            var input = new TextBox { PasswordChar = '●', Watermark = watermark };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
            ok.Classes.Add("accent");
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            string? result = null;
            ok.Click += (_, _) => { result = input.Text ?? ""; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();

            var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (error != null)
                panel.Children.Add(new TextBlock { Text = error, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, ok },
            });
            dialog.Content = panel;
            dialog.Opened += (_, _) => input.Focus();
            await dialog.ShowDialog(_owner);
            return result;
        });

    public async Task<string?> PickFileTextAsync(string title)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        if (files.Count == 0)
            return null;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static Task<T> OnUiThread<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private async Task<bool> ShowAsync(string title, string message, string confirmText, string? cancelText, bool danger)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 90 };
        if (danger)
            confirm.Classes.Add("accent");
        confirm.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 90 };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(confirm);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new SelectableTextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, monospace, Inter"),
                },
                buttons,
            },
        };

        var result = await dialog.ShowDialog<bool?>(_owner);
        return result == true;
    }
}
