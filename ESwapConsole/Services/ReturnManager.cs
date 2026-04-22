using System.Collections.Concurrent;
using ESwapConsole.Models;
using ESwapSharp;
using ESwapSharp.Interop;
using Spectre.Console;
using static ESwapSharp.Interop.NativeHelpers;

namespace ESwapConsole.Services;

public sealed class ReturnManager : IDisposable
{
    private static readonly TimeSpan CallbackPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromSeconds(10);

    private readonly AppConfig _config;
    private readonly ESwapApi _api;
    private readonly ConcurrentDictionary<long, ReturnResult> _resultsByOrderRef = new();
    private readonly ConcurrentDictionary<long, string> _instructionNamesByOrderRef = new();

    public ReturnManager(AppConfig config, ESwapApi api)
    {
        _config = config;
        _api = api;
        _api.RtnContractOperation += OnRtnContractOperation;
    }

    public async Task ReturnAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _resultsByOrderRef.Clear();
            _instructionNamesByOrderRef.Clear();

            AnsiConsole.MarkupLine("[cyan]正在加载合约...[/]");
            ReturnContract[] contracts = CsvHelper.ReadCsv<ReturnContract>(_config.ReturnFilePath);

            AnsiConsole.MarkupLine("[cyan]正在处理还券指令...[/]");
            foreach (ReturnContract contract in contracts)
            {
                await SubmitReturnInstructionAsync(contract, Constants.ESWAP_COTT_RETURN_YD_POSITION, contract.ReturnYesterdayQuantity, cancellationToken).ConfigureAwait(false);
                await SubmitReturnInstructionAsync(contract, Constants.ESWAP_COTT_RETURN_TD_POSITION, contract.ReturnTodayQuantity, cancellationToken).ConfigureAwait(false);
            }

            await WaitForCallbacksAsync(cancellationToken).ConfigureAwait(false);
            DisplayReturnResults(contracts.Length);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]错误：[/]{ex.Message}");
        }
    }

    public void Dispose()
    {
        _api.RtnContractOperation -= OnRtnContractOperation;
    }

    private async Task SubmitReturnInstructionAsync(ReturnContract contract, sbyte instructionType, int quantity, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
        {
            return;
        }

        string instructionName = GetInstructionName(instructionType);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long orderRef = await _api.ContractInstructionInsertAsync(contract.AccountId, contract.ContractId, instructionType, quantity).ConfigureAwait(false);
            _instructionNamesByOrderRef[orderRef] = instructionName;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]处理合约 {contract.ContractId} 的{instructionName}时出错：[/]{ex.Message}");
        }
    }

    private static string GetInstructionName(sbyte instructionType)
        => instructionType == Constants.ESWAP_COTT_RETURN_YD_POSITION ? "提前还券" : "今仓还券";

    private void OnRtnContractOperation(in CESwapContractOperationField instruction)
    {
        long orderRef = instruction.OrderRef;
        if (!_instructionNamesByOrderRef.TryGetValue(orderRef, out string? instructionName))
        {
            return;
        }

        string contractId = ReadString(instruction.ContractID);
        char status = (char)instruction.OperationResult;
        int volume = instruction.OperationVolume;

        var result = new ReturnResult
        {
            ContractId = contractId,
            InstructionName = instructionName,
            Status = status.ToString(),
            ReturnedVolume = volume,
            OrderRef = orderRef,
        };

        _resultsByOrderRef[orderRef] = result;
        _instructionNamesByOrderRef.TryRemove(orderRef, out _);

        AnsiConsole.MarkupLine(
            $"委托编号：[bold yellow]{orderRef}[/]，合约ID：[cyan]{contractId}[/]，类型：[magenta]{instructionName}[/]，操作：[magenta]{(char)instruction.Operation}[/]，状态：[yellow]{status}[/]，还券数量：[green]{volume}[/]");
    }

    private async Task WaitForCallbacksAsync(CancellationToken cancellationToken)
    {
        if (_instructionNamesByOrderRef.IsEmpty)
        {
            return;
        }

        AnsiConsole.MarkupLine("[cyan]等待处理完成...[/]");

        using CancellationTokenSource timeoutCancellation = new(CallbackTimeout);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using PeriodicTimer timer = new(CallbackPollInterval);

        try
        {
            while (!_instructionNamesByOrderRef.IsEmpty && await timer.WaitForNextTickAsync(linkedCancellation.Token).ConfigureAwait(false))
            {
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine($"[yellow]部分回报在 {CallbackTimeout.TotalSeconds:0} 秒内未返回，当前仍有 {_instructionNamesByOrderRef.Count} 笔待确认。[/]");
        }
    }

    private void DisplayReturnResults(int contractCount)
    {
        AnsiConsole.WriteLine();

        ReturnResult[] results = _resultsByOrderRef.Values
            .OrderBy(static result => result.OrderRef)
            .ToArray();

        if (results.Length > 0)
        {
            Table resultTable = new Table()
                .Title(new TableTitle("[cyan]还券结果[/]", new Style(Color.Cyan)))
                .AddColumn(new TableColumn("委托编号").RightAligned())
                .AddColumn(new TableColumn("合约编号").LeftAligned())
                .AddColumn(new TableColumn("还券类型").Centered())
                .AddColumn(new TableColumn("状态").Centered())
                .AddColumn(new TableColumn("还券数量").RightAligned())
                .Border(TableBorder.Rounded)
                .BorderStyle(new Style(Color.Green3));

            foreach (ReturnResult result in results)
            {
                resultTable.AddRow(
                    $"[yellow]{result.OrderRef}[/]",
                    $"[yellow]{result.ContractId}[/]",
                    $"[magenta]{result.InstructionName}[/]",
                    $"[cyan]{result.Status}[/]",
                    $"[green]{result.ReturnedVolume}[/]"
                );
            }

            AnsiConsole.Write(resultTable);

            string dataFolder = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dataFolder);
            string outputPath = Path.Combine(dataFolder, $"return_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            CsvHelper.WriteCsv(results, outputPath);

            AnsiConsole.WriteLine();
            var summary = new Panel(
                new Markup($"[green]还券完成。[/]共处理 [cyan]{results.Length}[/] 笔指令，涉及 [cyan]{contractCount}[/] 份合约。\n" +
                           $"来源文件：[yellow]{_config.ReturnFilePath}[/]\n" +
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
                new Markup("[yellow]暂无还券结果。[/]")
            )
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Yellow3),
                Padding = new Padding(2, 1),
            };

            AnsiConsole.Write(summary);
        }
    }

    private sealed record ReturnResult
    {
        public required long OrderRef { get; init; }
        public required string ContractId { get; init; }
        public required string InstructionName { get; init; }
        public required string Status { get; init; }
        public required int ReturnedVolume { get; init; }
    }
}
