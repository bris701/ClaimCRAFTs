using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ClaimCRAFTs.Models;
using ClaimCRAFTs.Services;

namespace ClaimCRAFTs;

public partial class MainWindow : Window
{
    private readonly ApiClient _api = new();
    private readonly UserSettings _settings = SettingsStore.Load();
    private readonly System.Windows.Threading.DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
    // Live percentage changes are combined into one UI update per short batch.
    private readonly System.Windows.Threading.DispatcherTimer _progressBatchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    // Persist appearance only after dragging pauses, not on every slider value change.
    private readonly System.Windows.Threading.DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly SemaphoreSlim _httpThrottle = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _handledExpirations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Run> _craftLabelRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveMessage> _pendingProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _surfaceBrushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SolidColorBrush> _badgeBrushes = new(StringComparer.OrdinalIgnoreCase);
    // Allocate action prefixes once rather than on each row render or live progress update.
    private static readonly string[] CraftActionPrefixes =
    [
        "Split into ", "Saw into ", "Refine into ", "Process into ", "Turn into ",
        "Forge ", "Extract ", "Split ", "Saw ", "Stitch ", "Mix ",
        "Identify ", "Make ", "Craft ", "Create ", "Produce ",
        "Process ", "Refine ", "Smelt ", "Weave ", "Tan ",
        "Cook ", "Bake ", "Brew ", "Sew ", "Cut ", "Chop ",
        "Carve ", "Mine ", "Assemble ", "Shape ", "Plant ",
        "Grow ", "Harvest ", "Prepare ", "Construct ", "Build ",
        "Polish ", "Spin ", "Crush ", "Grind ", "Render ",
        "Melt ", "Dye ", "Press ", "Collect ", "Gather ",
        "Clean ", "Smash ", "Braid ", "Upgrade "
    ];
    private LiveFeed? _live;
    private CancellationTokenSource? _selectionToken;
    private CraftSnapshot? _snapshot;
    private string? _selectedClaimId;
    private string? _selectedClaimName;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSuccessfulRefresh = DateTimeOffset.MinValue;
    private bool _liveConnected;
    private bool _snapshotOnline;
    private bool _refreshPending;
    private bool _refreshDetailsPending;
    private bool _refreshRunning;
    private bool _appearanceReady;
    private bool _closing;
    private int _selectionVersion;
    private int _requestsInFlight;

    public MainWindow()
    {
        InitializeComponent();
        WindowTitle.Text = $"ClaimCRAFTs v{typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.3.1"}";
        Topmost = _settings.AlwaysOnTop;
        UpdatePinButton();

        // Existing settings may contain 0% from older builds; normalize those to 1%.
        BackgroundOpacitySlider.Value = Math.Clamp(_settings.BackgroundOpacity, 1, 100);
        TextOpacitySlider.Value = Math.Clamp(_settings.TextOpacity, 1, 100);
        TextShadeSlider.Value = Math.Clamp(_settings.TextShade, 0, 100);
        LiveToggle.IsChecked = _settings.LiveEnabled;
        _appearanceReady = true;
        ApplyAppearance();

        _heartbeat.Tick += (_, _) => HeartbeatTick();
        _progressBatchTimer.Tick += (_, _) => ApplyPendingProgress();
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettings();
        };
        if (_settings.LiveEnabled) _heartbeat.Start();
        Closing += (_, _) => Shutdown();
        if (!string.IsNullOrWhiteSpace(_settings.ClaimId))
        {
            try { SelectClaim(new ClaimOption(_settings.ClaimId!, _settings.ClaimName ?? _settings.ClaimId!, 0, null)); }
            catch (FormatException) { _settings.ClaimId = null; OpenSettings(); }
        }
        else OpenSettings();
    }

    private void Shutdown()
    {
        _closing = true;
        _heartbeat.Stop();
        _progressBatchTimer.Stop();
        _settingsSaveTimer.Stop();
        // Flush any appearance changes not yet written by the debounce timer.
        SaveSettings();
        _selectionToken?.Cancel();
        _live?.Dispose();
        _api.Dispose();
        _selectionToken?.Dispose();
        // The pending request loop may still be unwinding; let the process reclaim the semaphore.
    }

    private void SelectClaim(ClaimOption option)
    {
        var id = JsonData.ValidateId(option.Id);
        _selectionVersion++;
        _selectionToken?.Cancel();
        StopLiveFeed();
        _selectionToken = new CancellationTokenSource();
        _selectedClaimId = id;
        _selectedClaimName = option.Name;
        _snapshot = null;
        _refreshDetailsPending = false;
        _progressBatchTimer.Stop();
        _pendingProgress.Clear();
        _craftLabelRuns.Clear();
        _handledExpirations.Clear();
        _snapshotOnline = false;
        _liveConnected = false;
        ClaimNameLabel.Text = option.Name;
        UpdatedLabel.Text = "Last Update: --:--";
        OverviewScroll.ScrollToTop();
        SearchResults.Children.Clear();
        SearchStatus.Text = $"Selected {option.Name}.";
        ErrorBanner.Visibility = Visibility.Collapsed;
        RenderOverview();
        UpdateConnectionLabel();

        _settings.ClaimId = id;
        _settings.ClaimName = option.Name;
        SaveSettings();

        if (_settings.LiveEnabled) StartLiveFeed(id);
        // Selecting a claim always loads its initial data, including in manual mode.
        RequestRefresh(includeClaimDetails: true);
    }

    private void StartLiveFeed(string claimId)
    {
        if (_closing || !_settings.LiveEnabled || _live is not null) return;
        var feed = new LiveFeed();
        _live = feed;
        feed.ConnectionChanged += connected => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || !_settings.LiveEnabled || !ReferenceEquals(feed, _live)) return;
            _liveConnected = connected;
            UpdateConnectionLabel();
            if (connected) RequestRefresh(includeClaimDetails: true); // Reconcile after reconnect.
        }));
        feed.MessageReceived += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing && _settings.LiveEnabled && ReferenceEquals(feed, _live))
                HandleLiveMessage(message);
        }));
        feed.Start(claimId);
    }

    private void StopLiveFeed()
    {
        var oldFeed = _live;
        _live = null; // Ignore late events from the previous socket.
        _liveConnected = false;
        oldFeed?.Dispose();
        _progressBatchTimer.Stop();
        _pendingProgress.Clear();
    }

    private void LiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_appearanceReady || _closing) return;
        var enabled = LiveToggle.IsChecked == true;
        if (_settings.LiveEnabled == enabled) return;
        _settings.LiveEnabled = enabled;
        if (enabled) _heartbeat.Start();
        else _heartbeat.Stop();
        if (enabled && _selectedClaimId is not null)
        {
            StartLiveFeed(_selectedClaimId);
            RequestRefresh(includeClaimDetails: true); // Reconcile edits made while paused.
        }
        else if (!enabled)
        {
            StopLiveFeed();
            // Drop automatic invalidations already queued before the toggle.
            // An HTTP request in flight may still finish; manual Refresh always works.
            _refreshPending = false;
            _refreshDetailsPending = false;
        }
        UpdateConnectionLabel();
        SaveSettings();
    }

    private void HandleLiveMessage(LiveMessage message)
    {
        if (_selectedClaimId is null) return;
        if (message.Type == "ClaimCraftingChanged")
        {
            // Null claim IDs represent global invalidations, including membership changes.
            if (message.ClaimId is null)
                RequestRefresh(includeClaimDetails: true);
            else if (message.ClaimId == _selectedClaimId)
                RequestRefresh();
        }
        else if (message.Type == "CraftProgressChanged" && message.ClaimId == _selectedClaimId)
        {
            if (message.CraftId is null || message.Progress is null)
            {
                RequestRefresh();
                return;
            }
            // Many crafts can progress almost simultaneously. Coalesce by craft ID.
            _pendingProgress[message.CraftId] = message;
            if (!_progressBatchTimer.IsEnabled) _progressBatchTimer.Start();
        }
    }

    private void ApplyPendingProgress()
    {
        _progressBatchTimer.Stop();
        if (_pendingProgress.Count == 0) return;
        if (_snapshot is null)
        {
            _pendingProgress.Clear();
            RequestRefresh();
            return;
        }

        var projects = _snapshot.Projects.ToList();
        var changed = false;
        var needsRebuild = false;
        foreach (var message in _pendingProgress.Values)
        {
            var index = projects.FindIndex(craft => craft.Id == message.CraftId);
            if (index < 0 || message.Progress is null)
            {
                RequestRefresh(); // Project was added while a snapshot was loading.
                continue;
            }

            var current = projects[index];
            var progress = current with
            {
                Progress = message.Progress,
                LockExpiration = message.LockExpiration,
                Status = current.ProgressTotal is > 0 && message.Progress >= current.ProgressTotal
                    ? "Complete"
                    : message.LockExpiration is DateTimeOffset expiration && expiration <= DateTimeOffset.UtcNow
                        ? "Suspended" : "Active"
            };
            if (progress == current) continue;
            changed = true;
            if (progress.IsFinished)
            {
                _handledExpirations.Remove(progress.Id);
                projects.RemoveAt(index);
                needsRebuild = true; // Totals, grouping and badges also change.
            }
            else
            {
                projects[index] = progress;
                if (_craftLabelRuns.TryGetValue(progress.Id, out var textRun))
                    textRun.Text = CraftLabel(progress); // Update only the affected percentage.
                else
                    needsRebuild = true;
            }
        }
        _pendingProgress.Clear();
        if (!changed) return;
        _snapshot = _snapshot with { Projects = projects };
        if (needsRebuild) RenderOverview();
        MarkLastUpdate();
        if (Volatile.Read(ref _requestsInFlight) > 0) RequestRefresh();
    }

    private void RequestRefresh(bool includeClaimDetails = false)
    {
        if (_closing || _selectedClaimId is null) return;
        _refreshDetailsPending |= includeClaimDetails;
        _refreshPending = true;
        if (!_refreshRunning) _ = RefreshQueueAsync();
    }

    private async Task RefreshQueueAsync()
    {
        if (_refreshRunning) return;
        _refreshRunning = true;
        try
        {
            while (!_closing && _refreshPending)
            {
                _refreshPending = false;
                var includeClaimDetails = _refreshDetailsPending || _snapshot is null;
                _refreshDetailsPending = false;
                var id = _selectedClaimId;
                var version = _selectionVersion;
                var token = _selectionToken?.Token ?? CancellationToken.None;
                if (id is null) break;
                try
                {
                    await _httpThrottle.WaitAsync(token);
                    try
                    {
                        var delay = _lastRequestAt.AddSeconds(5) - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                        _lastRequestAt = DateTimeOffset.UtcNow;
                    }
                    finally { _httpThrottle.Release(); }

                    if (version != _selectionVersion) continue;
                    Interlocked.Increment(ref _requestsInFlight);
                    try
                    {
                        var cachedClaim = includeClaimDetails ? null : _snapshot?.Claim;
                        var snapshot = await _api.FetchSnapshotAsync(id, cachedClaim, token);
                        if (_closing || version != _selectionVersion) continue;
                        var displayChanged = _snapshot is null || !SameDisplay(_snapshot, snapshot);
                        _snapshot = snapshot;
                        // Retain only expiry entries belonging to crafts still displayed.
                        var currentIds = snapshot.Projects.Select(craft => craft.Id).ToHashSet(StringComparer.Ordinal);
                        foreach (var expiredId in _handledExpirations.Keys.ToArray())
                            if (!currentIds.Contains(expiredId)) _handledExpirations.Remove(expiredId);
                        _snapshotOnline = true;
                        _selectedClaimName = snapshot.Claim.Name;
                        ClaimNameLabel.Text = snapshot.Claim.Name;
                        if (_settings.ClaimName != snapshot.Claim.Name)
                        {
                            _settings.ClaimName = snapshot.Claim.Name;
                            SaveSettings();
                        }
                        MarkLastUpdate();
                        ErrorBanner.Visibility = Visibility.Collapsed;
                        if (displayChanged) RenderOverview();
                        UpdateConnectionLabel();
                    }
                    finally { Interlocked.Decrement(ref _requestsInFlight); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { /* claim changed */ }
                catch (Exception ex)
                {
                    if (version != _selectionVersion || _closing) continue;
                    _snapshotOnline = false;
                    ShowError($"Unable to refresh claim data: {ex.Message}");
                    UpdateConnectionLabel();
                }
            }
        }
        finally
        {
            _refreshRunning = false;
            if (!_closing && _refreshPending) RequestRefresh();
        }
    }

    private void HeartbeatTick()
    {
        // Manual mode intentionally has no WebSocket or periodic REST requests.
        // The user can still press Refresh at any time.
        if (!_settings.LiveEnabled) return;
        // When WebSockets are unavailable, retry the snapshot at a modest cadence.
        if (_selectedClaimId is not null &&
            ((!_liveConnected && DateTimeOffset.UtcNow - _lastRequestAt > TimeSpan.FromSeconds(60)) ||
             (!_snapshotOnline && DateTimeOffset.UtcNow - _lastRequestAt > TimeSpan.FromSeconds(30))))
            RequestRefresh(includeClaimDetails: true);
        // Lock expiry changes the displayed activity even without a socket event.
        if (_snapshot is not null)
        {
            foreach (var craft in _snapshot.Projects)
            {
                if (craft.LockExpiration is not DateTimeOffset expiration || expiration > DateTimeOffset.UtcNow ||
                    !craft.Status.Equals("Active", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_handledExpirations.TryGetValue(craft.Id, out var handled) || handled != expiration)
                {
                    _handledExpirations[craft.Id] = expiration;
                    RequestRefresh();
                }
            }
        }
    }

    private void UpdateConnectionLabel()
    {
        if (_selectedClaimId is null) SetConnection("Select claim", "#F4B75E");
        else if (!_settings.LiveEnabled)
            SetConnection(_snapshotOnline ? "Connected · manual" : "Manual mode",
                _snapshotOnline ? "#56D49B" : "#9BA8B8");
        else if (_liveConnected && _snapshotOnline) SetConnection("Connected", "#56D49B");
        else if (_liveConnected) SetConnection("Live connected", "#F4B75E");
        else if (_snapshot is not null) SetConnection("REST · reconnecting", "#F4B75E");
        else SetConnection("Connecting…", "#F4B75E");
    }

    private void SetConnection(string label, string color)
    {
        ConnectionLabel.Text = label;
        ConnectionDot.Fill = Brush(color);
        LiveLabel.Text = _settings.LiveEnabled ? "Live: ON" : "Live: OFF";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void MarkLastUpdate()
    {
        _lastSuccessfulRefresh = DateTimeOffset.Now;
        UpdatedLabel.Text = $"Last Update: {_lastSuccessfulRefresh:HH:mm}";
    }

    // Skips expensive WPF tree rebuilding when a REST snapshot has the same visible content.
    private static bool SameDisplay(CraftSnapshot previous, CraftSnapshot current)
    {
        if (previous.Claim.Id != current.Claim.Id ||
            previous.Claim.Name != current.Claim.Name ||
            previous.Claim.OwnerId != current.Claim.OwnerId ||
            previous.Claim.Members.Count != current.Claim.Members.Count ||
            previous.Projects.Count != current.Projects.Count) return false;
        foreach (var member in previous.Claim.Members)
            if (!current.Claim.Members.TryGetValue(member.Key, out var candidate) || candidate != member.Value)
                return false;

        var oldById = new Dictionary<string, CraftProject>(previous.Projects.Count, StringComparer.Ordinal);
        foreach (var project in previous.Projects)
            oldById[project.Id] = project;
        foreach (var craft in current.Projects)
        {
            if (!oldById.TryGetValue(craft.Id, out var old) ||
                old.OwnerId != craft.OwnerId || old.OwnerName != craft.OwnerName ||
                old.BuildingName != craft.BuildingName || old.RecipeName != craft.RecipeName ||
                old.OutputName != craft.OutputName ||
                old.Quantity != craft.Quantity || old.Progress != craft.Progress ||
                old.ProgressTotal != craft.ProgressTotal || old.Status != craft.Status ||
                old.LockExpiration != craft.LockExpiration ||
                !old.FunctionIds.SequenceEqual(craft.FunctionIds)) return false;
        }
        return true;
    }

    private OwnerKind OwnerClass(string id)
    {
        if (_snapshot is null) return OwnerKind.NonMember;
        if (id == _snapshot.Claim.OwnerId) return OwnerKind.Member;
        return _snapshot.Claim.Members.TryGetValue(id, out var member)
            ? member.CanUseStorage ? OwnerKind.Member : OwnerKind.NoPermissions
            : OwnerKind.NonMember;
    }

    private string OwnerName(CraftProject craft)
    {
        if (_snapshot is not null && _snapshot.Claim.Members.TryGetValue(craft.OwnerId, out var member))
            return string.IsNullOrWhiteSpace(craft.OwnerName) ? member.Name : craft.OwnerName;
        return string.IsNullOrWhiteSpace(craft.OwnerName) ? craft.OwnerId : craft.OwnerName;
    }

    private void RenderOverview()
    {
        var offset = OverviewScroll.VerticalOffset;
        OverviewContent.Children.Clear();
        _craftLabelRuns.Clear();
        if (_snapshot is null)
        {
            OverviewContent.Children.Add(InfoCard(_selectedClaimId is null
                ? "Select a claim in Settings to begin monitoring public crafts."
                : "Loading public crafting data…"));
            return;
        }
        var projects = _snapshot.Projects;
        var members = projects.Count(craft => OwnerClass(craft.OwnerId) == OwnerKind.Member);
        var bees = projects.Count(craft => OwnerClass(craft.OwnerId) == OwnerKind.NoPermissions);
        var others = projects.Count - members - bees;

        var header = new StackPanel();
        header.Children.Add(DisplayText(new TextBlock
        {
            Text = $"{projects.Count} active public craft{(projects.Count == 1 ? "" : "s")}",
            FontSize = 13.5, FontWeight = FontWeights.Bold
        }));
        var counts = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        counts.Children.Add(CountBadge($"Members {members}", OwnerKind.Member));
        counts.Children.Add(CountBadge($"No-Perms {bees}", OwnerKind.NoPermissions));
        counts.Children.Add(CountBadge($"Non-members {others}", OwnerKind.NonMember));
        header.Children.Add(counts);
        OverviewContent.Children.Add(Card(header, "#202A37", new Thickness(0, 0, 0, 2), new Thickness(7, 4, 7, 2)));

        if (projects.Count == 0)
        {
            OverviewContent.Children.Add(InfoCard("No active public crafts found."));
            return;
        }
        string? section = null;
        foreach (var group in CraftCategories.Group(projects))
        {
            if (group.Key.Section != section)
            {
                section = group.Key.Section;
                OverviewContent.Children.Add(SectionHeading(section));
            }
            var stack = new StackPanel();
            stack.Children.Add(DisplayText(new TextBlock
            {
                Text = group.Key.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            }));
            foreach (var craft in group.OrderBy(c => OwnerName(c), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(c => c.RecipeName, StringComparer.OrdinalIgnoreCase))
                stack.Children.Add(CraftLine(craft));
            OverviewContent.Children.Add(Card(stack, "#171E28", new Thickness(0, 0, 0, 2), new Thickness(6, 3, 6, 3)));
        }
        // Let WPF arrange the updated tree during its normal layout pass.
        // Forcing UpdateLayout here made large claims and opacity changes stutter.
        Dispatcher.BeginInvoke(new Action(() => OverviewScroll.ScrollToVerticalOffset(offset)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Border InfoCard(string text) => Card(DisplayText(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5
    }), "#202A37", new Thickness(0, 0, 0, 2), new Thickness(8));

    private Border SectionHeading(string heading)
    {
        var grid = new Grid { Margin = new Thickness(1, 1, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(DisplayText(new TextBlock { Text = heading, FontSize = 11, FontWeight = FontWeights.Bold }));
        var rule = new Border { Height = 1, Background = Surface("#344052"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        return new Border { Child = grid };
    }

    private TextBlock CraftLine(CraftProject craft)
    {
        var row = DisplayText(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5, LineHeight = 14, Margin = new Thickness(0, 0, 0, 2)
        });
        var pill = new Border
        {
            Background = BadgeBrush(OwnerClass(craft.OwnerId)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(0, 0, 3, 0),
            Child = DisplayText(new TextBlock { Text = OwnerName(craft), FontSize = 10, FontWeight = FontWeights.SemiBold })
        };
        row.Inlines.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.Center });
        var textRun = new Run(CraftLabel(craft));
        row.Inlines.Add(textRun);
        _craftLabelRuns[craft.Id] = textRun;
        return row;
    }

    private static string CraftLabel(CraftProject craft)
    {
        var label = $" • {CraftDisplayName(craft)} ×{craft.Quantity.ToString("N0", CultureInfo.CurrentCulture)}";
        if (craft.ProgressPercent is int progress) label += $" ({progress}%)";
        return label;
    }

    // The API explicitly supplies crafted products under outputs[].name. Prefer the
    // actual result item over parsing action words from a descriptive recipe name.
    // Keep RecipeName unchanged for sorting and other authoritative data handling.
    private static string CraftDisplayName(CraftProject craft)
    {
        if (!string.IsNullOrWhiteSpace(craft.OutputName))
            return craft.OutputName.Trim();

        // Fallback for recipes without an available output item description.
        var recipeName = craft.RecipeName;
        const string into = " into ";
        var intoIndex = recipeName.LastIndexOf(into, StringComparison.OrdinalIgnoreCase);
        if (intoIndex >= 0)
        {
            var output = recipeName[(intoIndex + into.Length)..].Trim();
            if (output.Length > 0) return output;
        }

        // When a recipe names the result before its ingredients (e.g. "Braid
        // Sturdy Rope from Sturdy Cloth Strip"), discard the ingredient clause.
        foreach (var prefix in CraftActionPrefixes)
        {
            if (!recipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var result = recipeName[prefix.Length..].Trim();
            if (result.Length == 0) break;
            if (prefix.Equals("Upgrade ", StringComparison.OrdinalIgnoreCase))
            {
                const string to = " to ";
                var toIndex = result.LastIndexOf(to, StringComparison.OrdinalIgnoreCase);
                if (toIndex >= 0 && toIndex + to.Length < result.Length)
                    result = result[(toIndex + to.Length)..].Trim();
            }
            const string from = " from ";
            var fromIndex = result.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (fromIndex > 0) result = result[..fromIndex].TrimEnd();
            return result;
        }
        return recipeName;
    }

    private Border CountBadge(string text, OwnerKind kind) => new()
    {
        CornerRadius = new CornerRadius(4),
        Background = BadgeBrush(kind),
        Padding = new Thickness(5, 2, 5, 2),
        Margin = new Thickness(0, 0, 4, 3),
        Child = DisplayText(new TextBlock { Text = text, FontSize = 10 })
    };

    private Border Card(UIElement child, string bg, Thickness margin, Thickness padding) => new()
    {
        // Flat cards save 2px of border height each while preserving their rounded backgrounds.
        Background = Surface(bg), BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(6), Padding = padding, Margin = margin, Child = child
    };

    private SolidColorBrush BadgeBrush(OwnerKind kind)
    {
        var color = kind switch
        {
            OwnerKind.Member => "#1F7046",
            OwnerKind.NoPermissions => "#99641F",
            _ => "#872B2D"
        };
        return CachedBrush(_badgeBrushes, color, Math.Max(.30, _settings.BackgroundOpacity / 100));
    }

    private static TextBlock DisplayText(TextBlock block)
    {
        // Dynamically resolve the brush, rather than storing its current instance.
        // The original App.xaml brush can be frozen by WPF and replaced when a slider moves;
        // direct references then retain the old color/opacity until the overview rebuilds.
        block.SetResourceReference(TextBlock.ForegroundProperty, "DisplayTextBrush");
        return block;
    }

    private SolidColorBrush Surface(string hex) =>
        CachedBrush(_surfaceBrushes, hex, _settings.BackgroundOpacity / 100);

    private static SolidColorBrush CachedBrush(Dictionary<string, SolidColorBrush> cache, string hex, double alpha)
    {
        if (cache.TryGetValue(hex, out var cached)) return cached;
        var brush = new SolidColorBrush(WithOpacity(hex, alpha));
        cache.Add(hex, brush);
        return brush;
    }

    private static Color WithOpacity(string hex, double opacity)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        color.A = (byte)Math.Clamp(Math.Round(255 * opacity), 0, 255);
        return color;
    }

    private static void UpdateResourceBrush(string key, string hex, double opacity) =>
        UpdateResourceBrush(key, WithOpacity(hex, opacity));

    private static void UpdateResourceBrush(string key, Color color)
    {
        // WPF can freeze brushes declared in App.xaml. When that happens, replace the
        // resource; both XAML and generated overview text now use DynamicResource, so
        // no text element retains a stale brush after replacement.
        var resources = Application.Current.Resources;
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            resources[key] = new SolidColorBrush(color);
    }

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void ApplyAppearance()
    {
        if (!_appearanceReady) return;
        _settings.BackgroundOpacity = BackgroundOpacitySlider.Value;
        _settings.TextOpacity = TextOpacitySlider.Value;
        _settings.TextShade = TextShadeSlider.Value;
        BackgroundOpacityValue.Text = $"{_settings.BackgroundOpacity:0}%";
        TextOpacityValue.Text = $"{_settings.TextOpacity:0}%";
        TextShadeValue.Text = _settings.TextShade == 0 ? "White" : _settings.TextShade == 100 ? "Black" : $"{_settings.TextShade:0}% black";
        var alpha = _settings.BackgroundOpacity / 100;
        UpdateResourceBrush("WindowSurfaceBrush", "#11151C", alpha);
        UpdateResourceBrush("TitleSurfaceBrush", "#171D27", alpha);
        UpdateResourceBrush("SummarySurfaceBrush", "#202A37", alpha);
        UpdateResourceBrush("ErrorSurfaceBrush", "#3A2227", alpha);
        UpdateResourceBrush("MessageSurfaceBrush", "#1A202B", alpha);
        UpdateResourceBrush("SearchInputSurfaceBrush", "#0D1118", alpha);
        UpdateResourceBrush("ClaimSelectorSurfaceBrush", "#202A37", alpha);
        // These are created by our code rather than loaded from XAML, and
        // normally remain writable. Never attempt to mutate a frozen brush.
        foreach (var (hex, brush) in _surfaceBrushes)
            if (!brush.IsFrozen) brush.Color = WithOpacity(hex, alpha);
        foreach (var (hex, brush) in _badgeBrushes)
            if (!brush.IsFrozen) brush.Color = WithOpacity(hex, Math.Max(.30, alpha));

        var grey = (byte)Math.Clamp(Math.Round(255 * (1 - _settings.TextShade / 100)), 0, 255);
        UpdateResourceBrush("DisplayTextBrush", Color.FromArgb(
            (byte)Math.Round(255 * _settings.TextOpacity / 100), grey, grey, grey));
        // No RenderOverview() here: opacity and text shade only affect shared brushes.
    }

    private void SaveSettings()
    {
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { ShowError($"Couldn't save preferences: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        OverviewView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        ClaimSearchInput.Focus();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        OverviewView.Visibility = Visibility.Visible;
        _settingsSaveTimer.Stop();
        SaveSettings();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchClaimsAsync();
    private async void ClaimSearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchClaimsAsync();
    }

    private async Task SearchClaimsAsync()
    {
        var query = ClaimSearchInput.Text.Trim();
        if (query.Length < 2) { SearchStatus.Text = "Enter at least two characters."; return; }
        SearchButton.IsEnabled = false;
        SearchResults.Children.Clear();
        SearchStatus.Text = "Searching claims…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var results = await _api.SearchClaimsAsync(query, cts.Token);
            if (_closing) return;
            SearchStatus.Text = results.Count == 0 ? "No matching claims found." : $"{results.Count} result(s). Select a claim:";
            foreach (var result in results)
            {
                var button = new Button
                {
                    Content = new TextBlock
                    {
                        Text = $"{result.Name}  ·  Region {result.Region}  ·  #{result.Id}",
                        TextWrapping = TextWrapping.Wrap, FontSize = 10.5
                    },
                    Tag = result,
                    Style = (Style)FindResource("SecondaryButton"),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 7, 8, 7),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                button.Click += (_, _) =>
                {
                    SelectClaim((ClaimOption)button.Tag);
                    SettingsView.Visibility = Visibility.Collapsed;
                    OverviewView.Visibility = Visibility.Visible;
                };
                SearchResults.Children.Add(button);
            }
        }
        catch (Exception ex) { SearchStatus.Text = $"Claim search failed: {ex.Message}"; }
        finally { SearchButton.IsEnabled = true; }
    }

    private void AppearanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceReady) return;
        ApplyAppearance();
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }
    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RequestRefresh(includeClaimDetails: true);
    private void UpdatePinButton()
    {
        PinButton.Content = Topmost ? "📌" : "📍";
        PinButton.ToolTip = Topmost ? "Always on top: enabled" : "Always on top: disabled";
        PinButton.Foreground = Brush("#B8C2D0");
    }
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _settings.AlwaysOnTop = Topmost;
        UpdatePinButton();
        SaveSettings();
    }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1) DragMove();
    }

    private void ExternalLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // The Settings page only offers these two public, hard-coded URLs.
        if (e.Uri.AbsoluteUri is not ("https://craft.resubaka.dev/" or
                                     "https://github.com/ResuBaka/bitcraft-hub")) return;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Unable to open the link: {ex.Message}", "ClaimCRAFTs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb grip || grip.Tag is not string edge) return;
        var left = edge is "Left" or "TL" or "BL";
        var right = edge is "Right" or "TR" or "BR";
        var top = edge is "Top" or "TL" or "TR";
        var bottom = edge is "Bottom" or "BL" or "BR";
        if (left)
        {
            var change = Math.Min(e.HorizontalChange, Width - MinWidth);
            Width -= change;
            Left += change;
        }
        if (right) Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        if (top)
        {
            var change = Math.Min(e.VerticalChange, Height - MinHeight);
            Height -= change;
            Top += change;
        }
        if (bottom) Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }
}
