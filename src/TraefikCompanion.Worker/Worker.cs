using System.Net.Http.Json;
using StackExchange.Redis;
using Traefik.Contracts.TcpConfiguration;
using TraefikCompanion.Worker.Extensions;
using Server = TraefikCompanion.Worker.Configuration.Server;

namespace TraefikCompanion.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConnectionMultiplexer _redis;
    
    private readonly Server[] _servers;
    private readonly Dictionary<string, HttpClient> _clients = new();
    private readonly int _runEvery;
    
    private readonly IDictionary<string,string> _oldEntries = new Dictionary<string, string>();

    public Worker(ILogger<Worker> logger, IHttpClientFactory httpClientFactory, IConnectionMultiplexer redis, IConfiguration configuration)
    {
        _logger = logger;
        _redis = redis;
        _runEvery = configuration.GetValue("RUN_EVERY", 60);
        _servers = configuration.GetSection("servers").Get<Server[]>()!;

        foreach (var server in _servers)
        {
            _clients.Add(server.Name, httpClientFactory.CreateClient(server.Name));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker running at: {Time}", DateTimeOffset.Now);
            
            var entries = new Dictionary<string, string>();
            foreach (var server in _servers)
            {
                entries.Merge(await GenerateRedisEntries(server, stoppingToken));
            }
            
            var entriesToRemove = _oldEntries.Keys.Except(entries.Keys);
            
            var db = _redis.GetDatabase();

            foreach (var key in entriesToRemove)
            {
                await db.KeyDeleteAsync(key);
            }
            
            foreach (var (key, value) in entries)
            {
                await db.StringUpdateIfChanged(key, value);
            }

            _oldEntries.Clear();
            _oldEntries.Merge(entries);
            
            
            await Task.Delay(_runEvery, stoppingToken);
        }
    }

    private async Task<IDictionary<string,string>> GenerateRedisEntries(Server server, CancellationToken token)
    {
        var entries = new Dictionary<string, string>();
        
        _logger.LogInformation("Attempting to connect to {Server}", server.Name);
        var response = await _clients[server.Name].GetAsync("api/http/routers", token);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not connect to {Server}", server.Name);
            return entries;
        }
        
        _logger.LogInformation("Successfully connected to {Server}", server.Name);
        
        _logger.LogInformation("Retrieving http routers from {Server}", server.Name);
        var httpRouters = await response.Content.ReadFromJsonAsync<TcpRouter[]>(cancellationToken: token);

        if (httpRouters is null)
        {
            _logger.LogError("Could not get tcp routers from {Server}", server.Name);
            return entries;
        }
        
        _logger.LogInformation("Successfully retrieved {Number} http routers from {Server}",httpRouters.Length, server.Name);
        
        entries.Add($"traefik/http/services/{server.Name}/loadbalancer/servers/0/url",server.DestinationAddress.ToString());
        
        foreach (var router in httpRouters)
        {
            var name = router.Service;

            if (name.Contains('@'))
                name = $"{name.Split('@')[0]}_{server.Name}";

            entries.Add($"traefik/http/routers/{name}/entrypoints/0", "web");
            entries.Add($"traefik/http/routers/{name}/entrypoints/1", "web-secure");
            entries.Add($"traefik/http/routers/{name}/rule", router.Rule);
            entries.Add($"traefik/http/routers/{name}/service", server.Name);
        }

        return entries;
    }
}