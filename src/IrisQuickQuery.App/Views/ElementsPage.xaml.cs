using System.Windows.Controls;
namespace IrisQuickQuery.App.Views;
public partial class ElementsPage : UserControl
{
    public ElementsPage() => InitializeComponent();

    private void SaveElementsButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ElementsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ElementsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }
}
