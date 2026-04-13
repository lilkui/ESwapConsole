using System.Runtime.InteropServices;
using ESwapConsole.Models;
using ESwapConsole.Services;
using ESwapSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace ESwapConsole;

public sealed class ESwapApplication(IOptions<AppConfig> options, ILogger<ESwapApi> apiLogger)
{
    private readonly AppConfig _config = options.Value;

    private ESwapApi? _api;
    private UserProfile? _activeUser;
    private SblDemandMatcher? _sblDemandMatcher;
    private RecallManager? _recallManager;
    private ReturnManager? _returnManager;

    public async Task RunAsync()
    {
        AnsiConsole.Clear();
        DisplayWelcomeHeader(ESwapApi.GetApiVersion()!);

        while (true)
        {
            UserProfile? user = PromptUserSelection();
            if (user is null)
            {
                break;
            }

            if (user != _activeUser && !await SwitchUserAsync(user).ConfigureAwait(false))
            {
                continue;
            }

            bool switchUser = await RunMenuLoop().ConfigureAwait(false);
            if (!switchUser)
            {
                break;
            }
        }

        DisposeCurrentSession();
        DisplayExitMessage();
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
            if (_activeUser is not null)
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

    private async Task<bool> SwitchUserAsync(UserProfile user)
    {
        DisposeCurrentSession();

        _activeUser = user;

        _api = new ESwapApi(_config.BrokerId, user.UserId, apiLogger, _config.RequestQpsLimit);
        _sblDemandMatcher = new SblDemandMatcher(_config, user, _api);
        _recallManager = new RecallManager(_config, _api);
        _returnManager = new ReturnManager(_config, _api);

        DisplayUserInfo();

        if (!await ConnectToApiAsync().ConfigureAwait(false))
        {
            DisposeCurrentSession();
            return false;
        }

        return true;
    }

    private void DisposeCurrentSession()
    {
        _sblDemandMatcher = null;
        _recallManager = null;
        _returnManager = null;
        _activeUser = null;

        if (_api is not null)
        {
            _api.Dispose();
            _api = null;
        }
    }

    private static void DisplayError(string message)
    {
        AnsiConsole.MarkupLine($"[red]错误：[/]{message}");
    }

    private async Task<bool> ConnectToApiAsync()
    {
        const int maxRetries = 3;
        int retryCount = 0;

        while (true)
        {
            try
            {
                if (retryCount > 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]第 {retryCount} 次重试（共 {maxRetries - 1} 次）...[/]");
                    await Task.Delay(2000).ConfigureAwait(false);
                }

                AnsiConsole.MarkupLine("[cyan]正在连接 ESwap 服务器...[/]");
                await _api!.ConnectAsync(_config.FrontAddress).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]已成功连接服务器。[/]");

                AnsiConsole.MarkupLine("[cyan]正在认证...[/]");
                await _api.AuthenticateAsync().ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]认证成功。[/]");

                AnsiConsole.MarkupLine("[cyan]正在登录...[/]");
                await _api.LoginAsync(_activeUser!.Password).ConfigureAwait(false);
                AnsiConsole.MarkupLine("[green]登录成功。[/]");

                return true;
            }
            catch (ExternalException ex)
            {
                retryCount++;
                DisplayError(ex.Message);

                if (retryCount >= maxRetries)
                {
                    AnsiConsole.MarkupLine("[red]连接失败，已尝试 {0} 次。[/]", maxRetries);
                    return false;
                }
            }
            catch (Exception ex)
            {
                DisplayError($"意外错误：{ex.Message}");
                return false;
            }
        }
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

    private void DisplayUserInfo()
    {
        Table userTable = new Table()
            .AddColumn(new TableColumn("Property").LeftAligned())
            .AddColumn(new TableColumn("Value").LeftAligned())
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Blue3));

        userTable.AddRow("[cyan]用户编号[/]", $"[yellow]{_activeUser!.UserId}[/]");
        userTable.AddRow("[cyan]账户列表[/]", $"[yellow]{string.Join(", ", _activeUser.AccountIds)}[/]");

        AnsiConsole.Write(userTable);
    }

    /// <returns><see langword="true" /> 表示切换用户；<see langword="false" /> 表示退出。</returns>
    private async Task<bool> RunMenuLoop()
    {
        while (true)
        {
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
                        await _sblDemandMatcher!.Match().ConfigureAwait(false);
                        break;

                    case "2":
                        await _recallManager!.RecallAsync().ConfigureAwait(false);
                        break;

                    case "3":
                        await _returnManager!.ReturnAsync().ConfigureAwait(false);
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
}
