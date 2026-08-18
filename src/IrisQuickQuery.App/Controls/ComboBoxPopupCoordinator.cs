using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace IrisQuickQuery.App.Controls;

public static class ComboBoxPopupCoordinator
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ComboBoxPopupCoordinator),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(PopupState), typeof(ComboBoxPopupCoordinator));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ComboBox comboBox) return;
        if ((bool)e.NewValue)
        {
            comboBox.DropDownOpened += ComboBox_OnDropDownOpened;
            comboBox.DropDownClosed += ComboBox_OnDropDownClosed;
            comboBox.Unloaded += ComboBox_OnUnloaded;
            comboBox.SetValue(StateProperty, new PopupState(comboBox));
        }
        else
        {
            Detach(comboBox);
        }
    }

    private static void ComboBox_OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox) return;
        GetState(comboBox)?.Open();
    }

    private static void ComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox) GetState(comboBox)?.Close();
    }

    private static void ComboBox_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox) GetState(comboBox)?.Close();
    }

    private static PopupState? GetState(ComboBox comboBox)
        => comboBox.GetValue(StateProperty) as PopupState;

    private static void Detach(ComboBox comboBox)
    {
        GetState(comboBox)?.Close();
        comboBox.DropDownOpened -= ComboBox_OnDropDownOpened;
        comboBox.DropDownClosed -= ComboBox_OnDropDownClosed;
        comboBox.Unloaded -= ComboBox_OnUnloaded;
        comboBox.ClearValue(StateProperty);
    }

    private sealed class PopupState(ComboBox owner)
    {
        private Popup? _popup;
        private UIElement? _popupRoot;
        private ScrollViewer? _popupScrollViewer;
        private Point? _lastAnchorPosition;
        private bool _repositioning;

        public void Open()
        {
            owner.LayoutUpdated -= Owner_OnLayoutUpdated;
            owner.LayoutUpdated += Owner_OnLayoutUpdated;
            AttachPopup();
            owner.Dispatcher.BeginInvoke(AttachPopup, DispatcherPriority.Loaded);
        }

        public void Close()
        {
            owner.LayoutUpdated -= Owner_OnLayoutUpdated;
            if (_popupRoot is not null)
            {
                _popupRoot.PreviewMouseWheel -= PopupRoot_OnPreviewMouseWheel;
                _popupRoot.PreviewMouseMove -= PopupRoot_OnPreviewMouseMove;
            }

            _popup = null;
            _popupRoot = null;
            _popupScrollViewer = null;
            _lastAnchorPosition = null;
        }

        private void AttachPopup()
        {
            if (!owner.IsDropDownOpen) return;
            owner.ApplyTemplate();
            var popup = owner.Template.FindName("PART_Popup", owner) as Popup;
            var popupRoot = popup?.Child as UIElement;
            if (popup is null || popupRoot is null) return;

            if (!ReferenceEquals(_popupRoot, popupRoot))
            {
                if (_popupRoot is not null)
                {
                    _popupRoot.PreviewMouseWheel -= PopupRoot_OnPreviewMouseWheel;
                    _popupRoot.PreviewMouseMove -= PopupRoot_OnPreviewMouseMove;
                }

                _popupRoot = popupRoot;
                _popupRoot.PreviewMouseWheel += PopupRoot_OnPreviewMouseWheel;
                _popupRoot.PreviewMouseMove += PopupRoot_OnPreviewMouseMove;
            }

            _popup = popup;
            _popupScrollViewer = FindVisualChild<ScrollViewer>(popupRoot);
            _lastAnchorPosition = GetAnchorPosition();
        }

        private void Owner_OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (!owner.IsDropDownOpen || _popup is not { IsOpen: true }) return;
            var current = GetAnchorPosition();
            if (current is null) return;
            if (_lastAnchorPosition is { } previous
                && Math.Abs(previous.X - current.Value.X) < 0.1
                && Math.Abs(previous.Y - current.Value.Y) < 0.1) return;

            _lastAnchorPosition = current;
            RepositionPopup();
        }

        private Point? GetAnchorPosition()
        {
            try
            {
                return owner.IsVisible && PresentationSource.FromVisual(owner) is not null
                    ? owner.PointToScreen(new Point(0, owner.ActualHeight))
                    : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private void RepositionPopup()
        {
            if (_repositioning || _popup is not { IsOpen: true }) return;
            _repositioning = true;
            try
            {
                var offset = _popup.HorizontalOffset;
                _popup.HorizontalOffset = offset + 0.1;
                _popup.HorizontalOffset = offset;
            }
            finally
            {
                _repositioning = false;
            }
        }

        private void PopupRoot_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _popupScrollViewer ??= _popupRoot is null ? null : FindVisualChild<ScrollViewer>(_popupRoot);
            if (_popupScrollViewer is not null && NestedScrollCoordinator.TryScroll(_popupScrollViewer, e.Delta))
            {
                e.Handled = true;
                return;
            }

            if (NestedScrollCoordinator.TryScrollAncestors(owner, e.Delta))
            {
                e.Handled = true;
                owner.Dispatcher.BeginInvoke(RepositionPopup, DispatcherPriority.Render);
            }
        }

        private static void PopupRoot_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed
                && IsWithinScrollBar(e.OriginalSource as DependencyObject)) return;

            // Pointer movement over options must not invoke the ComboBox's legacy drag/autoscroll behavior.
            // A deliberate scrollbar drag remains available through the exception above.
            e.Handled = true;
        }

        private static bool IsWithinScrollBar(DependencyObject? source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is ScrollBar) return true;
                current = GetParent(current);
            }

            return false;
        }

        private static DependencyObject? GetParent(DependencyObject child)
        {
            if (child is ContentElement content)
                return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
            if (child is Visual or Visual3D) return VisualTreeHelper.GetParent(child);
            return LogicalTreeHelper.GetParent(child);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match) return match;
                if (FindVisualChild<T>(child) is { } descendant) return descendant;
            }

            return null;
        }
    }
}
