using System.Collections.ObjectModel;
using Ansible.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ansible;

public sealed partial class CustomWordsPage : Page
{
    private const string SearchGlyph = "";
    private readonly DictationSession _session;
    private readonly Action _openModels;
    private readonly AppNotices _notices;
    private readonly ObservableCollection<DictionaryWordRowModel> _rows = [];
    private UiSessionObserver? _observer;
    private SessionSnapshot _state;
    private string? _renderedDictionary;
    private DictionaryWordRow? _editingRow;
    private bool _editorOpen;

    internal CustomWordsPage(DictationSession session, Action openModels, AppNotices notices)
    {
        _session = session;
        _notices = notices;
        _openModels = openModels;
        _state = session.State;
        InitializeComponent();
        WordsList.ItemsSource = _rows;
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
        if (_renderedDictionary != dictionary)
        {
            _renderedDictionary = dictionary;
            RenderWords();
        }
        RenderCompatibility(dictionary);
        BusyText.Visibility = _state.IsIdle ? Visibility.Collapsed : Visibility.Visible;
        ImportWordsMenuItem.IsEnabled = _state.IsIdle;
        WordsList.IsEnabled = _state.IsIdle;
        UpdateAddButton();
        ValidateEditor();
    }

    private void RenderCompatibility(string dictionary)
    {
        var model = _state.SelectedModel;
        string? title = null;
        var message = "";
        if (model is null) title = "Choose a speech model to use custom words.";
        else if (!model.SupportsCustomDictionary)
        {
            title = $"{model.DisplayName ?? model.Id} ignores custom words.";
            var alternative = ModelSelectionPolicy.DictationModels(_state.Models).FirstOrDefault(candidate =>
                candidate.SupportsCustomDictionary && candidate.Id != model.Id);
            message = alternative is null ? "Install a model that supports them in Speech models."
                : $"{alternative.DisplayName ?? alternative.Id} is installed and uses them.";
        }
        else title = CustomVocabulary.ModelCompatibilityError(model, dictionary);

        CompatibilityNotice.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        if (title is not null)
            CompatibilityNotice.Show(new NoticeContent(NoticeSeverity.Warning, title, message,
                [new("Speech models", _openModels)], IsClosable: false));
    }

    private void RenderWords()
    {
        var entries = _state.Settings.CustomDictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var query = SearchBox.Text.Trim();
        var filtered = entries.Where(word => word.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var keep = filtered.ToHashSet(StringComparer.Ordinal);
        for (var index = _rows.Count - 1; index >= 0; index--)
        {
            if (keep.Contains(_rows[index].Word)) continue;
            if (_editingRow?.Word == _rows[index].Word) _editingRow = null;
            _rows.RemoveAt(index);
        }
        for (var index = 0; index < filtered.Length; index++)
        {
            if (index < _rows.Count && _rows[index].Word == filtered[index]) continue;
            var existing = _rows.FirstOrDefault(row => row.Word == filtered[index]);
            if (existing is null) _rows.Insert(index, new DictionaryWordRowModel(filtered[index]));
            else _rows.Move(_rows.IndexOf(existing), index);
        }
        CountText.Text = $"{entries.Length} of {CustomVocabulary.MaximumEntries} words";
        EmptyWords.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Length == 0)
        {
            EmptyWords.Glyph = "";
            EmptyWords.Title = "No words yet";
            EmptyWords.Detail = "Add names, acronyms, and terms the model gets wrong.";
        }
        else if (filtered.Length == 0)
        {
            EmptyWords.Glyph = SearchGlyph;
            EmptyWords.Title = "No matching words";
            EmptyWords.Detail = $"Press Enter to add \"{query}\".";
        }
    }

    private void BeginEdit(DictionaryWordRow row)
    {
        if (!_state.IsIdle) return;
        if (_editingRow is { } other && other != row) EndEdit(other);
        _editingRow = row;
        ShowFieldError("");
        row.BeginEdit();
    }

    private void EndEdit(DictionaryWordRow row)
    {
        if (_editingRow == row) _editingRow = null;
        ShowFieldError("");
        row.EndEdit();
    }

    private void SaveEdit(DictionaryWordRow row)
    {
        if (!_state.IsIdle) return;
        var renamed = CustomVocabulary.Normalize(row.EditedWord);
        if (!ApplyEdit(new CustomVocabulary.Edit.Replace(row.Word, row.EditedWord), _state.SelectedModel, out var error))
        {
            ShowFieldError(error);
            return;
        }
        if (_editingRow == row) { EndEdit(row); return; }
        ShowFieldError("");
        FocusRow(_rows.IndexOf(_rows.FirstOrDefault(model => model.Word == renamed)!));
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
        if (!_state.IsIdle || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        var before = _session.State.Settings.CustomDictionary;
        if (!ApplyEdit(new CustomVocabulary.Edit.Add(SearchBox.Text, SingleEntry: true), _state.SelectedModel, out var error))
        {
            ShowFieldError(error);
            return;
        }
        if (_session.State.Settings.CustomDictionary == before) return;
        var entry = CustomVocabulary.Normalize(SearchBox.Text);
        ShowFieldError("");
        SearchBox.Text = "";
        _notices.Confirm($"Added \"{entry}\".");
    }

    private bool CanAdd(string text)
    {
        var dictionary = _state.Settings.CustomDictionary;
        var (updated, _) = CustomVocabulary.TryApply(dictionary,
            new CustomVocabulary.Edit.Add(text, SingleEntry: true), _state.SelectedModel);
        return updated is not null && updated != dictionary;
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
        CountText.Visibility = error.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (WordsList is null) return;
        ShowFieldError("");
        RenderWords();
        UpdateAddButton();
    }

    private void RemoveWord(DictionaryWordRow row)
    {
        if (!_state.IsIdle) return;
        var word = row.Word;
        var index = row.Model is { } model ? _rows.IndexOf(model) : -1;
        if (!ApplyEdit(new CustomVocabulary.Edit.Remove(word), _state.SelectedModel, out _)) return;
        _notices.Confirm($"Removed \"{word}\".", new NoticeAction("Undo", () => RestoreWord(word)));
        FocusRow(Math.Min(Math.Max(index, 0), _rows.Count - 1));
    }

    private void FocusRow(int index)
    {
        if (index < 0 || index >= _rows.Count) { SearchBox.Focus(FocusState.Programmatic); return; }
        WordsList.UpdateLayout();
        if (WordsList.ContainerFromIndex(index) is { } container && FocusManager.FindFirstFocusableElement(container) is Control control)
            control.Focus(FocusState.Programmatic);
        else SearchBox.Focus(FocusState.Programmatic);
    }

    private void RestoreWord(string word) => ApplyEdit(new CustomVocabulary.Edit.Add(word), null, out _);

    private bool ApplyEdit(CustomVocabulary.Edit edit, AudioModel? model, out string error)
    {
        var dictionary = _session.State.Settings.CustomDictionary;
        var (updated, failure) = CustomVocabulary.TryApply(dictionary, edit, model);
        if (updated is not null)
        {
            if (updated != dictionary) return ApplyDictionary(updated, out error);
            error = "";
            _notices.Set("dictionary-error", null);
            return true;
        }
        error = failure!;
        ReportError(error);
        return false;
    }

    private void ReportError(string error) => _notices.Set("dictionary-error",
        new NoticeContent(NoticeSeverity.Error, "Couldn't update dictionary", error), NoticeArea.Dictionary);

    private bool ApplyDictionary(string dictionary, out string error)
    {
        error = "";
        try
        {
            _session.SetCustomDictionary(dictionary);
            _state = _session.State;
            Render();
            _notices.Set("dictionary-error", null);
            return true;
        }
        catch (Exception failure)
        {
            error = failure.Message;
            ReportError(error);
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
            EntryDialog.FollowWindowTheme(XamlRoot);
            EntryBox.Text = "";
            ValidateEditor();
            await EntryDialog.ShowAsync();
        }
        catch (Exception error) { ReportError(error.Message); }
        finally
        {
            _editorOpen = false;
            MoreOptionsButton.Focus(FocusState.Programmatic);
        }
    }

    private string? ImportedDictionary(out string error)
    {
        error = "";
        var state = _session.State;
        if (!state.IsIdle)
        {
            error = "Finish the current operation to change your dictionary.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(EntryBox.Text)) return null;
        var dictionary = state.Settings.CustomDictionary;
        var (updated, failure) = CustomVocabulary.TryApply(dictionary,
            new CustomVocabulary.Edit.Add(EntryBox.Text), state.SelectedModel);
        if (failure is not null) error = failure;
        else if (updated != dictionary) return updated;
        else error = "These words are already in your dictionary.";
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
        if (dictionary is not null && ApplyDictionary(dictionary, out error)) return;
        args.Cancel = true;
        if (error.Length > 0) ReportError(error);
        EditorValidationText.Text = error;
        EditorValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EntryTextChanged(object sender, TextChangedEventArgs args) => ValidateEditor();
}
