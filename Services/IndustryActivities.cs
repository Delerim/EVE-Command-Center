namespace EveCommandCenter.Services;
public static class IndustryActivities
{
    public static string Code(long id)=>id switch {1=>"manufacturing",3=>"research_time",4=>"research_material",5=>"copying",8=>"invention",9 or 11=>"reaction",_=>"other"};
    public static bool Matches(string filter,string activity)=>filter=="all"||filter==activity;
    public static string Color(string activity)=>activity switch {"manufacturing"=>"#74D6C9","research_material"=>"#80BFFF","research_time"=>"#78DCE8","copying"=>"#B8A1FF","invention"=>"#E3A3EF","reaction"=>"#FFBE85",_=>"#9FC5C7"};
    public static string StatusColor(string status)=>status.Contains("READY",StringComparison.OrdinalIgnoreCase)?"#74D6C9":status=="BUY MATERIALS"?"#FFD166":status=="SKILLS REQUIRED"?"#D4A5FF":status=="BLUEPRINT IN USE"?"#80BFFF":status=="DATA / PERMISSIONS NEEDED"?"#FF7C82":"#9FC5C7";
}
