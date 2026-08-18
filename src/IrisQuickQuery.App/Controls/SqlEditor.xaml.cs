using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IrisQuickQuery.App.Controls;

public partial class SqlEditor : UserControl
{
    private bool _updating;
    private static readonly Regex TokenRegex = new(@"(\{\{[A-Za-z_][A-Za-z0-9_]*\}\}|\b(?:SELECT|FROM|WHERE|JOIN|LEFT|RIGHT|INNER|OUTER|ON|AS|WITH|AND|OR|ORDER|BY|GROUP|HAVING|CASE|WHEN|THEN|ELSE|END|DISTINCT|TOP|IS|NULL|NOT|IN|LIKE)\b|'(?:''|[^'])*')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(SqlEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TextChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public event EventHandler? TextValueChanged;
    public SqlEditor() { InitializeComponent(); Loaded += (_, _) => Render(Text); }

    private static void TextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (SqlEditor)d;
        if (!editor._updating) editor.Render(e.NewValue as string ?? string.Empty);
        editor.TextValueChanged?.Invoke(editor, EventArgs.Empty);
    }
    private void Editor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return; _updating = true; Text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd('\r', '\n'); _updating = false;
    }
    private void Editor_OnLostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => Render(Text);
    private void Editor_OnPreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Editor.Dispatcher.BeginInvoke(() =>
        {
            var start = ExpandIdentifierBoundary(Editor.Selection.Start, LogicalDirection.Backward);
            var end = ExpandIdentifierBoundary(Editor.Selection.End, LogicalDirection.Forward);
            if (start.CompareTo(end) < 0) Editor.Selection.Select(start, end);
        }, DispatcherPriority.Input);
    }

    private static TextPointer ExpandIdentifierBoundary(TextPointer pointer, LogicalDirection direction)
    {
        var text = pointer.GetTextInRun(direction);
        var count = 0;
        if (direction == LogicalDirection.Backward)
        {
            for (var i = text.Length - 1; i >= 0 && IsIdentifierCharacter(text[i]); i--) count++;
            return pointer.GetPositionAtOffset(-count, direction) ?? pointer;
        }
        for (var i = 0; i < text.Length && IsIdentifierCharacter(text[i]); i++) count++;
        return pointer.GetPositionAtOffset(count, direction) ?? pointer;
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$' or '#';
    private void Render(string text)
    {
        if (_updating || Editor is null) return; _updating = true;
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 21 };
        var index = 0;
        foreach (Match match in TokenRegex.Matches(text))
        {
            if (match.Index > index) paragraph.Inlines.Add(new Run(text[index..match.Index]) { Foreground = new SolidColorBrush(Color.FromRgb(35, 55, 60)) });
            var color = match.Value.StartsWith("{{", StringComparison.Ordinal) ? Color.FromRgb(198, 123, 29)
                : match.Value.StartsWith("'", StringComparison.Ordinal) ? Color.FromRgb(30, 140, 98) : Color.FromRgb(19, 108, 120);
            paragraph.Inlines.Add(new Run(match.Value) { Foreground = new SolidColorBrush(color), FontWeight = match.Value.StartsWith("{{") ? FontWeights.SemiBold : FontWeights.Normal });
            index = match.Index + match.Length;
        }
        if (index < text.Length) paragraph.Inlines.Add(new Run(text[index..]));
        Editor.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) }; _updating = false;
    }
}
