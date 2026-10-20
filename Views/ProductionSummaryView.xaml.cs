using System.Windows;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class ProductionSummaryView : System.Windows.Controls.UserControl
{
    public event EventHandler? OpenMaterials;
    public ProductionSummaryView()
    {
        InitializeComponent();
        WorkspaceLayout.AttachTable(DashboardMaterialGaps);
    }
    internal void ShowSummary(ProductionDashboard.Summary summary)
    {
        SummaryText.Text = $"{summary.ActiveProjects:N0} active projects | {summary.Materials.Count:N0} materials";
        MissingText.Text = $"{summary.Shortfalls:N0} shortfalls";
        CoveredText.Text = $"{summary.Covered:N0} covered by stock";
        ReviewText.Text = $"{summary.NeedsReview:N0} need checking";
        DetailText.Text = summary.Detail;
        DashboardMaterialGaps.ItemsSource = summary.Materials;
        DashboardMaterialGaps.Visibility = summary.Materials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    internal void ShowUnavailable()
    {
        SummaryText.Text = "Project data unavailable";
        DetailText.Text = "Open Industry to review the saved workspace. Previous quantities are not shown as current.";
        MissingText.Text = CoveredText.Text = ReviewText.Text = "Unavailable";
        DashboardMaterialGaps.ItemsSource = null;
        DashboardMaterialGaps.Visibility = Visibility.Collapsed;
    }
    private void Open_Click(object sender, RoutedEventArgs e) => OpenMaterials?.Invoke(this, EventArgs.Empty);
}
