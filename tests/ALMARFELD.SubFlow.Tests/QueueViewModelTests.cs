using System.Collections;
using System.Windows.Threading;
using ALMARFELD.SubFlow.App.Services;
using ALMARFELD.SubFlow.App.ViewModels;
using ALMARFELD.SubFlow.Core.Jobs;
using ALMARFELD.SubFlow.Infrastructure.Persistence;

namespace ALMARFELD.SubFlow.Tests;

[Collection("media")]
public class QueueViewModelTests
{
    private static AppServices Services(TempDir dir) => new(new AppPaths(dir.File("settings"), dir.File("local")));
    private static JobItem Item(TempDir dir, string name) => new() { SourcePath = dir.File(name), RootPath = dir.Path, Options = new() };

    [Fact]
    public Task RemoveCommand_UsesItsRowInsteadOfAnotherSelection() => OnSta(async () =>
    {
        using var dir = new TempDir();
        var services = Services(dir);
        var vm = new QueueViewModel(services);
        var first = Item(dir, "first.mkv");
        var selected = Item(dir, "selected.mkv");
        var last = Item(dir, "last.mkv");
        await vm.EnqueueAsync([first, selected, last], false);
        vm.Selected = vm.Items[0];
        vm.RemoveCommand.Execute(vm.Items[1]);
        Assert.Equal(new[] { first.Id, last.Id }, vm.Items.Select(i => i.Item.Id));
        Assert.Null(services.Store.Get(selected.Id));
        Assert.Equal(new[] { first.Id, last.Id }, new QueueViewModel(services).Items.Select(i => i.Item.Id));
    });

    [Fact]
    public Task Enqueue_SkipsRepeatedPathsAndReplacesCancelledRowsDurably() => OnSta(async () =>
    {
        using var dir = new TempDir();
        var services = Services(dir);
        var vm = new QueueViewModel(services);
        var original = Item(dir, "episode.mkv");
        Assert.Equal(1, await vm.EnqueueAsync([original], false));
        Assert.Equal(0, await vm.EnqueueAsync([Item(dir, "episode.mkv"), Item(dir, ".\\episode.mkv")], false));
        original.Status = JobStatus.Running;
        Assert.Equal(0, await vm.EnqueueAsync([Item(dir, "EPISODE.mkv")], false));
        original.Status = JobStatus.Cancelled;
        services.Store.Upsert(original);
        var replacement = Item(dir, "episode.mkv");
        Assert.Equal(1, await vm.EnqueueAsync([replacement], false));
        Assert.Null(services.Store.Get(original.Id));
        Assert.Equal(replacement.Id, Assert.Single(new QueueViewModel(services).Items).Item.Id);
        replacement.Status = JobStatus.Cancelled;
        services.Store.Upsert(replacement);
        vm.ClearFinishedCommand.Execute(null);
        Assert.Empty(new QueueViewModel(services).Items);
    });

    [SkippableFact]
    public async Task RepeatedAndOverlappingFolders_DoNotDuplicateVideos_AndRemovePreservesFiles()
    {
        Skip.IfNot(TestMedia.FfmpegAvailable, "FFmpeg not installed");
        using var dir = new TempDir();
        var first = await TestMedia.CreateVideoAsync(dir.File("first.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var second = await TestMedia.CreateVideoAsync(dir.File("second.mkv"), [new(TestMedia.EnglishSrt, "eng")]);
        var originalFirst = await File.ReadAllBytesAsync(first);
        var originalSecond = await File.ReadAllBytesAsync(second);
        await OnSta(async () =>
        {
            var services = Services(dir);
            var vm = new VideosViewModel(services);
            await vm.AddPathsAsync([dir.Path]);
            await vm.AddPathsAsync([dir.Path, first]);
            await Task.WhenAll(vm.AddPathsAsync([dir.Path]), vm.AddPathsAsync([first, second]));
            Assert.Equal(2, vm.Count);
            Assert.Equal(2, vm.ReadyCount);
            var selected = vm.Entries.Single(e => e.Path == second);
            vm.Selected = vm.Entries.Single(e => e.Path == first);
            vm.RemoveCommand.Execute(new ArrayList { selected });
            Assert.Equal(first, Assert.Single(vm.Entries).Path);
            vm.RemoveCommand.Execute(new ArrayList()); // an empty explicit selection removes nothing
            Assert.Single(vm.Entries);
            vm.Selected = vm.Entries[0];
            vm.RemoveCommand.Execute(null);
            Assert.True(vm.IsEmpty);
            Assert.Equal(0, vm.ReadyCount);
            Assert.Null(vm.Selected);
        });
        Assert.Equal(originalFirst, await File.ReadAllBytesAsync(first));
        Assert.Equal(originalSecond, await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public Task DashboardExplainsPersistedWaitingItemsAndRefreshesAfterRemoval() => OnSta(async () =>
    {
        using var dir = new TempDir();
        var services = Services(dir);
        var queued = new QueueViewModel(services);
        await queued.EnqueueAsync([Item(dir, "waiting.mkv")], false);
        var restored = new QueueViewModel(services);
        var videos = new VideosViewModel(services);
        var translation = new TranslationViewModel(services, videos, restored, () => { });
        string? destination = null;
        var dashboard = new DashboardViewModel(services, videos, translation, restored, page => destination = page);
        Assert.True(dashboard.ShowWaitingNotice);
        Assert.Contains("1", dashboard.WaitingNotice);
        dashboard.GoCommand.Execute("Queue");
        Assert.Equal("Queue", destination);
        var notifications = new List<string?>();
        dashboard.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        restored.RemoveCommand.Execute(restored.Items[0]);
        Assert.False(dashboard.ShowWaitingNotice);
        Assert.Contains(nameof(DashboardViewModel.ShowWaitingNotice), notifications);
        Assert.Empty(new QueueViewModel(services).Items);
    });

    private static Task OnSta(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completed.SetResult(); }
                catch (Exception ex) { completed.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
