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
    private const string MicrophoneGlyph = "\uE720";
    private const string SearchGlyph = "\uE721";
    private const double ActionsRestOpacity = 0.65;
    private const double NarrowRowWidth = 420;

    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly DispatcherTimer _copiedTimer = new() { Interval = CopiedDuration };
    private readonly Dictionary<Guid, HistoryRowView> _rows = [];
    private readonly Dictionary<string, Grid> _groupHeaders = new(StringComparer.Ordinal);
    private HistoryRowView? _copiedRow;
    private SolidColorBrush? _restFill;
    private TransitionCollection? _transitions;
    private long _transitionGeneration;
    private bool _narrow;
    private HomeHistoryViewModel? _model;
    private int? _dictations;
    private PageCanvas? _pinCanvas;
    private StickyPin? _headerPin;

    internal event Action? QueryChanged;
    internal event Action<HomeHistoryItem>? CopyRequested;
    internal event Action<HomeHistoryItem>? RemoveRequested;
    internal event Action? EmptyHistoryFocusRequested;

    internal string Query => HistorySearchBox.Text.Trim();

    internal void SetEmptyContext(int? dictations)
    {
        if (_dictations == dictations) return;
        _dictations = dictations;
        RenderEmptyState();
    }

    public HomeHistoryView()
    {
        InitializeComponent();
        _copiedTimer.Tick += (_, _) => RestoreCopied();
        Unloaded += (_, _) => RestoreCopied();
        HistoryContent.BringIntoViewRequested += HistoryContentBringIntoViewRequested;
    }

    internal double StuckContentTop => HistoryHeaderGrid.Padding.Top;

    internal void PinHeader(PageCanvas canvas)
    {
        _pinCanvas = canvas;
        UpdateHeaderPin();
    }

    internal void UnpinHeader()
    {
        _pinCanvas = null;
        UpdateHeaderPin();
    }

    private void UpdateHeaderPin()
    {
        var pinned = _pinCanvas is not null && _model?.HasHistory == true;
        if (pinned == (_headerPin is not null)) return;
        if (pinned)
        {
            HistoryHeaderDivider.Visibility = Visibility.Visible;
            _headerPin = _pinCanvas!.Pin(HistoryHeaderGrid, this, indicator: HistoryHeaderDivider);
        }
        else
        {
            _headerPin!.Dispose();
            _headerPin = null;
            HistoryHeaderDivider.Visibility = Visibility.Collapsed;
        }
    }

    private void HistoryContentBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        var rect = args.TargetRect;
        if (rect.IsEmpty) return;
        var header = HistoryHeaderGrid.ActualHeight;
        args.TargetRect = new Windows.Foundation.Rect(rect.X, rect.Y - header, rect.Width, rect.Height + header);
    }

    internal void Render(HomeHistoryViewModel model, bool animate)
    {
        _model = model;
        var count = model.AllOperationIds.Count;
        var query = Query;
        HistoryCountPill.Text = query.Length > 0 && model.HasHistory
            ? $"{model.Groups.Sum(group => group.Entries.Count):N0} of {count:N0}"
            : $"{count:N0} {(count == 1 ? "transcription" : "transcriptions")}";
        HistoryCountPill.Visibility = model.HasHistory ? Visibility.Visible : Visibility.Collapsed;
        var searchVisibility = model.HasHistory ? Visibility.Visible : Visibility.Collapsed;
        HistorySearchBox.Visibility = searchVisibility;
        HistorySearchIcon.Visibility = searchVisibility;
        HistorySearchShortcut.Visibility = searchVisibility;
        if (HistoryHeaderGrid.RowDefinitions.Count > 1) { HistoryHeaderGrid.RowSpacing = model.HasHistory ? 8 : 0; }
        OversizedNotice.Text = $"A transcript over {HomeDictationHistory.MaximumCharacters:N0} characters stays in the current result and is not added to saved history.";
        OversizedNotice.Visibility = model.OversizedCurrentResult ? Visibility.Visible : Visibility.Collapsed;
        RenderEmptyState();
        UpdateHeaderPin();
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
            // Rows leaving the tree do not reliably raise PointerExited or LostFocus.
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

        var all = model.AllOperationIds;
        foreach (var id in _rows.Keys.Where(id => !all.Contains(id)).ToArray())
        {
            if (ReferenceEquals(_copiedRow, _rows[id])) { RestoreCopied(); }
            _rows.Remove(id);
        }
        foreach (var label in _groupHeaders.Keys.Where(label => !liveGroups.Contains(label)).ToArray())
            _groupHeaders.Remove(label);
    }

    private void RenderEmptyState()
    {
        if (_model is null) return;
        var empty = HomeHistoryProjection.EmptyState(_model, Query, _dictations);
        EmptyMessage.Visibility = empty is null ? Visibility.Collapsed : Visibility.Visible;
        if (empty is null) return;
        var noMatches = empty.Kind == HomeHistoryEmptyKind.NoMatches;
        EmptyMessage.Glyph = noMatches ? SearchGlyph : MicrophoneGlyph;
        EmptyMessage.Title = empty.Title;
        EmptyMessage.Detail = empty.Detail;
    }

    internal void ShowCopied(Guid operationId)
    {
        RestoreCopied();
        if (!_rows.TryGetValue(operationId, out var row)) { return; }
        _copiedRow = row;
        row.CopyIcon.Glyph = CopiedGlyph;
        ToolTipService.SetToolTip(row.Copy, "Copied");
        _copiedTimer.Start();
    }

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
            Margin = (Thickness)Resources["HomeHistoryGroupMargin"],
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new TextBlock { Text = label, Style = AppStyle("HistoryGroupLabelStyle"), CharacterSpacing = 33 });
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
        row.Root = new Border { Style = AppStyle("HomeHistoryRowStyle") };
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
        row.TagHost = new Border { Child = tagContent, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var metaLine = new Grid { ColumnSpacing = (double)resources["HistoryMetaGap"], HorizontalAlignment = HorizontalAlignment.Left };
        metaLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaLine.Children.Add(row.Meta);
        Grid.SetColumn(row.TagHost, 1);
        metaLine.Children.Add(row.TagHost);
        row.Body = new StackPanel { Spacing = 4 };
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
        row.CopyIcon = new FontIcon { Glyph = CopyGlyph, Style = AppStyle("HistoryCopyIconStyle") };
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
        UpdateRowState(row);
        return row;
    }

    private Button CreateIconButton(FontIcon icon)
    {
        return new Button { Style = AppStyle("IconButtonStyle"), Content = icon };
    }

    private void UpdateRowState(HistoryRowView row)
    {
        var active = row.Hovered || row.MenuOpen;
        // High Contrast hover is the system highlight, which would hide window-text transcript.
        var highContrast = _accessibility.HighContrast;
        if (highContrast) { row.Root.ClearValue(Border.BackgroundProperty); }
        else { row.Root.Background = active ? HoverFillAnchor.Background : RestFill(); }
        row.Actions.Opacity = active || row.Focused || highContrast || row.NeedsCopy ? 1 : ActionsRestOpacity;
    }

    // The brush fade interpolates every channel, so resting on Transparent
    // (white at zero alpha) would pass through translucent white on the way in and out.
    private SolidColorBrush RestFill()
    {
        var hover = ((SolidColorBrush)HoverFillAnchor.Background).Color;
        if (_restFill?.Color is not { A: 0 } rest || rest.R != hover.R || rest.G != hover.G || rest.B != hover.B)
            _restFill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, hover.R, hover.G, hover.B));
        return _restFill;
    }

    private static void ApplyRowLayout(HistoryRowView row, bool narrow)
    {
        Grid.SetColumnSpan(row.Time, narrow ? 2 : 1);
        Grid.SetRow(row.Body, narrow ? 1 : 0);
        Grid.SetRowSpan(row.Body, narrow ? 1 : 2);
        Grid.SetColumn(row.Body, narrow ? 0 : 1);
        Grid.SetColumnSpan(row.Body, narrow ? 2 : 1);
        row.Time.Margin = narrow ? new Thickness(0, 0, 0, 4) : new Thickness(0, 2, 0, 0);
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
        row.NeedsCopy = tag?.NeedsCopy == true;
        SetStyle(row.CopyIcon, row.NeedsCopy ? "HistoryNeedsCopyIconStyle" : "HistoryCopyIconStyle");
        UpdateRowState(row);
        if (tag is null)
        {
            row.TagHost.Visibility = Visibility.Collapsed;
            return;
        }
        var (text, icon) = tag.Tone switch
        {
            HomeHistoryTagTone.Inserted => ("HistoryTagTextStyle", "HistoryInsertedTagIconStyle"),
            HomeHistoryTagTone.Unverified => ("HistoryTagTextStyle", "HistoryTagIconStyle"),
            HomeHistoryTagTone.NotInserted => ("HistoryNotInsertedTagTextStyle", "HistoryNotInsertedTagIconStyle"),
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };
        SetStyle(row.TagText, text);
        SetStyle(row.TagIcon, icon);
        row.TagIcon.Glyph = tag.Glyph;
        row.TagText.Text = tag.Label;
        ToolTipService.SetToolTip(row.TagHost, tag.Meaning);
        // Border has no automation peer, so the readable tag text carries the name and meaning.
        AutomationProperties.SetName(row.TagText, $"Insertion: {tag.Label}");
        AutomationProperties.SetHelpText(row.TagText, tag.Meaning);
        row.TagHost.Visibility = Visibility.Visible;
    }

    private void Remove(HistoryRowView row)
    {
        var index = HistoryRows.Children.IndexOf(row.Root);
        RemoveRequested?.Invoke(row.Item);
        if (HistoryRows.Children.Contains(row.Root)) { return; }
        var next = HistoryRows.Children.Skip(Math.Max(0, index)).Concat(HistoryRows.Children.Take(Math.Max(0, index)).Reverse())
            .Select(child => _rows.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Root, child)))
            .FirstOrDefault(candidate => candidate is not null);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (next is not null) { next.More.Focus(FocusState.Keyboard); }
            else if (HistorySearchBox.Visibility == Visibility.Visible) { HistorySearchBox.Focus(FocusState.Keyboard); }
            else { EmptyHistoryFocusRequested?.Invoke(); }
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
        if (HistorySearchBox.Visibility != Visibility.Visible) { return; }
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
            HistoryHeaderGrid.RowSpacing = _model?.HasHistory == true ? 8 : 0;
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
        public Border TagHost = null!;
        public FontIcon TagIcon = null!;
        public TextBlock TagText = null!;
        public StackPanel Actions = null!;
        public Button Copy = null!;
        public FontIcon CopyIcon = null!;
        public Button More = null!;
        public bool Hovered;
        public bool Focused;
        public bool MenuOpen;
        public bool NeedsCopy;
        private bool _rendered;

        public void Update(HomeHistoryEntry entry, HomeHistoryView view)
        {
            var deliveryChanged = !_rendered || Item.Delivery != entry.Item.Delivery;
            Item = entry.Item;
            if (Time.Text != entry.TimeText)
            {
                Time.Text = entry.TimeText;
                ToolTipService.SetToolTip(Copy, ReferenceEquals(view._copiedRow, this) ? "Copied" : "Copy transcript");
                AutomationProperties.SetName(Copy, $"Copy transcript from {entry.TimeText}");
                ToolTipService.SetToolTip(More, "More actions");
                AutomationProperties.SetName(More, $"More actions for transcript from {entry.TimeText}");
            }
            if (Meta.Text != entry.MetadataText) { Meta.Text = entry.MetadataText; }
            var modelTip = entry.Model?.FullName;
            if (!Equals(ToolTipService.GetToolTip(Meta), modelTip)) { ToolTipService.SetToolTip(Meta, modelTip); }
            if (deliveryChanged) { view.UpdateTag(this, entry.Tag); }
            _rendered = true;
        }
    }
}
