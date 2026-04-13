using System.Collections.Concurrent;
using ESwapConsole.Models;
using ESwapSharp;
using ESwapSharp.Interop;
using Spectre.Console;
using static ESwapSharp.Interop.NativeHelpers;

namespace ESwapConsole.Services;

public sealed class RecallManager
{
    private readonly AppConfig _config;
    private readonly ESwapApi _api;
    private readonly ConcurrentDictionary<long, RecallResult> _resultsByOrderRef = new();
    private readonly ConcurrentDictionary<long, byte> _pendingOrderRefs = new();

    public RecallManager(AppConfig config, ESwapApi api)
    {
        _config = config;
        _api = api;
        _api.RtnContractOperation += OnRtnContractOperation;
    }

    public async Task RecallAsync()
    {
        try
        {
            _resultsByOrderRef.Clear();
            _pendingOrderRefs.Clear();

            AnsiConsole.MarkupLine("[cyan]正在加载合约...[/]");
            SblContract[] contracts = CsvHelper.ReadCsv<SblContract>(_config.RecallFilePath).ToArray();

            AnsiConsole.MarkupLine("[cyan]正在处理召回指令...[/]");
            foreach (SblContract contract in contracts)
            {
                try
                {
                    long orderRef = await _api.ContractInstructionInsertAsync(contract.AccountId, contract.ContractId, contract.Quantity);
                    _pendingOrderRefs[orderRef] = 0;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]处理合约 {contract.ContractId} 时出错：[/]{ex.Message}");
                }
            }

            AnsiConsole.MarkupLine("[cyan]等待处理完成...[/]");
            await Task.Delay(3000).ConfigureAwait(false);
            DisplayRecallResults();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]错误：[/]{ex.Message}");
        }
    }

    private void OnRtnContractOperation(in CESwapContractOperationField instruction)
    {
        long orderRef = instruction.OrderRef;
        if (!_pendingOrderRefs.ContainsKey(orderRef))
        {
            return;
        }

        string contractId = ReadString(instruction.ContractID);
        char status = (char)instruction.OperationResult;
        int volume = instruction.OperationVolume;

        var result = new RecallResult
        {
            ContractId = contractId,
            Status = status.ToString(),
            RecalledVolume = volume,
            OrderRef = orderRef,
        };

        _resultsByOrderRef[orderRef] = result;

        AnsiConsole.MarkupLine(
            $"委托编号：[bold yellow]{orderRef}[/]，合约ID：[cyan]{contractId}[/]，操作：[magenta]{(char)instruction.Operation}[/]，状态：[yellow]{status}[/]，召回数量：[green]{volume}[/]");
    }

    private void DisplayRecallResults()
    {
        AnsiConsole.WriteLine();

        RecallResult[] results = _resultsByOrderRef.Values.ToArray();

        if (results.Length > 0)
        {
            Table resultTable = new Table()
                .Title(new TableTitle("[cyan]合约召回结果[/]", new Style(Color.Cyan)))
                .AddColumn(new TableColumn("委托编号").RightAligned())
                .AddColumn(new TableColumn("合约编号").LeftAligned())
                .AddColumn(new TableColumn("状态").Centered())
                .AddColumn(new TableColumn("召回数量").RightAligned())
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Green3));

            foreach (RecallResult result in results)
            {
                resultTable.AddRow(
                    $"[yellow]{result.OrderRef}[/]",
                    $"[yellow]{result.ContractId}[/]",
                    $"[cyan]{result.Status}[/]",
                    $"[green]{result.RecalledVolume}[/]"
                );
            }

            AnsiConsole.Write(resultTable);

            string dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataFolder);
            string outputPath = Path.Combine(dataFolder, $"recall_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            CsvHelper.WriteCsv(results, outputPath);

            AnsiConsole.WriteLine();
            var summary = new Panel(
                new Markup($"[green]召回操作完成。[/]共处理 [cyan]{results.Length}[/] 份合约。\n" +
                           $"来源文件：[yellow]{_config.RecallFilePath}[/]\n" +
                           $"结果已保存至：[yellow]{outputPath}[/]")
            )
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green3),
                Padding = new Padding(2, 1),
            };

            AnsiConsole.Write(summary);
        }
        else
        {
            AnsiConsole.WriteLine();
            var summary = new Panel(
                new Markup("[yellow]暂无召回结果。[/]")
            )
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow3),
                Padding = new Padding(2, 1),
            };

            AnsiConsole.Write(summary);
        }
    }

    private sealed class RecallResult
    {
        public required long OrderRef { get; init; }
        public required string ContractId { get; init; }
        public required string Status { get; init; }
        public required int RecalledVolume { get; init; }
    }
}
