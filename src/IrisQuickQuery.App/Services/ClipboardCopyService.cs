using System.Windows;

namespace IrisQuickQuery.App.Services;

internal static class ClipboardCopyService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(750);

    internal static async Task<bool> TrySetTextAsync(string text, TimeSpan? timeout = null,
        Action<string>? clipboardWriter = null)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    (clipboardWriter ?? Clipboard.SetText)(text);
                    completion.TrySetResult(true);
                    return;
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(40);
                }
            }

            completion.TrySetResult(false);
        })
        {
            IsBackground = true,
            Name = "IrisQuickQuery.Clipboard"
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            return false;
        }

        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout ?? DefaultTimeout));
        return completed == completion.Task && await completion.Task;
    }
}
