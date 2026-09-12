using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckSkillPlanning()
    {
        var profile = SkillPlanning.Profile("Max Exhumer Miner");
        Check(profile.Count >= 30, "Max Exhumer profile includes mining, tank, hull and agility targets");
        var data = new EvePilotDashboard { TrainingProfile = new() { Attributes = new[] { 164,165,166,167,168 }.Select((id,i) => new EveTrainingAttribute { DogmaAttributeId=id, Total=20+i }).ToList() } };
        var original = SkillPlanning.Build(profile,data,false);
        var sorted = SkillPlanning.Build(profile,data,true);
        Check(original.Count==sorted.Count && Math.Abs(original.Sum(x=>x.Minutes)-sorted.Sum(x=>x.Minutes))<0.001, "Attribute reordering preserves total training duration and levels");
        var levels=new Dictionary<int,int>();
        foreach(var step in sorted)
        {
            if (levels.GetValueOrDefault(step.Id)!=step.Level-1 || !SkillPlanning.Catalog[step.Id].Prerequisites.All(p=>levels.GetValueOrDefault(p.Key)>=p.Value)) throw new Exception("Prerequisite order invalid");
            levels[step.Id]=step.Level;
        }
        Check(true,"Every planned skill follows its own previous level and prerequisites");
        var mining=SkillPlanning.Catalog.Values.Single(x=>x.Name=="Mining");
        var partial=new EvePilotDashboard { TrainingProfile=data.TrainingProfile, TrainedSkills=new[]{new EveSkillEntry {SkillId=mining.Id,TrainedSkillLevel=3,SkillpointsInSkill=10000}} };
        var remaining=SkillPlanning.Build(new(){{mining.Id,5}},partial,true).Where(x=>x.Id==mining.Id).ToList();
        Check(remaining.Sum(x=>x.RemainingSp)==SkillPlanning.Sp(mining.Rank,5)-10000,"Partial skill points are subtracted once across multiple target levels");
        var complete=new EvePilotDashboard {TrainingProfile=data.TrainingProfile,TrainedSkills=levels.Select(x=>new EveSkillEntry {SkillId=x.Key,TrainedSkillLevel=x.Value,SkillpointsInSkill=SkillPlanning.Sp(SkillPlanning.Catalog[x.Key].Rank,x.Value)}).ToList()};
        Check(SkillPlanning.Build(profile,complete,true).Count==0,"Completed profiles have no missing training");
        Check(SkillPlanning.Build(new(){{mining.Id,1}},new(),true).All(x=>x.Rate==0&&double.IsNaN(x.Minutes)),"Missing attributes never produce invented training estimates");
    }
}
