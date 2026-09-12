using System.Windows;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class NotificationCenterWindow:Window
{
    private bool _history;
    public NotificationCenterWindow(){InitializeComponent();NotificationCenterService.Current.Changed+=Update;Closed+=(_,_)=>NotificationCenterService.Current.Changed-=Update;Update();}
    private void Update(){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(Update);return;}var rows=NotificationCenterService.Current.Items;Rows.ItemsSource=rows.Where(x=>_history||x.Active).OrderByDescending(x=>x.Active).ThenBy(x=>x.Read).ThenByDescending(x=>x.Updated).ToList();Summary.Text=$"{rows.Count(x=>x.Active)} current PI issues | {rows.Count(x=>!x.Read)} unread | "+(_history?"All notifications":"Current issues");}
    private void Read_Click(object s,RoutedEventArgs e)=>NotificationCenterService.Current.MarkRead();
    private void Current_Click(object s,RoutedEventArgs e){_history=false;Update();}
    private void History_Click(object s,RoutedEventArgs e){_history=true;Update();}
    private void Open_Click(object s,RoutedEventArgs e){var source=((System.Windows.Controls.Button)s).Tag?.ToString();var app=BackgroundOperations.Current;if(source=="PI")app.OpenPlanetary();else if(source=="OMEGA")app.OpenOmega();else if(source=="INDUSTRY")app.OpenIndustry();else if(source=="CONTRACTS")app.OpenContracts();else app.OpenMoons();}
}
