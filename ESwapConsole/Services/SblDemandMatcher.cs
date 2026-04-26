using System.Collections.Concurrent;
using ESwapConsole.Models;
using ESwapSharp;
using ESwapSharp.Interop;
using Spectre.Console;

namespace ESwapConsole.Services;

public sealed class SblDemandMatcher : IDisposable
{
    private static readonly TimeSpan CallbackPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

    private readonly AppConfig _config;
    private readonly UserProfile _user;
    private readonly ESwapApi _api;
    private readonly ConcurrentDictionary<long, byte> _pendingCancelOrderLocalIds = new();

    public SblDemandMatcher(AppConfig config, UserProfile user, ESwapApi api)
    {
        _config = config;
        _user = user;
        _api = api;
        _api.RtnSecuLendOrder += OnRtnSecuLendOrder;
    }

    public async Task Match(CancellationToken cancellationToken = default)
    {
        await AnsiConsole.Status()
            .Start("[cyan]正在加载融券需求记录...[/]", async ctx =>
            {
                try
                {
                    _pendingCancelOrderLocalIds.Clear();

                    cancellationToken.ThrowIfCancellationRequested();
                    SblDemandRecord[] records = CsvHelper.ReadCsv<SblDemandRecord>(_config.SblDemandFilePath);
                    foreach (SblDemandRecord record in records)
                    {
                        record.LockQuantity = 0;
                    }

                    HashSet<int> managedAccounts = _user.AccountIds.ToHashSet();

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
                        cancellationToken.ThrowIfCancellationRequested();
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
                                    cancellationToken.ThrowIfCancellationRequested();
                                    long orderLocalId = order.OrderLocalID;
                                    _pendingCancelOrderLocalIds[orderLocalId] = 0;

                                    try
                                    {
                                        await _api.SecuLendOrderActionAsync(accountId, orderLocalId).ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        _pendingCancelOrderLocalIds.TryRemove(orderLocalId, out _);
                                        throw;
                                    }
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

                    foreach (SblDemandRecord record in records.Where(r => !managedAccounts.Contains(r.Account)))
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

                    await WaitForCancelCallbacksAsync(cancellationToken).ConfigureAwait(false);

                    matchResults.Sort(static (left, right) =>
                    {
                        int accountCompare = left.Account.CompareTo(right.Account);
                        return accountCompare != 0
                            ? accountCompare
                            : StringComparer.OrdinalIgnoreCase.Compare(left.Symbol, right.Symbol);
                    });

                    cancellationToken.ThrowIfCancellationRequested();
                    ctx.Status = "[cyan]正在写入结果文件...[/]";
                    CsvHelper.WriteCsv(records, _config.SblDemandFilePath);

                    DisplayMatchResults(matchResults);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("[yellow]操作已取消。[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]错误：[/]{ex.Message}");
                }
            });
    }

    public void Dispose()
    {
        _api.RtnSecuLendOrder -= OnRtnSecuLendOrder;
    }

    private void OnRtnSecuLendOrder(in CESwapSecuLendOrderField order)
    {
        if (!Convert.ToBoolean(order.IsCancel))
        {
            return;
        }

        string symbol = NativeHelpers.ReadString(order.InstrumentID);
        char status = Convert.ToChar(order.SecuLendOrderStatus);

        AnsiConsole.MarkupLine($"[yellow]报单已撤销：[/]股票代码：[cyan]{symbol}[/]，状态：[yellow]{status}[/]");
        _pendingCancelOrderLocalIds.TryRemove(order.OrderLocalID, out _);
    }

    private async Task WaitForCancelCallbacksAsync(CancellationToken cancellationToken)
    {
        if (_pendingCancelOrderLocalIds.IsEmpty)
        {
            return;
        }

        AnsiConsole.MarkupLine("[cyan]等待撤单回报完成...[/]");

        using CancellationTokenSource timeoutCancellation = new(CallbackTimeout);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using PeriodicTimer timer = new(CallbackPollInterval);

        try
        {
            while (!_pendingCancelOrderLocalIds.IsEmpty && await timer.WaitForNextTickAsync(linkedCancellation.Token).ConfigureAwait(false))
            {
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[yellow]部分撤单回报在 {CallbackTimeout.TotalSeconds:0} 秒内未返回，当前仍有 {_pendingCancelOrderLocalIds.Count} 笔待确认。[/]");
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

    private sealed record MatchResult
    {
        public required int Account { get; init; }
        public required string Symbol { get; init; }
        public required int DemandQuantity { get; init; }
        public required int SupplyQuantity { get; init; }
        public required int LockQuantity { get; init; }
        public required string Status { get; init; }
    }
}
