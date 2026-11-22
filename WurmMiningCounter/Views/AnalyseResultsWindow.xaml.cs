using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using WurmMiningCounter.Models;
using WurmMiningCounter.Services;

namespace WurmMiningCounter.Views;

public partial class AnalyseResultsWindow : Window
{
    private readonly AnalyseProcessor _processor;
    private PlayerProfileId? _profileId;
    private string? _profileName;
    private bool _closed;
    private bool _updatingProfiles;

    public AnalyseResultsWindow(AnalyseProcessor processor, PlayerProfileId? profileId, string? profileName)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _profileId = profileId;
        _profileName = profileName;
        InitializeComponent();
        _processor.AnalyseChanged += OnAnalyseChanged;
        Closed += OnClosed;
        RefreshResult();
    }

    public void SelectProfile(PlayerProfileId profileId, string profileName)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        _profileId = profileId;
        _profileName = profileName;
        RefreshResult();
    }

    private void OnAnalyseChanged(object? sender, AnalyseChangedEventArgs e)
    {
        // Read the current snapshot on the UI thread, so queued notifications
        // cannot restore an older result after a reset or profile switch.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_closed)
                RefreshResult();
        });
    }

    private void RefreshResult()
    {
        var snapshots = _processor.GetSnapshots();
        if (_profileId is null && snapshots.OrderBy(item => item.ProfileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault() is { } first)
        {
            _profileId = first.ProfileId;
            _profileName = first.ProfileName;
        }
        var snapshot = snapshots.FirstOrDefault(item => item.ProfileId == _profileId);
        _updatingProfiles = true;
        try
        {
            var profiles = snapshots.Select(item => new ProfileOption(item.ProfileId, item.ProfileName)).ToList();
            if (snapshot is null && _profileId is { } id) profiles.Add(new ProfileOption(id, _profileName ?? string.Empty));
            ProfileSelector.ItemsSource = profiles.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            ProfileSelector.SelectedItem = profiles.FirstOrDefault(item => item.Id == _profileId);
        }
        finally { _updatingProfiles = false; }
        ProfileText.Text = snapshot?.ProfileName ?? _profileName ?? string.Empty;
        InputText.Text = snapshot is null ? string.Empty : snapshot.Result.Input.ToString();
        FinishTimeText.Text = $"Ostatnia analiza: {snapshot?.Result.FinishTime ?? "czas niedostępny"}";
        EmptyText.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        EntriesList.ItemsSource = snapshot?.Result.Entries.Select(FormatEntry).ToArray();
        var traces = snapshot?.Result.Entries.Select(entry => entry.Trace).OfType<AnalyseTrace>().ToArray() ?? [];
        var observations = traces.Select((trace, index) => new VeinDisplay(trace, ObservationColor(index))).ToArray();
        VeinsList.ItemsSource = observations;
        MapNoteText.Text = observations.Any(item => item.Trace.Direction is null)
            ? "Pozycje złóż bez kierunku są nieznane. Pozostałe pozycje są orientacyjne."
            : "Pozycje orientacyjne — mapa nie wskazuje dokładnego pola złoża.";
        DrawMap(observations);
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingProfiles && ProfileSelector.SelectedItem is ProfileOption profile)
            SelectProfile(profile.Id, profile.Name);
    }

    private static int Distance(AnalyseStrength strength) => (int)strength + 1;

    private void DrawMap(VeinDisplay[] observations)
    {
        const int radius = 7;
        const int size = 15;
        string[] colors = ["#FFFFFFFF", "#FFFBDCDD", "#FFFFE4CF", "#FFFFF3C4", "#FFE5E4FA", "#FFDCEBF9", "#FFDCEFE5", "#FFF1F3F6"];
        var brushes = colors.Select(color => (Brush)new BrushConverter().ConvertFromString(color)!).ToArray();
        MapGrid.Children.Clear();
        MapGrid.RowDefinitions.Clear();
        MapGrid.ColumnDefinitions.Clear();
        for (var i = 0; i < size; i++)
        {
            MapGrid.RowDefinitions.Add(new RowDefinition());
            MapGrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (var row = 0; row < size; row++)
        for (var col = 0; col < size; col++)
        {
            var ring = Math.Max(Math.Abs(row - radius), Math.Abs(col - radius));
            var cell = new Border { Background = brushes[ring], BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(0.4) };
            if (ring == 0)
                cell.Child = new TextBlock { Text = "●", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DarkBlue };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            MapGrid.Children.Add(cell);
        }
        foreach (var group in observations.Where(item => item.Trace.Direction is not null)
                     .Select(item => (Item: item, Position: MapPosition(item.Trace.Direction!, Distance(item.Trace.Strength))))
                     .Where(item => item.Position is not null).GroupBy(item => item.Position!.Value))
        {
            var marks = new UniformGrid { Columns = (int)Math.Ceiling(Math.Sqrt(group.Count())) };
            foreach (var item in group)
                marks.Children.Add(new Border { Background = item.Item.Color, Margin = new Thickness(0.5),
                    ToolTip = $"{item.Item.Material}: {item.Item.Trace.Direction} ({Distance(item.Item.Trace.Strength)} tiles) - schematic" });
            var marker = new Border
            {
                Margin = new Thickness(3), Child = marks,
                ToolTip = string.Join("\n", group.Select(item => $"{item.Item.Material}: {item.Item.Trace.Direction} ({Distance(item.Item.Trace.Strength)} tiles) - schematic"))
            };
            Grid.SetRow(marker, radius + group.Key.Y);
            Grid.SetColumn(marker, radius + group.Key.X);
            MapGrid.Children.Add(marker);
        }
    }

    private static Brush ObservationColor(int index)
    {
        double hue = (index * 137.508 + 120) % 360;
        const double saturation = 0.8, value = 0.72;
        double chroma = value * saturation, section = hue / 60;
        double x = chroma * (1 - Math.Abs(section % 2 - 1)), m = value - chroma;
        var (r, g, b) = (int)section switch
        {
            0 => (chroma, x, 0d), 1 => (x, chroma, 0d), 2 => (0d, chroma, x),
            3 => (0d, x, chroma), 4 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255)));
        brush.Freeze();
        return brush;
    }

    private sealed record VeinDisplay(AnalyseTrace Trace, Brush Color)
    {
        public string Material => Trace.Material ?? "Unidentified material";
        public string Tiles => $"{Distance(Trace.Strength)} tiles";
        public string Details => string.Join("\n", new[]
        {
            Trace.Quality is null ? null : $"Quality: {Trace.Quality}",
            Trace.Direction is null ? null : $"Direction: {Trace.Direction}"
        }.OfType<string>());
    }

    private static (int X, int Y)? MapPosition(string direction, int radius)
    {
        var angle = direction switch
        {
            "north" => 0, "east of north" => 1, "northeast" => 2, "north of east" => 3,
            "east" => 4, "south of east" => 5, "southeast" => 6, "east of south" => 7,
            "south" => 8, "west of south" => 9, "southwest" => 10, "south of west" => 11,
            "west" => 12, "north of west" => 13, "northwest" => 14, "west of north" => 15, _ => -1
        };
        if (angle < 0) return null;
        var radians = angle * Math.PI / 8;
        var x = Math.Sin(radians);
        var y = -Math.Cos(radians);
        var scale = radius / Math.Max(Math.Abs(x), Math.Abs(y));
        return ((int)Math.Round(x * scale), (int)Math.Round(y * scale));
    }

    private sealed record ProfileOption(PlayerProfileId Id, string Name);

    private sealed record EntryDisplay(string Prefix, string Material, string Suffix)
    {
        public override string ToString() => Prefix + Material + Suffix;
    }

    private static EntryDisplay FormatEntry(AnalyseEntry entry)
    {
        if (entry.Trace is { } trace)
        {
            var suffix = trace.Quality is null ? "" : $"\nQuality: {trace.Quality}";
            if (trace.Direction is not null) suffix += $"\nDirection: {trace.Direction}";
            return new EntryDisplay($"{StrengthText(trace.Strength)} - ", trace.Material ?? "Unidentified material", suffix);
        }
        return new EntryDisplay(EventLogLineNormalizer.Normalize(entry.RawText), "", "");
    }

    private static string StrengthText(AnalyseStrength strength) => strength switch
    {
        AnalyseStrength.Trace => "Trace",
        AnalyseStrength.Slight => "Slight trace",
        AnalyseStrength.Faint => "Faint trace",
        AnalyseStrength.Minuscule => "Minuscule trace",
        AnalyseStrength.Vague => "Vague trace",
        AnalyseStrength.Indistinct => "Indistinct trace",
        _ => throw new ArgumentOutOfRangeException(nameof(strength))
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _processor.AnalyseChanged -= OnAnalyseChanged;
        Closed -= OnClosed;
    }
}
