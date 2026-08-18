using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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

                var ruleEditor = Assert.IsType<ScrollViewer>(rulesPage.FindName("RuleEditorScrollViewer"));
                var rulesRoot = Assert.IsType<Grid>(rulesPage.FindName("RulesRoot"));
                Assert.Equal("Session", BindingOperations.GetBinding(rulesRoot, FrameworkElement.DataContextProperty)!.Path.Path);
                var outputGrid = Assert.IsType<DataGrid>(rulesPage.FindName("OutputMappingsGrid"));
                Assert.NotNull(rulesPage.FindName("RuleSqlEditor"));
                var sqlExample = Assert.IsType<TextBlock>(rulesPage.FindName("SqlExampleText"));
                var resultModeCombo = Assert.IsType<ComboBox>(rulesPage.FindName("ResultModeComboBox"));
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
                Assert.Contains("{{id_card_no}}", sqlExample.Text);
                Assert.Null(rulesPage.FindName("LocateRuleTestButton"));
                Assert.Equal(ScrollBarVisibility.Visible, ruleEditor.VerticalScrollBarVisibility);
                Assert.IsType<Button>(rulesPage.FindName("ImportRulesButton"));
                Assert.IsType<Button>(rulesPage.FindName("ExportRulesButton"));
                Assert.IsType<Border>(rulesPage.FindName("RuleTestSection"));
                Assert.Equal(driverCombo.Height, resultModeCombo.Height);
                Assert.Equal(driverCombo.Padding, resultModeCombo.Padding);
                Assert.Equal(driverCombo.VerticalContentAlignment, resultModeCombo.VerticalContentAlignment);

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
                var mappingRule = new QueryRuleDefinition
                {
                    Name = "映射显示测试",
                    SqlTemplate = "SELECT REG_NO AS registration_no, NAME AS patient_name FROM Patient",
                    OutputMappings = [new OutputMapping { ColumnName = "registration_no", ElementKey = "registration_no" }]
                };
                mappingViewModel.Rules.Add(mappingRule);
                mappingViewModel.SelectedRule = mappingRule;
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
                Assert.True(Assert.IsType<ItemsControl>(queryPage.FindName("ConditionsItemsControl")).AllowDrop);
                Assert.IsType<Border>(queryPage.FindName("RuleTraceSection"));
                var editableQueryField = Assert.IsType<Style>(queryPage.Resources["EditableQueryField"]);
                Assert.Equal(16d, editableQueryField.Setters.OfType<Setter>()
                    .Single(x => x.Property == Control.FontSizeProperty).Value);
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

                var elementsGrid = Assert.IsType<DataGrid>(elementsPage.FindName("ElementsGrid"));
                var headers = elementsGrid.Columns.Select(x => x.Header?.ToString()).ToArray();
                Assert.DoesNotContain("类型", headers);
                Assert.DoesNotContain("角色", headers);
                Assert.DoesNotContain("顺序", headers);
                Assert.Contains("是否日期", headers);
                Assert.Equal("全局元素", headers[0]);
                Assert.IsType<Button>(elementsPage.FindName("SaveElementsButton"));
                Assert.IsType<Button>(rulesPage.FindName("SaveRulesButton"));
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
