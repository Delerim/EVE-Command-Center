using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Views;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckEmbeddedLifecycle()
    {
        var valued=StatTrackerService.CalculateFleetMiningValue(new Dictionary<string,double>{{"A",100},{"B",50},{"unknown",20}}, ore=>ore=="A"?2:ore=="B"?3:null);
        Check(valued.Value==350&&valued.MissingQuotes==1,"Fleet day valuation sums ore totals and flags unpriced ore rather than claiming completeness");
        Check(StatTrackerService.CalculateFleetMiningValue(new Dictionary<string,double>(),_=>null)==(0d,0),"Empty mining day has zero value and no missing quotes");
        var shell=new CommandCenterWindow(live:false){ShowActivated=false,ShowInTaskbar=false,Opacity=0,WindowState=WindowState.Normal};
        var surface=(ContentControl)shell.FindName("ModuleSurface");
        using var host=new EmbeddedModuleHost(shell,surface);
        var created=new List<Window>();int closed=0,loaded=0;
        Window Create(){var window=new EveCommandCenter.Views.LayoutTestWindow{Content=new TextBlock{Text="Embedded test"},DataContext="inherited"};window.Loaded+=(_,_)=>loaded++;window.Closed+=(_,_)=>closed++;created.Add(window);return window;}
        void Pump()=>Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        try
        {
            shell.Show();Pump();
            var first=host.Show("first",Create);Pump();
            Check(loaded==1,"Embedded window initializes Loaded exactly once");
            Check(!first.IsVisible&&first.Content==null&&surface.Content is FrameworkElement,"Embedded backing window stays hidden after content is moved");
            Check((surface.Content as FrameworkElement)?.DataContext as string=="inherited","Embedding preserves inherited data context");
            using(var layouts=new WindowLayoutService(Path.Combine(Path.GetTempPath(),"ecc-embedded-"+Guid.NewGuid())))
            {
                layouts.Attach(first,"test");first.Left=400;first.Width=850;
                Check(layouts.Get("test")==null,"Embedded backing windows cannot restore or overwrite standalone saved placement");
            }
            for(int i=0;i<100;i++){host.Show(i%2==0?"second":"first",Create);Pump();}
            Check(created.Count==2&&loaded==2,"Repeated workspace switching reuses two initialized backing windows");
            Check(created.All(w=>!w.IsVisible),"Repeated navigation never exposes a backing window");
            var second=host.Show("second",Create);bool cancelClose=true;
            second.Closing+=(_,e)=>e.Cancel=cancelClose;
            host.Close("second");Pump();
            Check(host.Contains("second")&&surface.Content!=null&&closed==0,"Cancelled module close keeps its tab content attached");
            cancelClose=false;
            host.Close("second");Pump();Check(closed==1&&!host.Contains("second"),"Closing a workspace closes and removes its backing window");
            host.Show("second",Create);Pump();Check(created.Count==3&&loaded==3,"Closed workspaces can be reopened with a fresh lifecycle");
            host.Dispose();Pump();Check(closed==3&&surface.Content==null,"Host disposal releases all module windows and visual content");
        }
        finally{host.Dispose();shell.Close();}
    }
}
