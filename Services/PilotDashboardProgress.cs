using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class PilotDashboardProgress
{
    public static EvePilotDashboard Merge(EvePilotDashboard incoming, EvePilotDashboard? previous)
    {
        if (!incoming.CoreOnly || previous == null || previous.CoreOnly || previous.Summary.CharacterId != incoming.Summary.CharacterId) return incoming;
        var fresh=incoming.Summary;var old=previous.Summary;
        return new EvePilotDashboard {
            Summary=new EvePilotSummary {CharacterId=fresh.CharacterId,CharacterName=fresh.CharacterName,TotalSp=fresh.TotalSp,CurrentSkill=fresh.CurrentSkill,CurrentSkillRemaining=fresh.CurrentSkillRemaining,CurrentProgressPercent=fresh.CurrentProgressPercent,QueueEndsIn=fresh.QueueEndsIn,WalletBalance=old.WalletBalance,CurrentSystem=old.CurrentSystem,CurrentShip=old.CurrentShip},
            TrainedSkills=incoming.TrainedSkills,SkillQueue=incoming.SkillQueue,TrainingProfile=previous.TrainingProfile,
            WalletOverview=previous.WalletOverview,WalletJournal=previous.WalletJournal,WalletTransactions=previous.WalletTransactions,PlexTransactions=previous.PlexTransactions
        };
    }
}
