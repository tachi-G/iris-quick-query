using IrisQuickQuery.App.ViewModels;
using IrisQuickQuery.Core.Models;

namespace IrisQuickQuery.Core.Tests;

public sealed class BasicUiViewModelTests
{
    [Fact]
    public void QueryField_UsesFourSeventhsOfConfiguredWidth()
    {
        var field = new ElementFieldViewModel(new ElementDefinition { Width = 280 });

        Assert.Equal(160, field.DisplayWidth);
    }

    [Fact]
    public void DerivedField_CanEnterEditCommitAndCancelWithoutVisualTreeTraversal()
    {
        var definition = new ElementDefinition { Key = "registration_no", Label = "登记号", CanInput = true };
        var field = new ElementFieldViewModel(definition);
        var slot = new ElementSlot(definition.Key);
        slot.AddOrReplace(new ValueContribution("rule:test", "R0001", false, Guid.NewGuid(), new HashSet<string>()));
        field.UpdateFromSlot(slot, "身份证查询登记号");

        Assert.True(field.IsDerivedReadOnly);
        Assert.False(field.IsEditorVisible);
        field.BeginEdit();
        Assert.True(field.IsEditing);
        Assert.True(field.IsEditorVisible);

        field.InputText = "R0002";
        field.CancelEdit();
        Assert.Equal("R0001", field.InputText);
        field.BeginEdit();
        field.InputText = "R0003";
        field.CommitEditAsManual();
        Assert.True(field.IsManual);
        Assert.False(field.IsDerivedReadOnly);
        Assert.Equal("R0003", field.InputText);
    }

    [Fact]
    public void DateField_AlwaysDisplaysYearMonthDayWithoutMidnight()
    {
        var definition = new ElementDefinition { Key = "birth_date", Label = "出生日期", IsDate = true };
        var field = new ElementFieldViewModel(definition);
        var slot = new ElementSlot(definition.Key);
        slot.AddOrReplace(new ValueContribution("rule:test", new DateTime(2026, 8, 16, 0, 0, 0), false,
            Guid.NewGuid(), new HashSet<string>()));

        field.UpdateFromSlot(slot, "患者信息");

        Assert.Equal("2026-08-16", field.InputText);
    }

    [Fact]
    public void QueryRefresh_InvalidatesStaleClipboardFeedback()
    {
        var definition = new ElementDefinition { Key = "registration_no", Label = "登记号" };
        var field = new ElementFieldViewModel(definition);
        var staleRequest = field.BeginCopyFeedback();
        field.CompleteCopyFeedback(staleRequest, "剪贴板忙，请重试");
        var slot = new ElementSlot(definition.Key);
        slot.AddOrReplace(new ValueContribution("rule:test", "R0001", false, Guid.NewGuid(), new HashSet<string>()));

        field.UpdateFromSlot(slot, "患者信息");
        field.CompleteCopyFeedback(staleRequest, "剪贴板忙，请重试");

        Assert.Equal("R0001", field.InputText);
        Assert.Empty(field.CopyHint);
    }

    [Fact]
    public void Clear_RemovesClipboardFeedback()
    {
        var field = new ElementFieldViewModel(new ElementDefinition { Key = "registration_no" });
        var request = field.BeginCopyFeedback();
        field.CompleteCopyFeedback(request, "已复制");

        field.Clear();

        Assert.Empty(field.CopyHint);
    }

    [Fact]
    public void ListResult_FormatsMappedDateColumnsConsistently()
    {
        var snapshot = TestConfig.CreateElements("visit_date");
        snapshot.Elements.Single().Label = "就诊日期";
        snapshot.Elements.Single().IsDate = true;
        var state = new ListResultState
        {
            RuleName = "就诊记录",
            Rows = [new Dictionary<string, object?> { ["VISIT_DATE"] = "20260816 00:00:00" }],
            Mappings = [new OutputMapping { ColumnName = "VISIT_DATE", ElementKey = "visit_date" }]
        };

        var result = new ListResultViewModel(state, snapshot, _ => Task.CompletedTask);

        Assert.Equal("2026-08-16", result.Rows[0]["就诊日期"]);
    }

    [Fact]
    public async Task AsyncCommand_ReportsUnexpectedFailureInsteadOfEscapingAsyncVoid()
    {
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<AsyncCommandFailedEventArgs> handler = (_, args) => failure.TrySetResult(args.Exception);
        AsyncRelayCommand.ExecutionFailed += handler;
        try
        {
            var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("test failure")));
            command.Execute(null);
            var exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Equal("test failure", exception.Message);
        }
        finally { AsyncRelayCommand.ExecutionFailed -= handler; }
    }
}
