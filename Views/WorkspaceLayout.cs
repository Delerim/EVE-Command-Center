using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
namespace EveCommandCenter.Views;

// View preferences are independent of production plans and never rewrite planning data.
internal static class WorkspaceLayout
{
    private sealed class State
    {
        public double Navigation { get; set; } = 390;
        public double StockRatio { get; set; } = 2;
        public Dictionary<string, double> Heights { get; set; } = new();
        public Dictionary<string, double[]> Columns { get; set; } = new();
    }
    internal static string PreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "industry-layout.json");
    public static void Attach(IndustryWorkspaceView view)
    {
        string FileName = PreferencePath;
        State state;
        try { state = JsonSerializer.Deserialize<State>(File.ReadAllText(FileName)) ?? new(); } catch { state = new(); }
        state.Columns ??= new(); state.Heights ??= new();
        var navigation = (ColumnDefinition)view.FindName("NavigationWidth");
        var stock = (RowDefinition)view.FindName("StockHeight");
        navigation.Width = new GridLength(double.IsFinite(state.Navigation) ? Math.Clamp(state.Navigation, 300, 700) : 390);
        view.SizeChanged += (_, _) =>
        {
            if (view.ActualWidth > 0) navigation.Width = new GridLength(Math.Clamp(navigation.Width.Value, 300, Math.Max(300, view.ActualWidth - 560)));
        };
        stock.Height = new GridLength(double.IsFinite(state.StockRatio) ? Math.Clamp(state.StockRatio, .3, 5) : 2, GridUnitType.Star);
        var frames = new[] { "ComponentBlueprintsFrame", "ExistingJobsFrame", "RolesFrame" }.Select(n => (Grid)view.FindName(n)).ToArray();
        foreach (var frame in frames) if (state.Heights.TryGetValue(frame.Name, out var height) && double.IsFinite(height)) frame.Height = Math.Clamp(height, 100, 900);
        var grids = new[] { "ProjectSkills", "ComponentInputs", "Stock", "Reservations", "Shopping", "Blueprints", "ComponentBlueprints", "ExistingJobs" }
            .Select(n => (DataGrid)view.FindName(n)).ToArray();
        foreach (var grid in grids)
        {
            grid.CanUserResizeColumns = true;
            grid.CanUserReorderColumns = true;
            foreach (var column in grid.Columns)
                if (state.Columns.TryGetValue(grid.Name + "/" + column.Header, out var saved) && saved is { Length: 3 } && double.IsFinite(saved[0]) && saved[0] > 0)
                    column.Width = new DataGridLength(saved[2] == (int)DataGridLengthUnitType.Star ? Math.Clamp(saved[0], .1, 10) : Math.Clamp(saved[0], 40, 1600), saved[2] == (int)DataGridLengthUnitType.Star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
            foreach (var column in grid.Columns)
                if (state.Columns.TryGetValue(grid.Name + "/" + column.Header, out var saved) && saved is { Length: 3 } && saved[1] >= 0 && saved[1] < grid.Columns.Count)
                    column.DisplayIndex = (int)saved[1];
        }
        void Save()
        {
            state.Navigation = navigation.Width.Value;
            foreach (var frame in frames) state.Heights[frame.Name] = frame.Height;
            var parent = (Grid)view.FindName("StockLayout");
            double bottom = parent.RowDefinitions[2].ActualHeight;
            if (bottom > 0) state.StockRatio = stock.ActualHeight / bottom;
            foreach (var grid in grids)
                foreach (var column in grid.Columns)
                    if (column.Width.Value > 0) state.Columns[grid.Name + "/" + column.Header] = new[] { column.Width.Value, (double)column.DisplayIndex, (double)column.Width.UnitType };
            try { Directory.CreateDirectory(Path.GetDirectoryName(FileName)!); File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(state)); File.Move(FileName + ".tmp", FileName, true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        view.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Save()));
        foreach (var grid in grids) grid.ColumnReordered += (_, _) => Save();
        view.Unloaded += (_, _) => Save();
    }
}
