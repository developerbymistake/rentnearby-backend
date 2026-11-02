using System.Collections.Concurrent;
using System.Reflection;
using StackExchange.Redis;

namespace RentNearBy.Tests.Support;

// In-memory Redis for the string get/set/delete calls the caches make; anything else throws.
public sealed class FakeRedis
{
    public ConcurrentDictionary<string, (string Value, TimeSpan? Ttl)> Store { get; } = new();
    public bool Fail { get; set; }
    public IConnectionMultiplexer Connection { get; }
    internal IDatabase Database { get; }

    public FakeRedis()
    {
        var db = DispatchProxy.Create<IDatabase, RedisProxy>();
        ((RedisProxy)(object)db).Owner = this;
        Database = db;
        var mux = DispatchProxy.Create<IConnectionMultiplexer, RedisProxy>();
        ((RedisProxy)(object)mux).Owner = this;
        Connection = mux;
    }

    public bool Has(string key) => Store.ContainsKey(key);
}

public class RedisProxy : DispatchProxy
{
    internal FakeRedis Owner = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        if (name == "GetDatabase") return Owner.Database;
        if (Owner.Fail && name.EndsWith("Async"))
            return FailedTask(method.ReturnType);

        switch (name)
        {
            case "StringGetAsync" when args![0] is RedisKey key:
                return Task.FromResult(Owner.Store.TryGetValue(key.ToString(), out var hit) ? (RedisValue)hit.Value : RedisValue.Null);

            case "StringSetAsync" when args![0] is RedisKey key:
            {
                var value = ((RedisValue)args[1]!).ToString();
                var ttl = args.Skip(2).OfType<TimeSpan>().Cast<TimeSpan?>().FirstOrDefault();
                var when = args.Skip(2).OfType<When>().FirstOrDefault();
                if (when == When.NotExists)
                    return Task.FromResult(Owner.Store.TryAdd(key.ToString(), (value, ttl)));
                Owner.Store[key.ToString()] = (value, ttl);
                return Task.FromResult(true);
            }

            case "KeyDeleteAsync" when args![0] is RedisKey key:
                return Task.FromResult(Owner.Store.TryRemove(key.ToString(), out _));

            case "KeyDeleteAsync" when args![0] is RedisKey[] keys:
                return Task.FromResult((long)keys.Count(k => Owner.Store.TryRemove(k.ToString(), out _)));
        }

        throw new NotSupportedException($"FakeRedis does not support {name}");
    }

    private static object FailedTask(Type returnType)
    {
        var error = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "fake redis down");
        var result = returnType.GetGenericArguments()[0];
        var source = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(result))!;
        source.GetType().GetMethod("SetException", [typeof(Exception)])!.Invoke(source, [error]);
        return source.GetType().GetProperty("Task")!.GetValue(source)!;
    }
}
