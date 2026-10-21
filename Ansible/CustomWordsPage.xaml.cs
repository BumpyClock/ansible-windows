using System.Collections.ObjectModel;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ansible;

public sealed partial class CustomWordsPage : Page
{
    private enum EditorMode { Add, Import, Edit }

    private readonly DictationSession _session;
    private readonly Action _openModels;
    private readonly ObservableCollection<DictionaryWord> _words = [];
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private string? _renderedDictionary;
    private string? _undoBefore;
    private string? _undoAfter;
    private EditorMode _editorMode;
    private string? _editingWord;
    private bool _editorOpen;

    internal CustomWordsPage(DictationSession session, Action openModels)
    {
        _session = session;
        _openModels = openModels;
        _state = session.State;
        InitializeComponent();
        WordsList.ItemsSource = _words;
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

    private void Render()
    {
        var dictionary = _state.Settings.CustomDictionary;
        if (_undoAfter is not null && _undoAfter != dictionary) _undoBefore = _undoAfter = null;
        if (_renderedDictionary != dictionary)
        {
            _renderedDictionary = dictionary;
            RenderWords();
        }
        var model = _state.SelectedModel;
        ModelStatusText.Text = model is null ? "Choose a speech model to use custom words."
            : !model.SupportsCustomDictionary ? $"{model.DisplayName ?? model.Id} does not support custom words."
            : CustomVocabulary.ModelCompatibilityError(model, dictionary) ?? "";
        ModelStatusText.Visibility = ModelStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        BusyText.Visibility = _state.IsIdle ? Visibility.Collapsed : Visibility.Visible;
        AddWordsButton.IsEnabled = ImportWordsButton.IsEnabled = AddFirstWordButton.IsEnabled = _state.IsIdle;
        UndoButton.Visibility = _undoBefore is null ? Visibility.Collapsed : Visibility.Visible;
        UndoButton.IsEnabled = _state.IsIdle;
        foreach (var word in _words)
            if (WordsList.ContainerFromItem(word) is ListViewItem container) container.IsEnabled = _state.IsIdle;
        ValidateEditor();
        RenderNotice();
    }

    private void RenderWords()
    {
        var entries = _state.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var query = SearchBox.Text.Trim();
        var filtered = entries.Where(word => word.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        // Preserve unchanged rows so dictionary updates do not discard their focused controls.
        for (var index = _words.Count - 1; index >= 0; index--)
            if (!filtered.Contains(_words[index].Word, StringComparer.Ordinal)) _words.RemoveAt(index);
        for (var index = 0; index < filtered.Length; index++)
            if (index >= _words.Count || _words[index].Word != filtered[index])
                _words.Insert(index, new DictionaryWord(filtered[index]));
        CountText.Text = query.Length == 0
            ? $"{entries.Length} of {CustomVocabulary.MaximumEntries} entries"
            : $"{filtered.Length} of {entries.Length} entries shown";
        EmptyState.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        WordsList.Visibility = filtered.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = entries.Length == 0 ? "Your dictionary starts here" : "No matching words";
        EmptyHelp.Text = entries.Length == 0 ? "Add names and terms you use, or import a list."
            : "Try another search. Your other words are still in your dictionary.";
        AddFirstWordButton.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task OpenEditorAsync(EditorMode mode, Control? source, string? word = null)
    {
        if (_editorOpen || !_state.IsIdle) return;
        _editorOpen = true;
        _editorMode = mode;
        _editingWord = word;
        try
        {
            var importing = mode == EditorMode.Import;
            EntryDialog.XamlRoot = XamlRoot;
            EntryDialog.Title = mode switch { EditorMode.Edit => "Edit word", EditorMode.Import => "Import words", _ => "Add a word" };
            EntryDialog.PrimaryButtonText = mode switch { EditorMode.Edit => "Save", EditorMode.Import => "Import words", _ => "Add word" };
            EntryDialog.DefaultButton = importing ? ContentDialogButton.None : ContentDialogButton.Primary;
            EditorHelpText.Text = importing ? "Paste your list below, with one name, word, or phrase per line."
                : "Use the spelling you want recognition to pick up.";
            EntryBox.Header = importing ? "Words to import" : "Word or phrase";
            EntryBox.PlaceholderText = importing ? "One entry per line" : "For example, a project name";
            AutomationProperties.SetName(EntryBox, importing ? "Words to import, one per line" : "Word or phrase");
            EntryBox.AcceptsReturn = importing;
            EntryBox.MinHeight = importing ? 160 : 40;
            EntryBox.Text = word ?? "";
            ValidateEditor();
            await EntryDialog.ShowAsync();
        }
        catch (Exception error) { _session.ReportUiError(error); }
        finally
        {
            _editorOpen = false;
            _editingWord = null;
            if (source is not { IsLoaded: true } || !source.Focus(FocusState.Programmatic))
                AddWordsButton.Focus(FocusState.Programmatic);
        }
    }

    private string? EditorDictionary(out string error)
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
            if (_editorMode != EditorMode.Import && text.Contains('\n'))
                throw new InvalidDataException("Enter one word or phrase. Use Import to add a list.");
            if (CustomVocabulary.ModelCompatibilityError(_state.SelectedModel, text) is { } compatibilityError)
                throw new InvalidDataException(compatibilityError);
            var updated = _editorMode == EditorMode.Edit
                ? CustomVocabulary.ReplaceEntry(dictionary, _editingWord!, text)
                : CustomVocabulary.AddEntries(dictionary, text);
            if (updated != dictionary) return updated;
            if (_editorMode != EditorMode.Edit) error = "These words are already in your dictionary.";
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
        var dictionary = EditorDictionary(out var error);
        EntryDialog.IsPrimaryButtonEnabled = dictionary is not null;
        EntryBox.IsEnabled = _state.IsIdle;
        EditorValidationText.Text = error;
        EditorValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var count = dictionary is null ? 0 : dictionary.Split('\n').Length -
            _session.State.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        EditorPreviewText.Text = _editorMode == EditorMode.Import && count > 0
            ? $"{count} new {(count == 1 ? "entry" : "entries")}. Duplicates are skipped." : "";
        EditorPreviewText.Visibility = EditorPreviewText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EntryDialogPrimaryClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var dictionary = EditorDictionary(out var error);
        if (dictionary is null)
        {
            args.Cancel = true;
            EditorValidationText.Text = error;
            EditorValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        if (ApplyDictionary(dictionary, "")) return;
        args.Cancel = true;
        EditorValidationText.Text = _session.State.Notice.Message;
        EditorValidationText.Visibility = Visibility.Visible;
    }

    private bool ApplyDictionary(string dictionary, string feedback)
    {
        try
        {
            _session.SetCustomDictionary(dictionary);
            _state = _session.State;
            FeedbackText.Text = _state.Notice.Kind == NoticeKind.Success ? feedback : "";
            Render();
            return true;
        }
        catch (Exception error)
        {
            _session.ReportUiError(error);
            _state = _session.State;
            Render();
            return false;
        }
    }

    private async void AddWordsClicked(object sender, RoutedEventArgs args) => await OpenEditorAsync(EditorMode.Add, sender as Control);
    private async void ImportWordsClicked(object sender, RoutedEventArgs args) => await OpenEditorAsync(EditorMode.Import, sender as Control);
    private async void EditWordClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string word } button) await OpenEditorAsync(EditorMode.Edit, button, word);
    }

    private void RemoveWordClicked(object sender, RoutedEventArgs args)
    {
        if (!_state.IsIdle || sender is not Button { Tag: string word }) return;
        var before = _state.Settings.CustomDictionary;
        var index = _words.IndexOf(new DictionaryWord(word));
        string after;
        try { after = CustomVocabulary.RemoveEntry(before, word); }
        catch (InvalidOperationException error) { _session.ReportUiError(error); return; }
        if (!ApplyDictionary(after, $"Removed \"{word}\".")) return;
        _undoBefore = before;
        _undoAfter = after;
        Render();
        if (_words.Count == 0) AddWordsButton.Focus(FocusState.Programmatic);
        else
        {
            var next = _words[Math.Min(Math.Max(index, 0), _words.Count - 1)];
            WordsList.ScrollIntoView(next);
            WordsList.UpdateLayout();
            if (WordsList.ContainerFromItem(next) is ListViewItem container &&
                FocusManager.FindFirstFocusableElement(container) is Control control) control.Focus(FocusState.Programmatic);
        }
    }

    private void UndoClicked(object sender, RoutedEventArgs args)
    {
        if (!_state.IsIdle || _undoBefore is null) return;
        if (ApplyDictionary(_undoBefore, "")) AddWordsButton.Focus(FocusState.Programmatic);
    }

    private void EntryTextChanged(object sender, TextChangedEventArgs args) => ValidateEditor();
    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (WordsList is not null) RenderWords();
    }

    private void WordsContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue) args.ItemContainer.IsEnabled = _state.IsIdle;
    }

    private void ModelsClicked(object sender, RoutedEventArgs args) => _openModels();

    private void DictionaryHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < (double)Application.Current.Resources["CompactPageThreshold"];
        Grid.SetColumnSpan(DictionaryPageTitle, compact ? 2 : 1);
        Grid.SetRow(DictionaryHeaderActions, compact ? 1 : 0);
        Grid.SetColumn(DictionaryHeaderActions, compact ? 0 : 1);
        Grid.SetColumnSpan(DictionaryHeaderActions, compact ? 2 : 1);
        DictionaryHeaderActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        DictionaryHeaderActions.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private void RenderNotice()
    {
        var notice = _state.Notice;
        StatusInfo.IsOpen = notice.ShouldDisplay;
        StatusInfo.Visibility = StatusInfo.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        StatusInfo.Title = notice.Title;
        StatusInfo.Message = notice.Message;
        StatusInfo.Severity = notice.Kind switch
        {
            NoticeKind.Error => InfoBarSeverity.Error,
            NoticeKind.Warning => InfoBarSeverity.Warning,
            NoticeKind.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational
        };
    }

    private void StatusInfoClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton) _session.DismissNotice(_state.Notice);
        sender.Visibility = Visibility.Collapsed;
    }
}
