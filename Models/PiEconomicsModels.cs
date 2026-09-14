namespace EveCommandCenter.Models;
public sealed class PiTaxSettings
{
    public double? PocoPercent {get;set;}
    public double SalesTaxPercent {get;set;}=7.5;
    public double BrokerPercent {get;set;}=3;
    public bool SellImmediately {get;set;}=true;
    public double OtherCost {get;set;}
    public DateTimeOffset Updated {get;set;}
}
public sealed class PiBatchEstimate
{
    public Dictionary<int,double> Inputs {get;set;}=new();
    public double? MaterialCost {get;set;}
    public double? ImportTax {get;set;}
    public double? ExportTax {get;set;}
    public double? Gross {get;set;}
    public double? SaleFees {get;set;}
    public double Other {get;set;}
    public double? Net=>Gross-MaterialCost-ImportTax-ExportTax-SaleFees-Other;
    public string Color=>Net==null?"#FFD166":Net>=0?"#74D6C9":"#FF7373";
    public static string Money(double? value)=>value.HasValue?$"{value:N0} ISK":"Pending rate / quote";
    public string NetText=>Money(Net);
    public string Detail=>$"Gross sale: {Money(Gross)}\nT1 replacement cost: {Money(MaterialCost)}\nImport tax: {Money(ImportTax)}\nExport tax: {Money(ExportTax)}\nSelling tax / commission: {Money(SaleFees)}\nOther batch costs: {Money(Other)}";
}
