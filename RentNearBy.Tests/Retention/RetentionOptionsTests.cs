using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace RentNearBy.Tests.Retention;

public class RetentionOptionsTests
{
    private static RetentionOptions Bind(Dictionary<string, string?> values)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.Configure<RetentionOptions>(cfg.GetSection(RetentionOptions.Section));
        services.PostConfigure<RetentionOptions>(o => o.Normalize());
        return services.BuildServiceProvider().GetRequiredService<IOptions<RetentionOptions>>().Value;
    }

    [Fact]
    public void Empty_section_gives_the_defaults()
    {
        var o = Bind(new());
        Assert.True(o.Enabled);
        Assert.Equal(1000, o.BatchSize);
        Assert.Equal(20, o.MaxPercent);
        Assert.Equal("01:00", o.WindowStartIst);
        Assert.Equal("04:30", o.WindowEndIst);
    }

    [Fact]
    public void Missing_rule_uses_code_defaults()
    {
        var s = Bind(new()).Resolve("Anything", 90, true);
        Assert.Equal(new RetentionRuleSettings(true, 90, false), s);
        Assert.False(Bind(new()).Resolve("BannerDismissals", 90, false).Enabled);
    }

    [Fact]
    public void Rule_keys_are_case_insensitive()
    {
        var o = Bind(new()
        {
            ["Retention:Rules:chat:Enabled"] = "false",
            ["Retention:Rules:CHAT:Days"] = "45",
        });
        var s = o.Resolve("Chat", 90, true);
        Assert.False(s.Enabled);
        Assert.Equal(45, s.Days);
    }

    [Fact]
    public void Config_overrides_the_default_and_global_off_wins()
    {
        var o = Bind(new() { ["Retention:Rules:Chat:Enabled"] = "true", ["Retention:Rules:Chat:Days"] = "30", ["Retention:Enabled"] = "false" });
        Assert.False(o.Resolve("Chat", 90, true).Enabled);
        var on = Bind(new() { ["Retention:Rules:BannerDismissals:Enabled"] = "true" });
        Assert.True(on.Resolve("BannerDismissals", 90, false).Enabled);
    }

    [Fact]
    public void Days_clamp_to_one_and_percent_to_one_to_fifty()
    {
        var o = Bind(new()
        {
            ["Retention:MaxPercent"] = "99",
            ["Retention:Rules:Chat:Days"] = "0",
            ["Retention:Rules:Sessions:Days"] = "-5",
        });
        Assert.Equal(50, o.MaxPercent);
        Assert.Equal(1, o.Resolve("Chat", 90, true).Days);
        Assert.Equal(1, o.Resolve("Sessions", 90, true).Days);
        Assert.Equal(1, Bind(new() { ["Retention:MaxPercent"] = "0" }).MaxPercent);
    }

    [Fact]
    public void Batch_sizes_stay_consistent()
    {
        var o = Bind(new() { ["Retention:BatchSize"] = "50", ["Retention:MinBatchSize"] = "500" });
        Assert.Equal(50, o.BatchSize);
        Assert.Equal(50, o.MinBatchSize);
    }

    [Fact]
    public void Checked_in_appsettings_defaults_match_the_contract()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RentNearBy.Api", "appsettings.json"));
        if (!File.Exists(path)) return; // appsettings.json is gitignored: nothing to check on a clean clone.
        var o = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection(RetentionOptions.Section).Get<RetentionOptions>()!;
        Assert.Equal(16, o.Rules.Count);
        Assert.False(o.Resolve("BannerDismissals", 90, true).Enabled);
        Assert.Equal(3, o.Resolve("OutboxMessages", 90, true).Days);
        Assert.Equal(180, o.Resolve("TripEnquiryOrphans", 90, true).Days);
    }
}
