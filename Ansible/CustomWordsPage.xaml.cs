using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ansible;

public sealed partial class CustomWordsPage : Page
{
    private sealed class WordRow(string word)
    {
        public string Word { get; } = word;
        public required StackPanel Root { get; init; }
        public required TextBlock Label { get; init; }
        public required TextBox Editor { get; init; }
        public required StackPanel Actions { get; init; }
        public required StackPanel EditActions { get; init; }
    }

    private readonly DictationSession _session;
    private readonly Action _openModels;
    private readonly AppNotices _notices;
    private readonly Dictionary<string, WordRow> _rows = new(StringComparer.Ordinal);
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private string? _renderedDictionary;
    private string? _compatibilityKey;
    private WordRow? _editingRow;
    private bool _editorOpen;

    internal CustomWordsPage(DictationSession session, Action openModels, AppNotices notices)
    {
        _session = session;
        _notices = notices;
        _openModels = openModels;
        _state = session.State;
        InitializeComponent();
        Loaded += (_, _) => _observer = new UiSessionObserver(session, DispatcherQueue, state =>
        {
            _state = state;
            Render();
        });
        Unloaded += (_, _) =>
        {
            _observer?.Dispose();
            _observer = null;
            if (_editorOpen) EntryDialog.Hide();
        };
        Render();
    }

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    private void Render()
    {
        var dictionary = _state.Settings.CustomDictionary;
        if (_renderedDictionary != dictionary)
        {
            _renderedDictionary = dictionary;
            RenderWords();
        }
        RenderCompatibility(dictionary);
        BusyText.Visibility = _state.IsIdle ? Visibility.Collapsed : Visibility.Visible;
        ImportWordsButton.IsEnabled = _state.IsIdle;
        WordsList.IsEnabled = _state.IsIdle;
        UpdateAddButton();
        ValidateEditor();
    }

    // The warning explains why the words do nothing, so it names the real model and any installed model that uses them.
    private void RenderCompatibility(string dictionary)
    {
        var model = _state.SelectedModel;
        string? title = null;
        var message = "";
        if (model is null) title = "Choose a speech model to use custom words.";
        else if (!model.SupportsCustomDictionary)
        {
            title = $"{model.DisplayName ?? model.Id} ignores custom words.";
            var alternative = _state.Models.FirstOrDefault(candidate =>
                candidate.SupportsCustomDictionary && candidate.Task != "diar" && candidate.Id != model.Id);
            message = alternative is null ? "Install a model that supports them in Speech models."
                : $"{alternative.DisplayName ?? alternative.Id} is installed and uses them.";
        }
        else title = CustomVocabulary.ModelCompatibilityError(model, dictionary);

        var key = title is null ? null : title + "\n" + message;
        if (key == _compatibilityKey) return;
        _compatibilityKey = key;
        CompatibilityNotice.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        if (title is not null)
            CompatibilityNotice.Show(new AppNotice("dictionary-compatibility", AppNoticeSeverity.Warning, title, message,
                NoticeArea.Dictionary, IsClosable: false, Actions: [new("Speech models", _openModels)]));
    }

    private void RenderWords()
    {
        var entries = _state.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var query = SearchBox.Text.Trim();
        var filtered = entries.Where(word => word.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var keep = filtered.ToHashSet(StringComparer.Ordinal);
        foreach (var word in _rows.Keys.Where(word => !keep.Contains(word)).ToArray())
        {
            if (_editingRow is { } editing && editing.Word == word) _editingRow = null;
            WordsList.Items.Remove(_rows[word].Root);
            _rows.Remove(word);
        }
        // Preserve unchanged rows so dictionary updates do not discard their focused controls.
        for (var index = 0; index < filtered.Length; index++)
        {
            var row = RowFor(filtered[index]);
            if (index >= WordsList.Items.Count || !ReferenceEquals(WordsList.Items[index], row.Root))
                WordsList.Items.Insert(index, row.Root);
        }
        CountText.Text = $"{entries.Length} of {CustomVocabulary.MaximumEntries}";
        var empty = filtered.Length == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHelp.Text = entries.Length == 0
            ? "Add names, product terms, and acronyms the model gets wrong."
            : $"No match. Press Enter to add \"{query}\".";
    }

    private WordRow RowFor(string word)
    {
        if (_rows.TryGetValue(word, out var existing)) return existing;
        var resources = Application.Current.Resources;
        var label = new TextBlock { Text = word, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var editor = new TextBox { Text = word, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(editor, $"Edit {word}");
        var edit = IconButton("", "Edit word", $"Edit {word}", "EditCustomWordButton");
        var remove = IconButton("", "Remove word", $"Remove {word}", "RemoveCustomWordButton");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(edit);
        actions.Children.Add(remove);
        var save = new Button { Content = "Save", Style = AppStyle("AppPrimaryButtonStyle") };
        var cancel = new Button { Content = "Cancel" };
        var editActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        editActions.Children.Add(save);
        editActions.Children.Add(cancel);

        var body = new Grid { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(label);
        body.Children.Add(editor);
        var buttons = new Grid { VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(actions);
        buttons.Children.Add(editActions);
        var layout = new Grid { MinHeight = 44, Padding = new Thickness(12, 4, 8, 4), ColumnSpacing = 8 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(body);
        Grid.SetColumn(buttons, 1);
        layout.Children.Add(buttons);
        var root = new StackPanel();
        root.Children.Add(layout);
        root.Children.Add(new Border { Style = (Style)resources["HistoryDividerStyle"] });

        var row = new WordRow(word) { Root = root, Label = label, Editor = editor, Actions = actions, EditActions = editActions };
        edit.Click += (_, _) => BeginEdit(row);
        remove.Click += (_, _) => RemoveWord(row);
        save.Click += (_, _) => SaveEdit(row);
        cancel.Click += (_, _) => EndEdit(row);
        editor.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; SaveEdit(row); }
            else if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; EndEdit(row); }
        };
        _rows[word] = row;
        return row;
    }

    private static Button IconButton(string glyph, string toolTip, string name, string automationId)
    {
        var button = new Button
        {
            Style = AppStyle("IconButtonStyle"), Content = new FontIcon { Glyph = glyph, FontSize = 16 }
        };
        ToolTipService.SetToolTip(button, toolTip);
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private void BeginEdit(WordRow row)
    {
        if (!_state.IsIdle) return;
        if (_editingRow is { } other && other != row) EndEdit(other);
        _editingRow = row;
        ShowFieldError("");
        row.Label.Visibility = Visibility.Collapsed;
        row.Actions.Visibility = Visibility.Collapsed;
        row.Editor.Text = row.Word;
        row.Editor.Visibility = Visibility.Visible;
        row.EditActions.Visibility = Visibility.Visible;
        row.Editor.Focus(FocusState.Programmatic);
        row.Editor.SelectAll();
    }

    private void EndEdit(WordRow row)
    {
        if (_editingRow == row) _editingRow = null;
        ShowFieldError("");
        row.Editor.Visibility = Visibility.Collapsed;
        row.EditActions.Visibility = Visibility.Collapsed;
        row.Label.Visibility = Visibility.Visible;
        row.Actions.Visibility = Visibility.Visible;
        if (row.Root.IsLoaded && FocusManager.FindFirstFocusableElement(row.Actions) is Control control)
            control.Focus(FocusState.Programmatic);
    }

    private void SaveEdit(WordRow row)
    {
        if (!_state.IsIdle) return;
        string updated;
        try
        {
            var dictionary = _state.Settings.CustomDictionary;
            var text = CustomVocabulary.Normalize(row.Editor.Text);
            if (text == row.Word) { EndEdit(row); return; }
            if (CustomVocabulary.ModelCompatibilityError(_state.SelectedModel, text) is { } compatibilityError)
                throw new InvalidDataException(compatibilityError);
            updated = CustomVocabulary.ReplaceEntry(dictionary, row.Word, text);
        }
        catch (Exception failure) when (failure is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ShowFieldError(failure.Message);
            return;
        }
        if (ApplyDictionary(updated)) ShowFieldError("");
    }

    private void AddWordClicked(object sender, RoutedEventArgs args) => AddFromField();

    private void SearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter) return;
        args.Handled = true;
        AddFromField();
    }

    private void AddFromField()
    {
        if (!_state.IsIdle || !CanAdd(SearchBox.Text)) return;
        string entry, updated;
        try
        {
            entry = CustomVocabulary.Normalize(SearchBox.Text);
            if (entry.Contains('\n')) throw new InvalidDataException("Enter one word or phrase. Use Import to add a list.");
            if (CustomVocabulary.ModelCompatibilityError(_state.SelectedModel, entry) is { } compatibilityError)
                throw new InvalidDataException(compatibilityError);
            updated = CustomVocabulary.AddEntries(_state.Settings.CustomDictionary, entry);
        }
        catch (Exception failure) when (failure is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ShowFieldError(failure.Message);
            return;
        }
        if (!ApplyDictionary(updated)) return;
        ShowFieldError("");
        SearchBox.Text = "";
        _notices.Show(AppNotice.Confirmation($"Added \"{entry}\"."));
    }

    // A word is addable when the field holds text that is not already in the dictionary, ignoring case.
    private bool CanAdd(string text)
    {
        var query = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return query.Length > 0 && !_state.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Contains(query, StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateAddButton()
    {
        var text = SearchBox.Text.Trim();
        var addable = CanAdd(text);
        AddWordButton.IsEnabled = _state.IsIdle && addable;
        AddWordLabel.Text = addable ? $"Add \"{text}\"" : "Add";
    }

    private void ShowFieldError(string error)
    {
        FieldValidationText.Text = error;
        FieldValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (WordsList is null) return;
        ShowFieldError("");
        RenderWords();
        UpdateAddButton();
    }

    private void RemoveWord(WordRow row)
    {
        if (!_state.IsIdle) return;
        var word = row.Word;
        var index = WordsList.Items.IndexOf(row.Root);
        string after;
        try { after = CustomVocabulary.RemoveEntry(_state.Settings.CustomDictionary, word); }
        catch (InvalidOperationException error) { _session.ReportUiError(error, NoticeArea.Dictionary); return; }
        if (!ApplyDictionary(after)) return;
        _notices.Show(AppNotice.Confirmation($"Removed \"{word}\".", new AppNoticeAction("Undo", () => RestoreWord(word))));
        if (WordsList.Items.Count == 0) { SearchBox.Focus(FocusState.Programmatic); return; }
        var next = (UIElement)WordsList.Items[Math.Min(Math.Max(index, 0), WordsList.Items.Count - 1)];
        WordsList.UpdateLayout();
        if (FocusManager.FindFirstFocusableElement(next) is Control control) control.Focus(FocusState.Programmatic);
    }

    // Undo adds the word back to the current dictionary, so words added since the removal are kept.
    private void RestoreWord(string word)
    {
        string updated;
        try { updated = CustomVocabulary.AddEntries(_session.State.Settings.CustomDictionary, word); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            _session.ReportUiError(error, NoticeArea.Dictionary);
            return;
        }
        ApplyDictionary(updated);
    }

    private bool ApplyDictionary(string dictionary)
    {
        try
        {
            _session.SetCustomDictionary(dictionary);
            _state = _session.State;
            Render();
            return true;
        }
        catch (Exception error)
        {
            _session.ReportUiError(error, NoticeArea.Dictionary);
            _state = _session.State;
            Render();
            return false;
        }
    }

    private async void ImportWordsClicked(object sender, RoutedEventArgs args)
    {
        if (_editorOpen || !_state.IsIdle) return;
        _editorOpen = true;
        try
        {
            EntryDialog.XamlRoot = XamlRoot;
            EntryBox.Text = "";
            ValidateEditor();
            await EntryDialog.ShowAsync();
        }
        catch (Exception error) { _session.ReportUiError(error, NoticeArea.Dictionary); }
        finally
        {
            _editorOpen = false;
            ImportWordsButton.Focus(FocusState.Programmatic);
        }
    }

    private string? ImportedDictionary(out string error)
    {
        error = "";
        if (!_session.State.IsIdle)
        {
            error = "Finish the current operation to change your dictionary.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(EntryBox.Text)) return null;
        try
        {
            var dictionary = _session.State.Settings.CustomDictionary;
            var text = CustomVocabulary.Normalize(EntryBox.Text);
            if (CustomVocabulary.ModelCompatibilityError(_state.SelectedModel, text) is { } compatibilityError)
                throw new InvalidDataException(compatibilityError);
            var updated = CustomVocabulary.AddEntries(dictionary, text);
            if (updated != dictionary) return updated;
            error = "These words are already in your dictionary.";
        }
        catch (Exception failure) when (failure is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            error = failure.Message;
        }
        return null;
    }

    private void ValidateEditor()
    {
        if (!_editorOpen) return;
        var dictionary = ImportedDictionary(out var error);
        EntryDialog.IsPrimaryButtonEnabled = dictionary is not null;
        EntryBox.IsEnabled = _state.IsIdle;
        EditorValidationText.Text = error;
        EditorValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var count = dictionary is null ? 0 : dictionary.Split('\n').Length -
            _session.State.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        EditorPreviewText.Text = count > 0 ? $"{count} new {(count == 1 ? "entry" : "entries")}. Duplicates are skipped." : "";
        EditorPreviewText.Visibility = EditorPreviewText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EntryDialogPrimaryClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var dictionary = ImportedDictionary(out var error);
        if (dictionary is null)
        {
            args.Cancel = true;
            EditorValidationText.Text = error;
            EditorValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        if (ApplyDictionary(dictionary)) return;
        args.Cancel = true;
        EditorValidationText.Text = _session.State.Notice.Message;
        EditorValidationText.Visibility = Visibility.Visible;
    }

    private void EntryTextChanged(object sender, TextChangedEventArgs args) => ValidateEditor();
}
