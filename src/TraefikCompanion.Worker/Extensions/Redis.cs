using StackExchange.Redis;

namespace TraefikCompanion.Worker.Extensions;

internal static class Redis
{
    public static async Task StringUpdateIfChanged(this IDatabase database, string key, string value)
    {
        var currentValue =  await database.StringGetAsync(key);

        if (!currentValue.HasValue || currentValue != value)
        {
            await database.StringSetAsync(key, value);
        }
    }
}