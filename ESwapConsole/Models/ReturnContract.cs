using CsvHelper.Configuration.Attributes;

namespace ESwapConsole.Models;

public sealed class ReturnContract
{
    [Name("账号")]
    public int AccountId { get; set; }

    [Name("合同编号")]
    public string ContractId { get; set; } = null!;

    [Name("昨仓还券")]
    public int ReturnYesterdayQuantity { get; set; }

    [Name("今仓还券")]
    public int ReturnTodayQuantity { get; set; }
}
