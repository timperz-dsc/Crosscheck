namespace Crosscheck.Bridge.Tests;

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("wss://crosscheck.up.railway.app/ws", "wss://crosscheck.up.railway.app/ws")]
    [InlineData("wss://crosscheck.up.railway.app", "wss://crosscheck.up.railway.app/ws")]
    [InlineData("https://crosscheck.up.railway.app/", "wss://crosscheck.up.railway.app/ws")]
    [InlineData("crosscheck.up.railway.app", "wss://crosscheck.up.railway.app/ws")]
    [InlineData("  crosscheck.up.railway.app  ", "wss://crosscheck.up.railway.app/ws")]
    [InlineData("localhost:8787", "ws://localhost:8787/ws")]
    [InlineData("ws://127.0.0.1:8787/ws", "ws://127.0.0.1:8787/ws")]
    [InlineData("http://192.168.1.20:8787", "ws://192.168.1.20:8787/ws")]
    public void Normalizes_what_a_pilot_types(string input, string expected)
    {
        Assert.True(ServerAddress.TryNormalize(input, out var uri, out _));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://example.com")]
    [InlineData("wss://")]
    public void Refuses_what_is_not_an_address(string? input)
    {
        Assert.False(ServerAddress.TryNormalize(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Default_is_the_local_development_server()
    {
        Assert.True(ServerAddress.TryNormalize(ServerAddress.Default, out var uri, out _));
        Assert.Equal("ws://127.0.0.1:8787/ws", uri.ToString());
    }
}

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _dir = Repo.TempDir("crosscheck-settings");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Saves_and_loads_the_server()
    {
        var path = Path.Combine(_dir, "sub", "settings.json");
        new UserSettings("wss://x.example/ws").Save(path);
        Assert.Equal("wss://x.example/ws", UserSettings.Load(path).Server);
    }

    [Fact]
    public void Missing_or_broken_file_means_defaults()
    {
        Assert.Null(UserSettings.Load(Path.Combine(_dir, "nope.json")).Server);
        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.Null(UserSettings.Load(broken).Server);
    }
}
