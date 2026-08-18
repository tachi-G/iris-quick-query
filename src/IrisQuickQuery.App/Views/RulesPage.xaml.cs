using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IrisQuickQuery.App.Controls;
using IrisQuickQuery.App.ViewModels;
using IrisQuickQuery.Core.Models;
namespace IrisQuickQuery.App.Views;
public partial class RulesPage : UserControl
{
    private bool _refreshingMappingOptions;
    public RulesPage()
    {
        InitializeComponent();
        EntryResultModeColumn.ItemsSource = Enum.GetValues<RuleResultMode>();
    }
    private DraftConfigurationViewModel? ViewModel => RulesRoot.DataContext as DraftConfigurationViewModel;

    private void RuleEditor_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => NestedScrollCoordinator.RouteMouseWheel(RuleEditorScrollViewer, e);

    private void SqlEditor_OnTextValueChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => ViewModel?.RefreshSelectedRuleResultColumns(), DispatcherPriority.DataBind);

    private void ResultColumnComboBox_OnLoaded(object sender, RoutedEventArgs e) => RefreshResultColumnOptions(sender);
    private void ResultColumnComboBox_OnDropDownOpened(object sender, EventArgs e) => RefreshResultColumnOptions(sender);
    private void RefreshResultColumnOptions(object sender)
    {
        if (sender is ComboBox { DataContext: OutputMapping mapping } combo && ViewModel is { } viewModel)
        {
            _refreshingMappingOptions = true;
            try
            {
                combo.ItemsSource = viewModel.GetResultColumnOptions(mapping);
                combo.SelectedItem = mapping.ColumnName;
            }
            finally { _refreshingMappingOptions = false; }
        }
    }

    private void ResultColumnComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMappingOptions || sender is not ComboBox { DataContext: OutputMapping mapping, SelectedItem: string column }) return;
        mapping.ColumnName = column;
    }

    private void OutputElementComboBox_OnLoaded(object sender, RoutedEventArgs e) => RefreshOutputElementOptions(sender);
    private void OutputElementComboBox_OnDropDownOpened(object sender, EventArgs e) => RefreshOutputElementOptions(sender);
    private void RefreshOutputElementOptions(object sender)
    {
        if (sender is ComboBox { DataContext: OutputMapping mapping } combo && ViewModel is { } viewModel)
        {
            _refreshingMappingOptions = true;
            try
            {
                combo.ItemsSource = viewModel.GetAvailableOutputElements(mapping);
                combo.SelectedValue = mapping.ElementKey;
            }
            finally { _refreshingMappingOptions = false; }
        }
    }

    private void OutputElementComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingMappingOptions || sender is not ComboBox { DataContext: OutputMapping mapping, SelectedValue: string key }) return;
        mapping.ElementKey = key;
    }

    private void SaveRulesButton_OnClick(object sender, RoutedEventArgs e)
    {
        OutputMappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OutputMappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        QueryEntriesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        QueryEntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
}
