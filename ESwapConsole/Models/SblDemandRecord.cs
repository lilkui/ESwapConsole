using CsvHelper.Configuration.Attributes;

namespace ESwapConsole.Models;

public sealed class SblDemandRecord
{
    public int Account { get; set; }

    public string Symbol { get; set; } = null!;

    public int DemandQuantity { get; set; }

    [Optional]
    public int LockQuantity { get; set; }
}
