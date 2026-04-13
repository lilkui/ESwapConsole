using ESwapConsole.Models;
using ESwapSharp;
using ESwapSharp.Interop;
using Spectre.Console;

namespace ESwapConsole.Services;

public sealed class SblDemandMatcher
{
    private readonly AppConfig _config;
    private readonly UserProfile _user;
    private readonly ESwapApi _api;

    public SblDemandMatcher(AppConfig config, UserProfile user, ESwapApi api)
    {
        _config = config;
        _user = user;
        _api = api;
        _api.RtnSecuLendOrder += OnRtnSecuLendOrder;
    }

    public async Task Match()
    {
        await AnsiConsole.Status()
            .Start("[cyan]正在加载融券需求记录...[/]", async ctx =>
            {
                try
                {
                    SblDemandRecord[] records = CsvHelper.ReadCsv<SblDemandRecord>(_config.SblDemandFilePath).ToArray();
                    foreach (SblDemandRecord record in records)
                    {
                        record.LockQuantity = 0;
                    }

                    Dictionary<int, Dictionary<string, SblDemandRecord>> recordsByAccountAndSymbol = records
                        .GroupBy(r => r.Account)
                        .ToDictionary(
                            g => g.Key,
                            g => g.ToDictionary(r => r.Symbol, StringComparer.OrdinalIgnoreCase)
                        );

                    ctx.Status = "[cyan]正在查询账户报单...[/]";

                    var matchResults = new List<MatchResult>();

                    foreach (int accountId in _user.AccountIds)
                    {
                        ctx.Status = $"[cyan]正在处理账户 {accountId}...[/]";

                        List<CESwapSecuLendOrderField> orders = await _api.QrySecuLendOrderAsync(accountId);
                        recordsByAccountAndSymbol.TryGetValue(accountId, out Dictionary<string, SblDemandRecord>? accountRecords);
                        if (accountRecords is null)
                        {
                            continue;
                        }

                        Dictionary<string, int> supplyBySymbol = new(StringComparer.OrdinalIgnoreCase);

                        foreach (CESwapSecuLendOrderField order in orders)
                        {
                            string symbol = NativeHelpers.ReadString(order.InstrumentID);
                            if (accountRecords.TryGetValue(symbol, out SblDemandRecord? record))
                            {
                                // Order already cancelled
                                if (Convert.ToBoolean(order.IsCancel) || order.SecuLendOrderStatus is Constants.ESWAP_COT_CANCELLED or Constants.ESWAP_COT_PART_CANCELLED)
                                {
                                    continue;
                                }

                                supplyBySymbol.TryGetValue(symbol, out int currentSupply);
                                supplyBySymbol[symbol] = currentSupply + order.VolumeTotal;

                                // Cancel order
                                if (order.VolumeTotal > 0)
                                {
                                    await _api.SecuLendOrderActionAsync(accountId, order.OrderLocalID);
                                }
                            }
                        }

                        foreach ((string symbol, SblDemandRecord record) in accountRecords)
                        {
                            supplyBySymbol.TryGetValue(symbol, out int supplyQuantity);
                            record.LockQuantity = Math.Min(record.DemandQuantity, supplyQuantity);

                            matchResults.Add(new MatchResult
                            {
                                Account = accountId,
                                Symbol = symbol,
                                DemandQuantity = record.DemandQuantity,
                                SupplyQuantity = supplyQuantity,
                                LockQuantity = record.LockQuantity,
                                Status = GetMatchStatus(record.DemandQuantity, record.LockQuantity),
                            });
                        }
                    }

                    foreach (SblDemandRecord record in records.Where(r => !_user.AccountIds.Contains(r.Account)))
                    {
                        matchResults.Add(new MatchResult
                        {
                            Account = record.Account,
                            Symbol = record.Symbol,
                            DemandQuantity = record.DemandQuantity,
                            SupplyQuantity = 0,
                            LockQuantity = record.LockQuantity,
                            Status = GetMatchStatus(record.DemandQuantity, record.LockQuantity),
                        });
                    }

                    matchResults.Sort(static (left, right) =>
                    {
                        int accountCompare = left.Account.CompareTo(right.Account);
                        return accountCompare != 0
                            ? accountCompare
                            : StringComparer.OrdinalIgnoreCase.Compare(left.Symbol, right.Symbol);
                    });

                    await Task.Delay(3000).ConfigureAwait(false);

                    ctx.Status = "[cyan]正在写入结果文件...[/]";
                    CsvHelper.WriteCsv(records, _config.SblDemandFilePath);

                    DisplayMatchResults(matchResults);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]错误：[/]{ex.Message}");
                }
            });
    }

    private static void OnRtnSecuLendOrder(in CESwapSecuLendOrderField order)
    {
        string symbol = NativeHelpers.ReadString(order.InstrumentID);
        char status = Convert.ToChar(order.SecuLendOrderStatus);

        if (Convert.ToBoolean(order.IsCancel))
        {
            AnsiConsole.MarkupLine($"[yellow]报单已撤销：[/]股票代码：[cyan]{symbol}[/]，状态：[yellow]{status}[/]");
        }
    }

    private static string GetMatchStatus(int demandQuantity, int lockQuantity)
    {
        if (lockQuantity <= 0)
        {
            return "未匹配";
        }

        return lockQuantity >= demandQuantity ? "完全匹配" : "部分匹配";
    }

    private void DisplayMatchResults(List<MatchResult> matches)
    {
        AnsiConsole.WriteLine();

        if (matches.Count > 0)
        {
            Table matchTable = new Table()
                .Title(new TableTitle("[cyan]需求匹配结果[/]", new Style(Color.Cyan)))
                .AddColumn(new TableColumn("账户").RightAligned())
                .AddColumn(new TableColumn("股票代码").LeftAligned())
                .AddColumn(new TableColumn("需求数量").RightAligned())
                .AddColumn(new TableColumn("供给数量").RightAligned())
                .AddColumn(new TableColumn("锁定数量").RightAligned())
                .AddColumn(new TableColumn("状态").Centered())
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Green3));

            foreach (MatchResult match in matches)
            {
                matchTable.AddRow(
                    $"[cyan]{match.Account}[/]",
                    $"[yellow]{match.Symbol}[/]",
                    $"[cyan]{match.DemandQuantity}[/]",
                    $"[cyan]{match.SupplyQuantity}[/]",
                    $"[green]{match.LockQuantity}[/]",
                    match.Status
                );
            }

            AnsiConsole.Write(matchTable);
        }

        AnsiConsole.WriteLine();
        int fullyMatchedCount = matches.Count(m => m.Status == "完全匹配");
        int partiallyMatchedCount = matches.Count(m => m.Status == "部分匹配");
        int unmatchedCount = matches.Count(m => m.Status == "未匹配");
        var summary = new Panel(
            new Markup($"[green]匹配完成。[/]共处理 [cyan]{matches.Count}[/] 条需求。\n" +
                       $"完全匹配：[green]{fullyMatchedCount}[/]，部分匹配：[yellow]{partiallyMatchedCount}[/]，未匹配：[red]{unmatchedCount}[/]\n" +
                       $"结果已写入：[yellow]{_config.SblDemandFilePath}[/]")
        )
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green3),
            Padding = new Padding(2, 1),
        };

        AnsiConsole.Write(summary);
    }

    private sealed class MatchResult
    {
        public required int Account { get; init; }
        public required string Symbol { get; init; }
        public required int DemandQuantity { get; init; }
        public required int SupplyQuantity { get; init; }
        public required int LockQuantity { get; init; }
        public required string Status { get; init; }
    }
}
