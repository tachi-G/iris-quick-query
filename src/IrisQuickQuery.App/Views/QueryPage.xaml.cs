using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows;
using IrisQuickQuery.App.Controls;
using IrisQuickQuery.App.ViewModels;

namespace IrisQuickQuery.App.Views;

public partial class QueryPage : UserControl
{
    private Point _dragStart;
    private ElementFieldViewModel? _dragCandidate;
    public QueryPage() => InitializeComponent();
    private QueryViewModel? ViewModel => DataContext as QueryViewModel;

    private void PageScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => NestedScrollCoordinator.RouteMouseWheel(QueryScrollViewer, e);

    private void ConditionCard_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (ViewModel?.IsEditingConditions != true || sender is not Border { Tag: ElementFieldViewModel field }) return;
        if (e.OriginalSource is DependencyObject source
            && (FindAncestor<Button>(source) is not null || FindAncestor<TextBox>(source) is not null)) return;
        _dragStart = e.GetPosition(ConditionsItemsControl);
        _dragCandidate = field;
    }

    private void ConditionCard_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate is null || ViewModel?.IsEditingConditions != true) return;
        var position = e.GetPosition(ConditionsItemsControl);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(ConditionsItemsControl, dragged, DragDropEffects.Move);
    }

    private void ConditionsItemsControl_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ViewModel?.IsEditingConditions == true && e.Data.GetDataPresent(typeof(ElementFieldViewModel))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ConditionsItemsControl_OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel?.IsEditingConditions != true
            || e.Data.GetData(typeof(ElementFieldViewModel)) is not ElementFieldViewModel dragged) return;
        var target = e.OriginalSource is DependencyObject source
            ? FindFieldDataContext(source)
            : null;
        ViewModel.MoveCondition(dragged, target);
        e.Handled = true;
    }

    private static ElementFieldViewModel? FindFieldDataContext(DependencyObject source)
    {
        for (var current = source; current is not null; current = GetParent(current))
            if (current is FrameworkElement { DataContext: ElementFieldViewModel field }) return field;
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
        => child is System.Windows.Media.Visual
            ? System.Windows.Media.VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private async void FieldEditor_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: ElementFieldViewModel field }) return;
        if (e.Key == Key.Enter) { e.Handled = true; await (ViewModel?.CommitFieldAndRunAsync(field) ?? Task.CompletedTask); }
        else if (e.Key == Key.Escape) { e.Handled = true; ViewModel?.CancelFieldEdit(field); }
    }
    private void ReadOnlyField_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ElementFieldViewModel field } border) return;
        border.Focus();
        if (e.ClickCount == 2)
        {
            field.BeginEdit();
            e.Handled = true;
        }
    }
    private void ReadOnlyField_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Border { Tag: ElementFieldViewModel field }) return;
        if (e.Key == Key.F2) { field.BeginEdit(); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { ViewModel?.CopyField(field); e.Handled = true; }
    }

    private void FieldEditor_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true, Tag: ElementFieldViewModel { IsEditing: true } } box) return;
        box.Dispatcher.BeginInvoke(() =>
        {
            if (box.IsVisible)
            {
                box.Focus();
                box.SelectAll();
            }
        }, DispatcherPriority.Input);
    }
}
