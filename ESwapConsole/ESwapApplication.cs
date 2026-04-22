using System.Runtime.InteropServices;
using ESwapConsole.Models;
using ESwapConsole.Services;
using ESwapSharp;
using ESwapSharp.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace ESwapConsole;

public sealed class ESwapApplication(IOptions<AppConfig> options, ILogger<ESwapApi> apiLogger)
{
    private const int MaxConnectionRetries = 3;
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(2);

    private readonly AppConfig _config = options.Value;

    private SessionContext? _session;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        AnsiConsole.Clear();
        DisplayWelcomeHeader(ESwapApi.GetApiVersion() ?? "unknown");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UserProfile? user = PromptUserSelection();
                if (user is null)
                {
                    break;
                }

                if (_session?.User != user && !await SwitchUserAsync(user, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                SessionContext session = _session ?? throw new InvalidOperationException("活动会话尚未初始化。");
                bool switchUser = await RunMenuLoopAsync(session, cancellationToken).ConfigureAwait(false);
                if (!switchUser)
                {
                    break;
                }
            }
        }
        finally
        {
            DisposeCurrentSession();
            DisplayExitMessage();
        }
    }

    private UserProfile? PromptUserSelection()
    {
        AnsiConsole.WriteLine();

        if (_config.Users.Length == 0)
        {
            DisplayError("未配置任何用户，请检查 appsettings.json。");
            return null;
        }

        if (_config.Users.Length == 1)
        {
            if (_session is not null)
            {
                AnsiConsole.MarkupLine("[yellow]仅配置了一个用户，无法切换。[/]");
                return null;
            }

            return _config.Users[0];
        }

        string selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]请选择登录用户：[/]")
                .AddChoices(_config.Users.Select(u => u.UserId)));

        return _config.Users.First(u => u.UserId == selected);
    }

    private async Task<bool> SwitchUserAsync(UserProfile user, CancellationToken cancellationToken)
    {
        DisposeCurrentSession();

        SessionContext session = CreateSession(user);
        _session = session;

        DisplayUserInfo(user);

        if (!await ConnectToApiAsync(session, cancellationToken).ConfigureAwait(false))
        {
            DisposeCurrentSession();
            return false;
        }

        return true;
    }

    private SessionContext CreateSession(UserProfile user)
    {
        ESwapApi api = new(_config.BrokerId, user.UserId, apiLogger, _config.RequestQpsLimit);
        return new SessionContext(
            user,
            api,
            new SblDemandMatcher(_config, user, api),
            new RecallManager(_config, api),
            new ReturnManager(_config, api));
    }

    private void DisposeCurrentSession()
    {
        _session?.Dispose();
        _session = null;
    }

    private static void DisplayError(string message)
    {
        AnsiConsole.MarkupLine($"[red]错误：[/]{message}");
    }

    private async Task<bool> ConnectToApiAsync(SessionContext session, CancellationToken cancellationToken)
    {
        for (int retryCount = 0; retryCount < MaxConnectionRetries; retryCount++)
        {
            try
            {
                if (retryCount > 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]第 {retryCount} 次重试（共 {MaxConnectionRetries - 1} 次）...[/]");
                    await Task.Delay(ConnectionRetryDelay, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                AnsiConsole.MarkupLine("[cyan]正在连接 ESwap 服务器...[/]");
                await session.Api.ConnectAsync(_config.FrontAddress, _config.DataFrontAddress).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]已成功连接服务器。[/]");

                await AuthenticateAndLoginAsync(session, ESWAP_TE_CONNECTION_TYPE.ESWAP_TECN_TRADE, cancellationToken).ConfigureAwait(false);
                await AuthenticateAndLoginAsync(session, ESWAP_TE_CONNECTION_TYPE.ESWAP_TECN_DATA, cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (ExternalException ex)
            {
                DisplayError(ex.Message);

                if (retryCount == MaxConnectionRetries - 1)
                {
                    AnsiConsole.MarkupLine("[red]连接失败，已尝试 {0} 次。[/]", MaxConnectionRetries);
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                DisplayError($"意外错误：{ex.Message}");
                return false;
            }
        }

        return false;
    }

    private async Task AuthenticateAndLoginAsync(
        SessionContext session,
        ESWAP_TE_CONNECTION_TYPE connectionType,
        CancellationToken cancellationToken)
    {
        string serverName = connectionType == ESWAP_TE_CONNECTION_TYPE.ESWAP_TECN_TRADE ? "交易" : "数据";

        cancellationToken.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine($"[cyan]正在认证{serverName}服务...[/]");
        await session.Api.AuthenticateAsync(connectionType).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]{serverName}服务认证成功。[/]");

        cancellationToken.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine($"[cyan]正在登录{serverName}服务...[/]");
        await session.Api.LoginAsync(session.User.Password, connectionType).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]{serverName}服务登录成功。[/]");
    }

    private void DisplayWelcomeHeader(string version)
    {
        var panel = new Panel(
            new Text(
                "  ESwap Console  ",
                new Style(Color.Cyan1, decoration: Decoration.Bold)
            )
        )
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan3),
            Padding = new Padding(2, 1),
        };

        AnsiConsole.Write(panel);

        Table versionTable = new Table()
            .AddColumn(new TableColumn("Property").LeftAligned())
            .AddColumn(new TableColumn("Value").LeftAligned())
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Blue3));

        versionTable.AddRow("[cyan]接口版本[/]", $"[green]{version}[/]");
        versionTable.AddRow("[cyan]经纪商编号[/]", $"[yellow]{_config.BrokerId}[/]");

        AnsiConsole.Write(versionTable);
    }

    private static void DisplayUserInfo(UserProfile user)
    {
        Table userTable = new Table()
            .AddColumn(new TableColumn("Property").LeftAligned())
            .AddColumn(new TableColumn("Value").LeftAligned())
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Blue3));

        userTable.AddRow("[cyan]用户编号[/]", $"[yellow]{user.UserId}[/]");
        userTable.AddRow("[cyan]账户列表[/]", $"[yellow]{string.Join(", ", user.AccountIds)}[/]");

        AnsiConsole.Write(userTable);
    }

    /// <returns><see langword="true" /> 表示切换用户；<see langword="false" /> 表示退出。</returns>
    private async Task<bool> RunMenuLoopAsync(SessionContext session, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AnsiConsole.WriteLine();
            var panel = new Panel(
                new Text("功能菜单", new Style(Color.White, decoration: Decoration.Bold))
            )
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Cyan3),
                Padding = new Padding(1, 0),
            };

            AnsiConsole.Write(panel);

            var menuItems = new[]
            {
                new
                {
                    Key = "1",
                    Description = "匹配融券需求",
                },
                new
                {
                    Key = "2",
                    Description = "召回合约",
                },
                new
                {
                    Key = "3",
                    Description = "还券",
                },
                new
                {
                    Key = "S",
                    Description = "切换用户",
                },
                new
                {
                    Key = "X",
                    Description = "退出",
                },
            };

            foreach (var item in menuItems)
            {
                AnsiConsole.MarkupLine($"  [cyan]{item.Key}.[/] {item.Description}");
            }

            AnsiConsole.MarkupLine("");
            string input = AnsiConsole.Ask<string>("[cyan]请选择功能：[/] ").ToUpperInvariant();

            AnsiConsole.WriteLine();

            try
            {
                switch (input)
                {
                    case "1":
                        await session.SblDemandMatcher.Match(cancellationToken).ConfigureAwait(false);
                        break;

                    case "2":
                        await session.RecallManager.RecallAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case "3":
                        await session.ReturnManager.ReturnAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case "S":
                        return true;

                    case "X":
                        return false;

                    default:
                        DisplayError("无效选项，请重新选择。");
                        break;
                }
            }
            catch (Exception ex)
            {
                DisplayError($"发生错误：{ex.Message}");
            }
        }
    }

    private void DisplayExitMessage()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]程序已退出。[/]");
    }

    private sealed class SessionContext(
        UserProfile user,
        ESwapApi api,
        SblDemandMatcher sblDemandMatcher,
        RecallManager recallManager,
        ReturnManager returnManager) : IDisposable
    {
        public UserProfile User { get; } = user;

        public ESwapApi Api { get; } = api;

        public SblDemandMatcher SblDemandMatcher { get; } = sblDemandMatcher;

        public RecallManager RecallManager { get; } = recallManager;

        public ReturnManager ReturnManager { get; } = returnManager;

        public void Dispose()
        {
            SblDemandMatcher.Dispose();
            RecallManager.Dispose();
            ReturnManager.Dispose();
            Api.Dispose();
        }
    }
}
