using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ThunderstoreStats_DiscordBot;

class Program
{
    public static DateTime LastRan = DateTime.MinValue;
    public static List<PackageInfo>? AllPackages = [];
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactions;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _services;

    static Task Main(string[] args) => new Program().MainAsync();

    public Program()
    {
        DiscordSocketConfig socketCfg = new()
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false
        };

        _client = new DiscordSocketClient(socketCfg);
        _config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: false).Build();

        _services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>()))
            .AddSingleton<ThunderstoreAPI>()
            .AddSingleton<Chunking>()
            .AddSingleton(new ThunderstoreCache(TimeSpan.FromHours(1)))
            .BuildServiceProvider();

        _interactions = _services.GetRequiredService<InteractionService>();

        _services.GetRequiredService<ThunderstoreCache>().Start();
    }

    public async Task MainAsync()
    {
        _client.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };
        _interactions.Log += msg =>
        {
            Console.WriteLine(msg.ToString());
            return Task.CompletedTask;
        };

        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += HandleInteractionAsync;

        string? token = _config["BotToken"];
        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        // Add modules containing slash commands
        await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        await Task.Delay(-1);
    }

    private async Task OnReadyAsync()
    {
        Console.WriteLine($"{_client.CurrentUser} is connected!");

        // Register commands: prefer testing in a guild (fast) then switch to global.
        // Guild registration (fast): set your guild id in appsettings.json as "DevGuildId"
        if (ulong.TryParse(_config["DevGuildId"], out ulong guildId) && guildId != 0)
        {
            await _interactions.RegisterCommandsToGuildAsync(guildId, true);
            Console.WriteLine($"Registered slash commands to guild {guildId}");
        }
        else
        {
            await _interactions.RegisterCommandsGloballyAsync(true);
            Console.WriteLine("Registered slash commands globally (may take up to an hour to appear).");
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction raw)
    {
        try
        {
            SocketInteractionContext ctx = new(_client, raw);
            await _interactions.ExecuteCommandAsync(ctx, _services);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            if (raw.Type == InteractionType.ApplicationCommand)
            {
                try
                {
                    await raw.GetOriginalResponseAsync();
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}