using System.Windows.Controls;
using System.Windows.Input;
using IrisQuickQuery.App.Controls;
using IrisQuickQuery.App.ViewModels;
namespace IrisQuickQuery.App.Views;
public partial class ConnectionPage : UserControl
{
    public ConnectionPage() => InitializeComponent();
    private void PageScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        => NestedScrollCoordinator.RouteMouseWheel(ConnectionScrollViewer, e);
    private void PasswordBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is ConnectionViewModel vm && sender is PasswordBox box) vm.Password = box.Password; }
    private void PasswordBox_OnGotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm) vm.BeginPasswordEdit();
    }
}
