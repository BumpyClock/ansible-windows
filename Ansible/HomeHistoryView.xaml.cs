using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace Ansible;

public sealed partial class HomeHistoryView : UserControl
{
    private static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2);
    private const string CopyGlyph = "\uE8C8";
    private const string CopiedGlyph = "\uE73E";
    private const double ActionsRestOpacity = 0.65;
    private const double NarrowRowWidth = 420;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private readonly Dictionary<Guid, HistoryRowView> _rows = [];
    private readonly Dictionary<string, Grid> _groupHeaders = new(StringComparer.Ordinal);
    private HistoryRowView? _copiedRow;
    private TransitionCollection? _transitions;
    private long _transitionGeneration;
    private bool _narrow;

    internal event Action? QueryChanged;
    internal event Action<HomeHistoryItem>? CopyRequested;
    internal event Action<HomeHistoryItem>? RemoveRequested;

    internal string Query => HistorySearchBox.Text.Trim();

    public HomeHistoryView()
    {
        InitializeComponent();
        _copiedTimer.Tick += (_, _) => RestoreCopied();
        Unloaded += (_, _) => RestoreCopied();
    }

    /// <summary>
    /// Renders the projection. Rows are reused by operation, so with <paramref name="animate"/> a new dictation,
    /// a removal, or a delivery outcome changes only what changed. Search and date changes rebuild without
    /// transitions so every row does not animate at once.
    /// </summary>
    internal void Render(HomeHistoryViewModel model, bool animate)
    {
        HistoryCount.Text = $"{model.TodayCount:N0} {(model.TodayCount == 1 ? "dictation" : "dictations")} today";
        OversizedNotice.Text = $"A transcript over {HomeDictationHistory.MaximumCharacters:N0} characters stays in the current result and is not added to session history.";
        OversizedNotice.Visibility = model.OversizedCurrentResult ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = model.HasMatches ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = model.HasHistory ? "No matching transcripts." : "No completed dictations in this app session.";
        EmptyDetail.Text = model.HasHistory ? "Try a different search." : "Choose Dictate to start your history.";
        HistoryContent.VerticalAlignment = model.HasMatches ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        animate &= HistoryRows.Children.Count > 0;

        var desired = new List<UIElement>();
        var liveRows = new HashSet<Guid>();
        var liveGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in model.Groups)
        {
            liveGroups.Add(group.Label);
            desired.Add(GroupHeader(group.Label));
            foreach (var entry in group.Entries)
            {
                var id = entry.Item.OperationId;
                liveRows.Add(id);
                if (!_rows.TryGetValue(id, out var row))
                {
                    row = CreateRow(entry.Item);
                    _rows[id] = row;
                }
                row.Update(entry, this);
                desired.Add(row.Root);
            }
        }

        if (!animate || !TryReconcile(desired))
        {
            var transitions = HistoryRows.ChildrenTransitions;
            if (transitions is not null) { _transitions = transitions; }
            HistoryRows.ChildrenTransitions = null;
            HistoryRows.Children.Clear();
            foreach (var element in desired) { HistoryRows.Children.Add(element); }
            // Rows leaving the tree do not reliably raise PointerExited or LostFocus; start rebuilt rows at rest.
            foreach (var row in _rows.Values.Where(row => row.Hovered || row.Focused))
            {
                row.Hovered = false;
                row.Focused = false;
                UpdateRowState(row);
            }
            var generation = ++_transitionGeneration;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (generation == _transitionGeneration) { HistoryRows.ChildrenTransitions = _transitions; }
            });
        }

        // Filtered-out rows stay cached so clearing a search restores them without rebuilding.
        var all = model.AllOperationIds;
        foreach (var id in _rows.Keys.Where(id => !all.Contains(id)).ToArray())
        {
            if (ReferenceEquals(_copiedRow, _rows[id])) { RestoreCopied(); }
            _rows.Remove(id);
        }
        foreach (var label in _groupHeaders.Keys.Where(label => !liveGroups.Contains(label)).ToArray())
            _groupHeaders.Remove(label);
    }

    /// <summary>Shows the copied state on a row after the page put its transcript on the clipboard.</summary>
    internal void ShowCopied(Guid operationId)
    {
        RestoreCopied();
        if (!_rows.TryGetValue(operationId, out var row)) { return; }
        _copiedRow = row;
        row.CopyIcon.Glyph = CopiedGlyph;
        ToolTipService.SetToolTip(row.Copy, "Copied");
        _copiedTimer.Start();
    }

    /// <summary>Removes and inserts only the changed children. Fails when existing children would need to move.</summary>
    private bool TryReconcile(List<UIElement> desired)
    {
        var children = HistoryRows.Children;
        var keep = desired.ToHashSet();
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (!keep.Contains(children[index])) { children.RemoveAt(index); }
        }
        var current = children.ToHashSet();
        var position = 0;
        foreach (var element in desired)
        {
            if (position < children.Count && ReferenceEquals(children[position], element)) { position++; continue; }
            if (current.Contains(element)) { return false; }
            children.Insert(position++, element);
        }
        return children.Count == desired.Count;
    }

    private Grid GroupHeader(string label)
    {
        if (_groupHeaders.TryGetValue(label, out var header)) { return header; }
        header = new Grid
        {
            Margin = (Thickness)Application.Current.Resources["HistoryGroupMargin"],
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = label, Style = AppStyle("HistoryGroupLabelStyle") });
        var line = new Border { Style = AppStyle("HistoryDividerStyle") };
        Grid.SetColumn(line, 1);
        header.Children.Add(line);
        _groupHeaders[label] = header;
        return header;
    }

    private HistoryRowView CreateRow(HomeHistoryItem item)
    {
        var resources = Application.Current.Resources;
        var row = new HistoryRowView(item);
        var animations = _uiSettings.AnimationsEnabled;
        var fast = TimeSpan.FromMilliseconds((double)resources["MotionFastMilliseconds"]);
        row.Root = new Border { Style = AppStyle("HistoryRowStyle") };
        if (animations) { row.Root.BackgroundTransition = new BrushTransition { Duration = fast }; }
        row.Layout = new Grid { ColumnSpacing = (double)resources["HistoryRowColumnGap"] };
        row.Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)resources["HistoryTimeColumnWidth"] });
        row.Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        row.Time = new TextBlock { Style = AppStyle("HistoryTimeTextStyle"), VerticalAlignment = VerticalAlignment.Top };
        row.Layout.Children.Add(row.Time);

        row.Transcript = new TextBlock { Text = item.Text, Style = AppStyle("HistoryTranscriptTextStyle") };
        row.Meta = new TextBlock { Style = AppStyle("HistoryMetaTextStyle"), VerticalAlignment = VerticalAlignment.Center };
        row.TagIcon = new FontIcon();
        AutomationProperties.SetAccessibilityView(row.TagIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        row.TagText = new TextBlock();
        var tagContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        tagContent.Children.Add(row.TagIcon);
        tagContent.Children.Add(row.TagText);
        row.TagPill = new Border { Child = tagContent, Visibility = Visibility.Collapsed };
        // A left-aligned star column lets the meta line trim before it pushes the tag out of the row.
        var metaLine = new Grid { ColumnSpacing = (double)resources["HistoryMetaGap"], HorizontalAlignment = HorizontalAlignment.Left };
        metaLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaLine.Children.Add(row.Meta);
        Grid.SetColumn(row.TagPill, 1);
        metaLine.Children.Add(row.TagPill);
        row.Body = new StackPanel { Spacing = 6 };
        row.Body.Children.Add(row.Transcript);
        row.Body.Children.Add(metaLine);
        row.Layout.Children.Add(row.Body);

        row.Actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = (double)resources["HistoryActionGap"],
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _accessibility.HighContrast ? 1 : ActionsRestOpacity
        };
        if (animations) { row.Actions.OpacityTransition = new ScalarTransition { Duration = fast }; }
        row.CopyIcon = new FontIcon { Glyph = CopyGlyph, FontSize = (double)resources["IconButtonGlyphSize"] };
        row.Copy = CreateIconButton(row.CopyIcon);
        row.Copy.Click += (_, _) => CopyRequested?.Invoke(row.Item);
        row.More = CreateIconButton(new FontIcon { Glyph = "\uE712", FontSize = (double)resources["IconButtonGlyphSize"] });
        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = "Copy transcript", Icon = new FontIcon { Glyph = CopyGlyph } };
        copyItem.Click += (_, _) => CopyRequested?.Invoke(row.Item);
        var removeItem = new MenuFlyoutItem { Text = "Remove from history", Icon = new FontIcon { Glyph = "\uE74D" } };
        removeItem.Click += (_, _) => Remove(row);
        menu.Items.Add(copyItem);
        menu.Items.Add(removeItem);
        menu.Opened += (_, _) => { row.MenuOpen = true; UpdateRowState(row); };
        menu.Closed += (_, _) => { row.MenuOpen = false; UpdateRowState(row); };
        row.More.Flyout = menu;
        row.Actions.Children.Add(row.Copy);
        row.Actions.Children.Add(row.More);
        Grid.SetColumn(row.Actions, 2);
        Grid.SetRowSpan(row.Actions, 2);
        row.Layout.Children.Add(row.Actions);
        row.Root.Child = row.Layout;

        row.Root.PointerEntered += (_, _) => { row.Hovered = true; UpdateRowState(row); };
        row.Root.PointerExited += (_, _) => { row.Hovered = false; UpdateRowState(row); };
        row.Root.PointerCanceled += (_, _) => { row.Hovered = false; UpdateRowState(row); };
        row.Root.GotFocus += (_, _) => { row.Focused = true; UpdateRowState(row); };
        row.Root.LostFocus += (_, _) => { row.Focused = false; UpdateRowState(row); };
        ApplyRowLayout(row, _narrow);
        return row;
    }

    private Button CreateIconButton(FontIcon icon)
    {
        var button = new Button { Style = AppStyle("IconButtonStyle"), Content = icon };
        PageCanvas.ApplyBrandInteractionBrushes(button, "ButtonBackgroundPointerOver", "ButtonBackgroundPressed");
        return button;
    }

    private void UpdateRowState(HistoryRowView row)
    {
        var active = row.Hovered || row.MenuOpen;
        // High Contrast hover is the system highlight, which would hide window-text transcript. Keep rows plain there.
        var highContrast = _accessibility.HighContrast;
        if (active && !highContrast) { row.Root.Background = HoverFillAnchor.Background; }
        else { row.Root.ClearValue(Border.BackgroundProperty); }
        row.Actions.Opacity = active || row.Focused || highContrast ? 1 : ActionsRestOpacity;
    }

    private static void ApplyRowLayout(HistoryRowView row, bool narrow)
    {
        // Narrow rows put the time above the transcript instead of clipping a fixed time column.
        Grid.SetColumnSpan(row.Time, narrow ? 2 : 1);
        Grid.SetRow(row.Body, narrow ? 1 : 0);
        Grid.SetRowSpan(row.Body, narrow ? 1 : 2);
        Grid.SetColumn(row.Body, narrow ? 0 : 1);
        Grid.SetColumnSpan(row.Body, narrow ? 2 : 1);
        row.Time.Margin = narrow ? new Thickness(0, 0, 0, 4) : new Thickness(0, 3, 0, 0);
    }

    private void HistoryRowsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < NarrowRowWidth;
        if (narrow == _narrow) { return; }
        _narrow = narrow;
        foreach (var row in _rows.Values) { ApplyRowLayout(row, narrow); }
    }

    private void UpdateTag(HistoryRowView row, HomeHistoryTag? tag)
    {
        if (tag is null)
        {
            row.TagPill.Visibility = Visibility.Collapsed;
            return;
        }
        SetStyle(row.TagPill, tag.Inserted ? "HistoryInsertedTagStyle" : "HistoryTagStyle");
        SetStyle(row.TagText, tag.Inserted ? "HistoryInsertedTagTextStyle" : "HistoryTagTextStyle");
        SetStyle(row.TagIcon, tag.Inserted ? "HistoryInsertedTagIconStyle" : "HistoryTagIconStyle");
        row.TagIcon.Glyph = tag.Glyph;
        row.TagText.Text = tag.Label;
        ToolTipService.SetToolTip(row.TagPill, tag.Meaning);
        // Border has no automation peer, so the readable tag text carries the name and meaning.
        AutomationProperties.SetName(row.TagText, $"Insertion: {tag.Label}");
        AutomationProperties.SetHelpText(row.TagText, tag.Meaning);
        row.TagPill.Visibility = Visibility.Visible;
    }

    private void Remove(HistoryRowView row)
    {
        var index = HistoryRows.Children.IndexOf(row.Root);
        RemoveRequested?.Invoke(row.Item);
        if (HistoryRows.Children.Contains(row.Root)) { return; }
        // Keep keyboard users in the list: focus the row that took this one's place, or search when none is left.
        var next = HistoryRows.Children.Skip(Math.Max(0, index)).Concat(HistoryRows.Children.Take(Math.Max(0, index)).Reverse())
            .Select(child => _rows.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Root, child)))
            .FirstOrDefault(candidate => candidate is not null);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (next is not null) { next.More.Focus(FocusState.Keyboard); }
            else { HistorySearchBox.Focus(FocusState.Keyboard); }
        });
    }

    private void RestoreCopied()
    {
        _copiedTimer.Stop();
        if (_copiedRow is not { } row) { return; }
        _copiedRow = null;
        row.CopyIcon.Glyph = CopyGlyph;
        ToolTipService.SetToolTip(row.Copy, "Copy transcript");
    }

    private Style AppStyle(string key) =>
        (Style)(Resources.TryGetValue(key, out var local) ? local : Application.Current.Resources[key]);

    private void SetStyle(FrameworkElement element, string key)
    {
        var style = AppStyle(key);
        if (!ReferenceEquals(element.Style, style)) { element.Style = style; }
    }

    private void HistorySearchChanged(object sender, TextChangedEventArgs args) => QueryChanged?.Invoke();

    private void FocusHistorySearchInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HistorySearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private void HistoryHeaderSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < (double)Application.Current.Resources["HistorySearchStackThreshold"];
        HistoryHeaderGrid.ColumnDefinitions.Clear();
        HistoryHeaderGrid.RowDefinitions.Clear();
        if (narrow)
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0);
            Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 0); Grid.SetColumnSpan(HistorySearchBox, 2); Grid.SetRow(HistorySearchBox, 1);
            Grid.SetColumn(HistorySearchIcon, 0); Grid.SetColumnSpan(HistorySearchIcon, 2); Grid.SetRow(HistorySearchIcon, 1);
            Grid.SetColumn(HistorySearchShortcut, 0); Grid.SetColumnSpan(HistorySearchShortcut, 2); Grid.SetRow(HistorySearchShortcut, 1);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 10;
        }
        else
        {
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HistoryHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = (GridLength)Application.Current.Resources["HistorySearchColumnWidth"] });
            HistoryHeaderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(HistoryTitle, 0); Grid.SetColumn(HistoryCountPill, 1);
            Grid.SetColumn(HistorySearchBox, 3); Grid.SetColumnSpan(HistorySearchBox, 1); Grid.SetRow(HistorySearchBox, 0);
            Grid.SetColumn(HistorySearchIcon, 3); Grid.SetColumnSpan(HistorySearchIcon, 1); Grid.SetRow(HistorySearchIcon, 0);
            Grid.SetColumn(HistorySearchShortcut, 3); Grid.SetColumnSpan(HistorySearchShortcut, 1); Grid.SetRow(HistorySearchShortcut, 0);
            HistorySearchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            HistoryHeaderGrid.RowSpacing = 0;
        }
        HistorySearchBox.Margin = new Thickness(0);
    }

    private sealed class HistoryRowView(HomeHistoryItem item)
    {
        public HomeHistoryItem Item { get; private set; } = item;
        public Border Root = null!;
        public Grid Layout = null!;
        public TextBlock Time = null!;
        public StackPanel Body = null!;
        public TextBlock Transcript = null!;
        public TextBlock Meta = null!;
        public Border TagPill = null!;
        public FontIcon TagIcon = null!;
        public TextBlock TagText = null!;
        public StackPanel Actions = null!;
        public Button Copy = null!;
        public FontIcon CopyIcon = null!;
        public Button More = null!;
        public bool Hovered;
        public bool Focused;
        public bool MenuOpen;
        private bool _rendered;

        public void Update(HomeHistoryEntry entry, HomeHistoryView view)
        {
            var deliveryChanged = !_rendered || Item.Delivery != entry.Item.Delivery;
            Item = entry.Item;
            if (Time.Text != entry.TimeText)
            {
                Time.Text = entry.TimeText;
                // Copy and More names carry the row time so a screen reader can tell rows apart.
                ToolTipService.SetToolTip(Copy, ReferenceEquals(view._copiedRow, this) ? "Copied" : "Copy transcript");
                AutomationProperties.SetName(Copy, $"Copy transcript from {entry.TimeText}");
                ToolTipService.SetToolTip(More, "More actions");
                AutomationProperties.SetName(More, $"More actions for transcript from {entry.TimeText}");
            }
            if (Meta.Text != entry.MetadataText) { Meta.Text = entry.MetadataText; }
            if (deliveryChanged) { view.UpdateTag(this, entry.Tag); }
            _rendered = true;
        }
    }
}
