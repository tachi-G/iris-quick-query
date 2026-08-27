using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using IrisQuickQuery.App;
using IrisQuickQuery.App.Controls;
using IrisQuickQuery.App.ViewModels;
using IrisQuickQuery.App.Views;
using IrisQuickQuery.Core.Models;
using IrisQuickQuery.Core.Services;
using IrisQuickQuery.Infrastructure.Database;
using IrisQuickQuery.Infrastructure.Diagnostics;
using IrisQuickQuery.Infrastructure.Security;
using IrisQuickQuery.Infrastructure.Storage;

namespace IrisQuickQuery.Core.Tests;

public sealed class WpfSmokeTests
{
    [Fact]
    public async Task AppTheme_MainWindow_AndAllPages_LoadOnStaThread()
    {
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new IrisQuickQuery.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                Assert.NotNull(app.Resources["PrimaryBrush"]);
                var unifiedComboStyle = Assert.IsType<Style>(app.Resources["UnifiedComboBox"]);
                var implicitComboStyle = Assert.IsType<Style>(app.TryFindResource(typeof(ComboBox)));
                Assert.Same(unifiedComboStyle, implicitComboStyle.BasedOn);
                Assert.Null(unifiedComboStyle.Setters.OfType<Setter>()
                    .Single(x => x.Property == FrameworkElement.FocusVisualStyleProperty).Value);
                Assert.True(Assert.IsType<bool>(unifiedComboStyle.Setters.OfType<Setter>()
                    .Single(x => x.Property == ComboBoxPopupCoordinator.IsEnabledProperty).Value));
                var comboItemStyle = Assert.IsType<Style>(app.TryFindResource(typeof(ComboBoxItem)));
                Assert.Null(comboItemStyle.Setters.OfType<Setter>()
                    .Single(x => x.Property == FrameworkElement.FocusVisualStyleProperty).Value);
                var navStyle = Assert.IsType<Style>(app.Resources["NavButton"]);
                var navTemplate = Assert.IsType<ControlTemplate>(navStyle.Setters
                    .OfType<Setter>().Single(x => x.Property == Control.TemplateProperty).Value);
                Assert.DoesNotContain(navTemplate.Triggers.OfType<Trigger>(),
                    trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty);
                var connectionPage = new ConnectionPage();
                var rulesPage = new RulesPage();
                var elementsPage = new ElementsPage();
                var queryPage = new QueryPage();
                var mainWindow = new MainWindow();
                var controls = new FrameworkElement[]
                {
                    mainWindow, queryPage, elementsPage, rulesPage,
                    connectionPage, new AboutPage()
                };
                Assert.All(controls, control => Assert.NotNull(control));
                Assert.Equal(1200d, mainWindow.Width);
                Assert.Equal(1100d, mainWindow.MinWidth);

                var fieldsCard = Assert.IsType<Border>(connectionPage.FindName("ConnectionFieldsCard"));
                var driverCombo = Assert.IsType<ComboBox>(connectionPage.FindName("DriverComboBox"));
                var dsnCombo = Assert.IsType<ComboBox>(connectionPage.FindName("DsnComboBox"));
                var password = Assert.IsType<PasswordBox>(connectionPage.FindName("ConnectionPasswordBox"));
                var savedPasswordPlaceholder = Assert.IsType<TextBlock>(connectionPage.FindName("SavedPasswordPlaceholder"));
                var notification = Assert.IsType<Border>(connectionPage.FindName("ConnectionNotification"));
                Assert.Equal(500, fieldsCard.Width);
                Assert.Equal(38, driverCombo.Height);
                Assert.Equal(driverCombo.Height, dsnCombo.Height);
                Assert.Equal(driverCombo.Height, password.Height);
                Assert.Equal("********", savedPasswordPlaceholder.Text);
                Assert.Equal(VerticalAlignment.Center, driverCombo.VerticalContentAlignment);
                Assert.Equal(HorizontalAlignment.Right, notification.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Bottom, notification.VerticalAlignment);

                var scrollProbe = new ScrollViewer { Height = 100, Content = new Border { Height = 500 } };
                scrollProbe.Measure(new Size(200, 100));
                scrollProbe.Arrange(new Rect(0, 0, 200, 100));
                scrollProbe.UpdateLayout();
                Assert.False(NestedScrollCoordinator.CanScroll(scrollProbe, 120));
                Assert.True(NestedScrollCoordinator.CanScroll(scrollProbe, -120));
                scrollProbe.ScrollToEnd();
                scrollProbe.UpdateLayout();
                Assert.True(NestedScrollCoordinator.CanScroll(scrollProbe, 120));
                Assert.False(NestedScrollCoordinator.CanScroll(scrollProbe, -120));

                var logicalPanel = new StackPanel();
                for (var index = 0; index < 30; index++) logicalPanel.Children.Add(new Border { Height = 20 });
                var logicalScrollProbe = new ScrollViewer
                {
                    Height = 100,
                    CanContentScroll = true,
                    Content = logicalPanel
                };
                logicalScrollProbe.Measure(new Size(200, 100));
                logicalScrollProbe.Arrange(new Rect(0, 0, 200, 100));
                logicalScrollProbe.UpdateLayout();
                Assert.True(NestedScrollCoordinator.TryScroll(logicalScrollProbe, -120));
                logicalScrollProbe.UpdateLayout();
                Assert.True(logicalScrollProbe.VerticalOffset > 0);
                Assert.True(logicalScrollProbe.VerticalOffset < logicalScrollProbe.ScrollableHeight);

                var ruleEditor = Assert.IsType<ScrollViewer>(rulesPage.FindName("RuleEditorScrollViewer"));
                var rulesRoot = Assert.IsType<Grid>(rulesPage.FindName("RulesRoot"));
                var queryObjectLayout = Assert.IsType<Grid>(rulesPage.FindName("QueryObjectLayout"));
                Assert.Equal("Session", BindingOperations.GetBinding(rulesRoot, FrameworkElement.DataContextProperty)!.Path.Path);
                Assert.Equal(202.5d, queryObjectLayout.ColumnDefinitions[0].Width.Value);
                var outputGrid = Assert.IsType<DataGrid>(rulesPage.FindName("OutputMappingsGrid"));
                Assert.NotNull(rulesPage.FindName("RuleSqlEditor"));
                var sqlExample = Assert.IsType<TextBlock>(rulesPage.FindName("SqlExampleText"));
                var entriesGrid = Assert.IsType<DataGrid>(rulesPage.FindName("QueryEntriesGrid"));
                Assert.NotEmpty(ruleEditor.Style.Triggers);
                Assert.All(outputGrid.Columns, column => Assert.IsType<DataGridTemplateColumn>(column));
                Assert.Equal(520, outputGrid.Width);
                Assert.Equal("结果列名", outputGrid.Columns[0].Header);
                var resultCombo = Assert.IsType<ComboBox>(((DataGridTemplateColumn)outputGrid.Columns[0]).CellTemplate.LoadContent());
                var elementCombo = Assert.IsType<ComboBox>(((DataGridTemplateColumn)outputGrid.Columns[1]).CellTemplate.LoadContent());
                Assert.Equal(BindingMode.OneWay,
                    BindingOperations.GetBinding(resultCombo, Selector.SelectedItemProperty)!.Mode);
                Assert.Equal(BindingMode.OneWay,
                    BindingOperations.GetBinding(elementCombo, Selector.SelectedValueProperty)!.Mode);
                Assert.Contains("PA_PatMas", sqlExample.Text);
                Assert.Null(rulesPage.FindName("LocateRuleTestButton"));
                Assert.Equal(ScrollBarVisibility.Visible, ruleEditor.VerticalScrollBarVisibility);
                Assert.IsType<Button>(rulesPage.FindName("ImportRulesButton"));
                Assert.IsType<Button>(rulesPage.FindName("ExportRulesButton"));
                var ruleTestSection = Assert.IsType<Border>(rulesPage.FindName("RuleTestSection"));
                Assert.Same(app.Resources["SurfaceBrush"], ruleTestSection.Background);
                Assert.Equal("入口名称", entriesGrid.Columns[0].Header);
                Assert.Equal("筛选条件", entriesGrid.Columns[1].Header);
                Assert.Equal("排序", entriesGrid.Columns[4].Header);
                Assert.Equal(2d, entriesGrid.Columns[1].Width.Value);
                Assert.Equal(DataGridLengthUnitType.Star, entriesGrid.Columns[1].Width.UnitType);
                Assert.NotEmpty(Assert.IsType<DataGridComboBoxColumn>(entriesGrid.Columns[2]).ItemsSource);

                var mappingTestDirectory = Path.Combine(Path.GetTempPath(), "IrisQuickQueryWpfTests", Guid.NewGuid().ToString("N"));
                var mappingPaths = new AppDataPaths(mappingTestDirectory);
                var mappingRepository = new SqliteConfigurationRepository(mappingPaths);
                var mappingProfiles = new ConnectionProfileRepository(mappingRepository);
                var mappingCredentials = new DpapiCredentialStore(mappingRepository);
                var mappingExecutor = new OdbcQueryExecutor(mappingProfiles, mappingCredentials);
                var mappingLogger = new ExecutionMetadataLogger(mappingPaths);
                var mappingServices = new ApplicationServices(mappingPaths, mappingRepository, mappingProfiles, mappingCredentials,
                    mappingExecutor, new RuleScheduler(mappingExecutor, mappingLogger), mappingLogger, new ConfigurationPackageService());
                var mappingViewModel = new DraftConfigurationViewModel(mappingServices);
                mappingViewModel.Elements.Add(new ElementDefinition { Key = "registration_no", Label = "登记号" });
                mappingViewModel.Elements.Add(new ElementDefinition { Key = "patient_name", Label = "患者姓名" });
                var mappingObject = new QueryObjectDefinition
                {
                    Name = "映射显示测试",
                    BaseSqlTemplate = "SELECT REG_NO AS registration_no, NAME AS patient_name FROM Patient",
                    OutputMappings = [new OutputMapping { ColumnName = "registration_no", ElementKey = "registration_no" }],
                    Entries = [new QueryEntryDefinition { Name = "按登记号", FilterTemplate = "REG_NO={{registration_no}}" }]
                };
                mappingViewModel.QueryObjects.Add(mappingObject);
                mappingViewModel.SelectedQueryObject = mappingObject;
                rulesPage.DataContext = new RuleConfigurationPageViewModel(mappingViewModel);
                rulesPage.Measure(new Size(1180, 900));
                rulesPage.Arrange(new Rect(0, 0, 1180, 900));
                rulesPage.UpdateLayout();
                outputGrid.UpdateLayout();
                var mappingCombos = FindVisualChildren<ComboBox>(outputGrid).ToArray();
                Assert.Equal(2, mappingCombos.Length);
                Assert.Same(mappingViewModel, rulesRoot.DataContext);
                foreach (var combo in mappingCombos)
                    combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.All(mappingCombos, combo => Assert.NotEmpty(combo.Items));
                Assert.Equal("registration_no", mappingCombos[0].SelectedItem);
                Assert.Equal("registration_no", mappingCombos[1].SelectedValue);

                var aboutPage = Assert.IsType<AboutPage>(controls[5]);
                Assert.IsType<Button>(aboutPage.FindName("AboutOpenLogFolderButton"));

                Assert.IsType<Button>(queryPage.FindName("ConditionsEditButton"));
                var queryActions = Assert.IsType<StackPanel>(queryPage.FindName("QueryActionButtons"));
                var clearQuery = Assert.IsType<Button>(queryPage.FindName("QueryClearButton"));
                var runQuery = Assert.IsType<Button>(queryPage.FindName("QueryRunButton"));
                Assert.Same(queryActions, clearQuery.Parent);
                Assert.Same(queryActions, runQuery.Parent);
                Assert.Equal(1, Grid.GetColumn(queryActions));
                var conditionItems = Assert.IsType<ItemsControl>(queryPage.FindName("ConditionsItemsControl"));
                Assert.True(conditionItems.AllowDrop);
                Assert.IsType<Border>(queryPage.FindName("RuleTraceSection"));
                Assert.Null(queryPage.FindName("QueryStatusRail"));
                var editableQueryField = Assert.IsType<Style>(queryPage.Resources["EditableQueryField"]);
                Assert.Equal(16d, editableQueryField.Setters.OfType<Setter>()
                    .Single(x => x.Property == Control.FontSizeProperty).Value);
                Assert.Equal(40d, editableQueryField.Setters.OfType<Setter>()
                    .Single(x => x.Property == FrameworkElement.HeightProperty).Value);
                var readOnlyQueryField = Assert.IsType<Style>(queryPage.Resources["ReadOnlyFieldValue"]);
                Assert.Equal(40d, readOnlyQueryField.Setters.OfType<Setter>()
                    .Single(x => x.Property == FrameworkElement.HeightProperty).Value);
                Assert.Null(queryPage.FindName("ConditionAddButton"));
                var conditionPicker = Assert.IsType<Border>(queryPage.FindName("ConditionPicker"));
                var availableConditions = Assert.IsType<ItemsControl>(queryPage.FindName("AvailableConditionsList"));
                conditionPicker.Visibility = Visibility.Visible;
                availableConditions.ItemsSource = new[]
                {
                    new QueryConditionOptionViewModel(new ElementDefinition
                        { Key = "visit_no", Label = "就诊号", Group = "就诊记录" })
                };
                queryPage.Measure(new Size(1180, 800));
                queryPage.Arrange(new Rect(0, 0, 1180, 800));
                queryPage.UpdateLayout();

                conditionItems.ItemsSource = new[]
                {
                    new ElementFieldViewModel(new ElementDefinition
                        { Key = "readonly_value", Label = "只读值", CanInput = false })
                };
                queryPage.UpdateLayout();
                var selectableReadOnlyValue = FindVisualChildren<TextBox>(conditionItems)
                    .Single(box => box.IsReadOnly);
                Assert.True(selectableReadOnlyValue.IsReadOnlyCaretVisible);
                Assert.Equal(0d, selectableReadOnlyValue.MinHeight);
                Assert.Contains(selectableReadOnlyValue.CommandBindings.Cast<CommandBinding>(),
                    binding => binding.Command == ApplicationCommands.Copy);

                var elementsGrid = Assert.IsType<DataGrid>(elementsPage.FindName("ElementsGrid"));
                var headers = elementsGrid.Columns.Select(x => x.Header?.ToString()).ToArray();
                Assert.DoesNotContain("类型", headers);
                Assert.DoesNotContain("角色", headers);
                Assert.DoesNotContain("顺序", headers);
                Assert.DoesNotContain("分组", headers);
                Assert.DoesNotContain("校验正则（可选）", headers);
                Assert.DoesNotContain("敏感", headers);
                Assert.Contains("是否日期", headers);
                Assert.Equal("全局元素", headers[0]);
                Assert.False(elementsGrid.CanUserSortColumns);
                Assert.Equal(560d, elementsGrid.Width);
                Assert.Equal(HorizontalAlignment.Left, elementsGrid.HorizontalAlignment);
                Assert.All(elementsGrid.Columns, column => Assert.Equal(DataGridLengthUnitType.Pixel, column.Width.UnitType));
                var saveElements = Assert.IsType<Button>(elementsPage.FindName("SaveElementsButton"));
                var saveRules = Assert.IsType<Button>(rulesPage.FindName("SaveRulesButton"));
                Assert.Equal("elements", saveElements.CommandParameter);
                Assert.Equal("rules", saveRules.CommandParameter);
                Assert.Equal(72d, saveElements.Width);
                Assert.Equal(36d, saveElements.Height);
                Assert.Equal(52d, Assert.IsType<Grid>(elementsPage.FindName("ElementsFooter")).Height);
                Assert.Equal(52d, Assert.IsType<Grid>(rulesPage.FindName("RulesFooter")).Height);
                var elementStatus = Assert.IsType<TextBlock>(elementsPage.FindName("ElementStatusText"));
                var rulesStatus = Assert.IsType<TextBlock>(rulesPage.FindName("RulesStatusText"));
                Assert.Equal("ElementStatusMessage", BindingOperations.GetBinding(elementStatus, TextBlock.TextProperty)!.Path.Path);
                Assert.Equal("StatusMessage", BindingOperations.GetBinding(rulesStatus, TextBlock.TextProperty)!.Path.Path);
                Assert.Equal(15, elementsGrid.FontSize);
                Assert.Equal(40, elementsGrid.RowHeight);
                Assert.Equal(42, elementsGrid.ColumnHeaderHeight);
                Assert.Equal(DataGridGridLinesVisibility.All, elementsGrid.GridLinesVisibility);
                var dateColumn = Assert.IsType<DataGridCheckBoxColumn>(elementsGrid.Columns.Single(x => Equals(x.Header, "是否日期")));
                var dateBinding = Assert.IsType<Binding>(dateColumn.Binding);
                Assert.Equal(BindingMode.TwoWay, dateBinding.Mode);
                Assert.Equal(UpdateSourceTrigger.PropertyChanged, dateBinding.UpdateSourceTrigger);
                app.Shutdown();
                completion.SetResult(null);
            }
            catch (Exception ex) { completion.SetResult(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var error = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(error);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
