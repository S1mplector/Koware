// Author: Ilgaz Mehmetoğlu
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Orchestration;
using Koware.Autoconfig.Storage;
using Koware.Cli.Console;
using Spectre.Console;

namespace Koware.Cli.Commands;

/// <summary>
/// Provider management command with subcommands for autoconfig, list, show, etc.
/// </summary>
public sealed class ProviderCommand : ICliCommand
{
    public string Name => "provider";
    public string Description => "Manage content providers (autoconfig, list, show, remove)";
    public IReadOnlyList<string> Aliases => ["providers", "prov"];
    public bool RequiresProvider => false;

    private readonly IAutoconfigOrchestrator _orchestrator;
    private readonly IProviderStore _providerStore;

    public ProviderCommand(IAutoconfigOrchestrator orchestrator, IProviderStore providerStore)
    {
        _orchestrator = orchestrator;
        _providerStore = providerStore;
    }

    public async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        var cancellationToken = context.CancellationToken;

        if (args.Length == 0)
        {
            return await ListProvidersAsync(cancellationToken);
        }

        var subcommand = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        return subcommand switch
        {
            "autoconfig" or "auto" => await AutoconfigAsync(subArgs, cancellationToken),
            "list" or "ls" => await ListProvidersAsync(cancellationToken),
            "show" or "info" => await ShowProviderAsync(subArgs, cancellationToken),
            "test" or "validate" => await TestProviderAsync(subArgs, cancellationToken),
            "remove" or "rm" or "delete" => await RemoveProviderAsync(subArgs, cancellationToken),
            "set-default" or "default" => await SetDefaultAsync(subArgs, cancellationToken),
            "export" => await ExportProviderAsync(subArgs, cancellationToken),
            "import" => await ImportProviderAsync(subArgs, cancellationToken),
            "help" or "-h" or "--help" => ShowHelp(),
            _ => await HandleUnknownSubcommand(subcommand)
        };
    }

    private async Task<int> AutoconfigAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a website URL");
            AnsiConsole.MarkupLine("Usage: [cyan]koware provider autoconfig <url>[/]");
            return 1;
        }

        var urlString = args[0];
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
        {
            // Try adding https://
            if (!urlString.StartsWith("http"))
            {
                urlString = "https://" + urlString;
                if (!Uri.TryCreate(urlString, UriKind.Absolute, out url))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Invalid URL: {0}", args[0]);
                    return 1;
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid URL: {0}", args[0]);
                return 1;
            }
        }

        // Parse options
        var options = new AutoconfigOptions
        {
            ProviderName = GetArgValue(args, "--name"),
            TestQuery = GetArgValue(args, "--test-query"),
            SkipValidation = args.Contains("--skip-validation"),
            DryRun = args.Contains("--dry-run"),
            ForceType = ParseProviderType(GetArgValue(args, "--type"))
        };

        AnsiConsole.MarkupLine("\n[bold cyan]Analyzing[/] {0}...\n", url.Host);

        var result = await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Autoconfig[/]", maxValue: 100);

                var progress = new Progress<AutoconfigProgress>(p =>
                {
                    task.Description = $"[cyan]{p.Phase}[/]: {p.Step}";
                    task.Value = p.Percentage;
                });

                return await _orchestrator.AnalyzeAndConfigureAsync(url, options, progress, ct);
            });

        AnsiConsole.WriteLine();

        // Display results
        if (result.IsSuccess && result.Config != null)
        {
            DisplaySuccessResult(result);
            return 0;
        }
        else
        {
            DisplayFailureResult(result);
            return 1;
        }
    }

    private void DisplaySuccessResult(AutoconfigResult result)
    {
        var config = result.Config!;

        AnsiConsole.Write(new Rule("[green][+] Provider Created Successfully[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Name", $"[cyan]{config.Name}[/]");
        table.AddRow("Slug", config.Slug);
        table.AddRow("Type", config.Type.ToString());
        table.AddRow("Base Host", config.Hosts.BaseHost);
        table.AddRow("API Base", config.Hosts.ApiBase ?? "-");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Show phases
        var phaseTable = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("[bold]Phase[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Duration[/]");

        foreach (var phase in result.Phases)
        {
            var status = phase.Succeeded ? "[green][+][/]" : "[red][x][/]";
            phaseTable.AddRow(phase.Name, status, $"{phase.Duration.TotalMilliseconds:F0}ms");
        }

        AnsiConsole.Write(phaseTable);
        AnsiConsole.WriteLine();

        // Validation results
        if (result.ValidationResult != null)
        {
            AnsiConsole.MarkupLine("[bold]Validation Results:[/]");
            foreach (var check in result.ValidationResult.Checks)
            {
                var icon = check.Passed ? "[green][+][/]" : "[red][x][/]";
                AnsiConsole.MarkupLine($"  {icon} {check.Name}: {check.Description ?? check.ErrorMessage ?? "OK"}");
            }
            AnsiConsole.WriteLine();
        }

        // Warnings
        if (result.Warnings.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Warnings:[/]");
            foreach (var warning in result.Warnings)
            {
                AnsiConsole.MarkupLine($"  [yellow][!][/] {warning}");
            }
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[dim]To use:[/] koware watch \"Naruto\" --provider {0}", config.Slug);
        AnsiConsole.MarkupLine("[dim]Set as default:[/] koware provider set-default {0}", config.Slug);
    }

    private static void DisplayFailureResult(AutoconfigResult result)
    {
        AnsiConsole.Write(new Rule("[red][x] Autoconfig Failed[/]").RuleStyle("red"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[red]Error:[/] {0}", result.ErrorMessage ?? "Unknown error");
        AnsiConsole.WriteLine();

        if (result.Phases.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]Phases:[/]");
            foreach (var phase in result.Phases)
            {
                var icon = phase.Succeeded ? "[green][+][/]" : "[red][x][/]";
                AnsiConsole.MarkupLine($"  {icon} {phase.Name}: {phase.Message}");
            }
        }
    }

    private async Task<int> ListProvidersAsync(CancellationToken ct)
    {
        var providers = await _providerStore.ListAsync(ct);

        if (providers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No providers configured.[/]");
            AnsiConsole.MarkupLine("Run [cyan]koware provider autoconfig <url>[/] to add one.");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Slug[/]")
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Host[/]")
            .AddColumn("[bold]Status[/]");

        foreach (var provider in providers)
        {
            var status = new List<string>();
            if (provider.IsBuiltIn)
                status.Add("[dim]built-in[/]");
            if (provider.IsActive)
                status.Add("[green]active[/]");

            table.AddRow(
                provider.Name,
                $"[cyan]{provider.Slug}[/]",
                provider.Type.ToString(),
                provider.BaseHost,
                string.Join(", ", status));
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private async Task<int> ShowProviderAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a provider name or slug");
            return 1;
        }

        var config = await _providerStore.GetAsync(args[0], ct);
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Provider '{0}' not found", args[0]);
            return 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Name", config.Name);
        table.AddRow("Slug", config.Slug);
        table.AddRow("Type", config.Type.ToString());
        table.AddRow("Version", config.Version);
        table.AddRow("Generated", config.GeneratedAt.ToString("g"));
        table.AddRow("Last Validated", config.LastValidatedAt?.ToString("g") ?? "-");
        table.AddRow("", "");
        table.AddRow("[bold]Hosts[/]", "");
        table.AddRow("  Base Host", config.Hosts.BaseHost);
        table.AddRow("  API Base", config.Hosts.ApiBase ?? "-");
        table.AddRow("  Referer", config.Hosts.Referer);
        table.AddRow("", "");
        table.AddRow("[bold]Search[/]", "");
        table.AddRow("  Method", config.Search.Method.ToString());
        table.AddRow("  Endpoint", config.Search.Endpoint);

        if (!string.IsNullOrEmpty(config.Notes))
        {
            table.AddRow("", "");
            table.AddRow("[bold]Notes[/]", config.Notes);
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private async Task<int> TestProviderAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a provider name or slug");
            return 1;
        }

        var config = await _providerStore.GetAsync(args[0], ct);
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Provider '{0}' not found", args[0]);
            return 1;
        }

        AnsiConsole.MarkupLine("Testing provider [cyan]{0}[/]...\n", config.Name);

        // Would need to inject validator here - simplified for now
        AnsiConsole.MarkupLine("[yellow]Provider test not yet implemented in CLI.[/]");
        AnsiConsole.MarkupLine("Use [cyan]koware provider autoconfig <url>[/] to re-analyze and validate.");

        return 0;
    }

    private async Task<int> RemoveProviderAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a provider name or slug");
            return 1;
        }

        var slug = args[0];
        var config = await _providerStore.GetAsync(slug, ct);

        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Provider '{0}' not found", slug);
            return 1;
        }

        if (config.IsBuiltIn)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Cannot remove built-in provider '{0}'", config.Name);
            return 1;
        }

        if (!args.Contains("--force") && !args.Contains("-f"))
        {
            var confirm = AnsiConsole.Confirm($"Remove provider [cyan]{config.Name}[/]?", defaultValue: false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                return 0;
            }
        }

        var deleted = await _providerStore.DeleteAsync(slug, ct);
        if (deleted)
        {
            AnsiConsole.MarkupLine("[green]✓[/] Provider '{0}' removed", config.Name);
            return 0;
        }

        AnsiConsole.MarkupLine("[red]Error:[/] Failed to remove provider");
        return 1;
    }

    private async Task<int> SetDefaultAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a provider name or slug");
            return 1;
        }

        var slug = args[0];
        var config = await _providerStore.GetAsync(slug, ct);

        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Provider '{0}' not found", slug);
            return 1;
        }

        await _providerStore.SetActiveAsync(slug, config.Type, ct);
        AnsiConsole.MarkupLine("[green]✓[/] Set [cyan]{0}[/] as default {1} provider", config.Name, config.Type);

        return 0;
    }

    private async Task<int> ExportProviderAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a provider name or slug");
            return 1;
        }

        var slug = args[0];
        var outputFile = GetArgValue(args, "--file") ?? GetArgValue(args, "-o");

        try
        {
            var json = await _providerStore.ExportAsync(slug, ct);

            if (!string.IsNullOrEmpty(outputFile))
            {
                await File.WriteAllTextAsync(outputFile, json, ct);
                AnsiConsole.MarkupLine("[green]✓[/] Exported to {0}", outputFile);
            }
            else
            {
                AnsiConsole.WriteLine(json);
            }

            return 0;
        }
        catch (Exception ex)
        {
            var detail = ErrorClassifier.SafeDetail(ex);
            var message = string.IsNullOrWhiteSpace(detail) ? "Failed to export provider" : $"Failed to export provider: {detail}";
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", message);
            return 1;
        }
    }

    private async Task<int> ImportProviderAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Please provide a file path or URL");
            return 1;
        }

        var source = args[0];
        string json;

        try
        {
            if (source.StartsWith("http"))
            {
                using var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
                json = await client.GetStringAsync(source, ct);
            }
            else
            {
                json = await File.ReadAllTextAsync(source, ct);
            }

            var config = await _providerStore.ImportAsync(json, ct);
            await _providerStore.SaveAsync(config, ct);

            AnsiConsole.MarkupLine("[green]✓[/] Imported provider [cyan]{0}[/]", config.Name);
            return 0;
        }
        catch (Exception ex)
        {
            var detail = ErrorClassifier.SafeDetail(ex);
            var message = string.IsNullOrWhiteSpace(detail) ? "Failed to import provider" : $"Failed to import provider: {detail}";
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", message);
            return 1;
        }
    }

    private static int ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]Provider Management[/]\n");
        AnsiConsole.MarkupLine("Usage: [cyan]koware provider <subcommand> [options][/]\n");

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("")
            .AddColumn("");

        table.AddRow("[cyan]autoconfig <url>[/]", "Analyze a website and create a provider");
        table.AddRow("[cyan]list[/]", "List all configured providers");
        table.AddRow("[cyan]show <name>[/]", "Show provider details");
        table.AddRow("[cyan]test <name>[/]", "Test a provider configuration");
        table.AddRow("[cyan]remove <name>[/]", "Remove a custom provider");
        table.AddRow("[cyan]set-default <name>[/]", "Set provider as default");
        table.AddRow("[cyan]export <name>[/]", "Export provider to JSON");
        table.AddRow("[cyan]import <file|url>[/]", "Import provider from JSON");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[bold]Autoconfig Options:[/]");
        AnsiConsole.MarkupLine("  --name <name>         Custom provider name");
        AnsiConsole.MarkupLine("  --type <anime|manga>  Force content type");
        AnsiConsole.MarkupLine("  --test-query <query>  Custom validation query");
        AnsiConsole.MarkupLine("  --skip-validation     Skip validation step");
        AnsiConsole.MarkupLine("  --dry-run             Don't save configuration");

        return 0;
    }

    private static Task<int> HandleUnknownSubcommand(string subcommand)
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Unknown subcommand '{0}'", subcommand);
        AnsiConsole.MarkupLine("Run [cyan]koware provider help[/] for usage");
        return Task.FromResult(1);
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static ProviderType? ParseProviderType(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value.ToLowerInvariant() switch
        {
            "anime" => ProviderType.Anime,
            "manga" => ProviderType.Manga,
            "both" => ProviderType.Both,
            _ => null
        };
    }
}
