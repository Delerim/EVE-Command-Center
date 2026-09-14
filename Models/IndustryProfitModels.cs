namespace EveCommandCenter.Models;
public sealed class IndustryCostSettings
{
    public double TaxPercent {get;set;}=7.5;
    public double BrokerPercent {get;set;}=3;
    public double? CopyCost {get;set;}
    public double? JobCost {get;set;}
    public bool InstantSale {get;set;}
}
public sealed class IndustryProfitRow
{
    public string Name {get;set;}="";
    public double? Materials {get;set;}
    public double? Missing {get;set;}
    public double? PurchaseFees {get;set;}
    public double? Sale {get;set;}
    public double? SaleFees {get;set;}
    public double? Total {get;set;}
    public double? Profit {get;set;}
    public string Note {get;set;}="";
    static string Money(double? value)=>value.HasValue?$"{value:N0} ISK":"Pending";
    public string ProfitText=>Money(Profit);
    public string Color=>Profit==null?"#9FC5C7":Profit>=0?"#74D6C9":"#FF7373";
    public string Breakdown=>$"All materials: {Money(Materials)}\nMissing materials only: {Money(Missing)} (before buy-order fees)\nBuy-order commission: {Money(PurchaseFees)}\nTotal job cost: {Money(Total)}\nGross sale: {Money(Sale)}\nSale tax + commission: {Money(SaleFees)}";
}
