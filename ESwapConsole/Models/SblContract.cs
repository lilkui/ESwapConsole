using CsvHelper.Configuration.Attributes;

namespace ESwapConsole.Models;

public sealed class SblContract
{
    [Name("对手方账号")]
    public int AccountId { get; set; }

    [Name("对手方合同编号")]
    public string ContractId { get; set; } = null!;

    [Name("未归还")]
    public int Quantity { get; set; }
}
