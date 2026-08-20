using System.Runtime.InteropServices;
using System.Diagnostics;
using IrisQuickQuery.App.Services;

namespace IrisQuickQuery.Core.Tests;

public sealed class ClipboardCopyTests
{
    [Fact]
    public async Task BusyClipboard_RetriesOnStaThreadAndEventuallySucceeds()
    {
        var attempts = 0;
        var apartmentState = ApartmentState.Unknown;

        var copied = await ClipboardCopyService.TrySetTextAsync("value", TimeSpan.FromSeconds(1), _ =>
        {
            apartmentState = Thread.CurrentThread.GetApartmentState();
            if (Interlocked.Increment(ref attempts) < 3) throw new COMException("clipboard busy");
        });

        Assert.True(copied);
        Assert.Equal(3, attempts);
        Assert.Equal(ApartmentState.STA, apartmentState);
    }

    [Fact]
    public async Task PersistentlyBusyClipboard_ReturnsFalseInsteadOfThrowing()
    {
        var attempts = 0;

        var copied = await ClipboardCopyService.TrySetTextAsync("value", TimeSpan.FromSeconds(1), _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new COMException("clipboard busy");
        });

        Assert.False(copied);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StalledClipboard_TimesOutWithoutBlockingCaller()
    {
        using var release = new ManualResetEventSlim(false);
        var watch = Stopwatch.StartNew();

        var copied = await ClipboardCopyService.TrySetTextAsync("value", TimeSpan.FromMilliseconds(40), _ =>
        {
            release.Wait(TimeSpan.FromSeconds(2));
        });
        watch.Stop();
        release.Set();

        Assert.False(copied);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"复制超时返回过慢：{watch.Elapsed}");
    }
}
