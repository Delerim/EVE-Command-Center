using System.Windows;
using System.Windows.Threading;

namespace EveCommandCenter.Views;

public partial class OperatingToast : Window
{
    private static readonly List<OperatingToast> Active = new();
    private static readonly Queue<(string Structure, string Message, Action Open)> Pending = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly Action _open;

    public OperatingToast(string structure, string message, Action open)
    {
        InitializeComponent();
        StructureText.Text = structure;
        StructureText.ToolTip = structure;
        MessageText.Text = message;
        _open = open;
        _timer.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); Active.Remove(this); Reflow(); if (Pending.TryDequeue(out var next)) Notify(next.Structure, next.Message, next.Open); };
    }

    public static void Notify(string structure, string message, Action open)
    {
        if (Active.Count >= 3) { Pending.Enqueue((structure, message, open)); return; }
        var toast = new OperatingToast(structure, message, open);
        Active.Add(toast);
        Reflow();
        toast.Show();
        toast._timer.Start();
    }

    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        for (int i = 0; i < Active.Count; i++)
        {
            Active[i].Left = Math.Max(area.Left, area.Right - Active[i].Width - 16);
            Active[i].Top = Math.Max(area.Top, area.Bottom - (i + 1) * (Active[i].Height + 10) - 6);
        }
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) { e.Handled = true; Close(); }
    private void Open_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) { Close(); _open(); }
}
