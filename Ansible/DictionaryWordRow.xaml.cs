using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ansible;

public sealed record DictionaryWordRowModel(string Word);

public sealed partial class DictionaryWordRow : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(DictionaryWordRowModel), typeof(DictionaryWordRow),
        new PropertyMetadata(null, (sender, _) => ((DictionaryWordRow)sender).Render()));

    public DictionaryWordRow()
    {
        InitializeComponent();
    }

    public DictionaryWordRowModel? Model
    {
        get => (DictionaryWordRowModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public string Word => Model?.Word ?? "";
    public string EditedWord => Editor.Text;

    public event Action<DictionaryWordRow>? EditRequested;
    public event Action<DictionaryWordRow>? SaveRequested;
    public event Action<DictionaryWordRow>? CancelRequested;
    public event Action<DictionaryWordRow>? RemoveRequested;

    private void Render()
    {
        WordLabel.Text = Word;
        AutomationProperties.SetName(Editor, $"Edit {Word}");
        AutomationProperties.SetName(EditButton, $"Edit {Word}");
        AutomationProperties.SetName(RemoveButton, $"Remove {Word}");
    }

    public void BeginEdit()
    {
        Editor.Text = Word;
        VisualStateManager.GoToState(this, "Editing", false);
        Editor.Focus(FocusState.Programmatic);
        Editor.SelectAll();
    }

    public void EndEdit()
    {
        VisualStateManager.GoToState(this, "Reading", false);
        if (IsLoaded) EditButton.Focus(FocusState.Programmatic);
    }

    private void EditClicked(object sender, RoutedEventArgs args) => EditRequested?.Invoke(this);
    private void SaveClicked(object sender, RoutedEventArgs args) => SaveRequested?.Invoke(this);
    private void CancelClicked(object sender, RoutedEventArgs args) => CancelRequested?.Invoke(this);
    private void RemoveClicked(object sender, RoutedEventArgs args) => RemoveRequested?.Invoke(this);

    private void EditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Enter)
        {
            args.Handled = true;
            SaveRequested?.Invoke(this);
        }
        else if (args.Key == Windows.System.VirtualKey.Escape)
        {
            args.Handled = true;
            CancelRequested?.Invoke(this);
        }
    }
}
