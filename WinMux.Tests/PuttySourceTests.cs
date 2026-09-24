using WinMux.Connections;

namespace WinMux.Tests;

/// <summary>
/// PuTTY's saved sessions, read from an invented registry rather than from whatever is on the
/// machine — which is the whole reason <see cref="IRegistryStore"/> exists.
/// </summary>
public class PuttySourceTests
{
    private const string Sessions = @"Software\SimonTatham\PuTTY\Sessions";

    private static FakeRegistry WithSessions()
    {
        var registry = new FakeRegistry();

        registry.Set($@"{Sessions}\Default%20Settings", "HostName", "");
        registry.Set($@"{Sessions}\Default%20Settings", "Protocol", "ssh");

        registry.Set($@"{Sessions}\web%2001", "HostName", "files.example.com");
        registry.Set($@"{Sessions}\web%2001", "PortNumber", "2222");
        registry.Set($@"{Sessions}\web%2001", "UserName", "deploy");
        registry.Set($@"{Sessions}\web%2001", "Protocol", "ssh");
        registry.Set($@"{Sessions}\web%2001", "PublicKeyFile", @"C:\keys\deploy.ppk");
        registry.Set($@"{Sessions}\web%2001", "ProxyHost", "jump.example.com");
        registry.Set($@"{Sessions}\web%2001", "TerminalType", "xterm");

        registry.Set($@"{Sessions}\switch", "HostName", "COM3");
        registry.Set($@"{Sessions}\switch", "Protocol", "serial");

        return registry;
    }

    [Fact]
    public void Sessions_are_read_with_their_names_unescaped()
    {
        // A session called "web 01" is the key "web%2001", and showing the key would put PuTTY's
        // escaping in front of the user.
        var root = new PuttySource(WithSessions()).Read();

        Assert.Equal(["switch", "web 01"], root.Entries().Select(entry => entry.Name));
    }

    [Fact]
    public void Puttys_own_defaults_are_not_a_host_you_can_open()
    {
        var root = new PuttySource(WithSessions()).Read();

        Assert.DoesNotContain(root.Entries(), entry => entry.Name.Contains("Default", StringComparison.Ordinal));
    }

    [Fact]
    public void A_session_keeps_its_settings()
    {
        var root = new PuttySource(WithSessions()).Read();

        var web = root.Entries().First(entry => entry.Name == "web 01");

        Assert.Equal("files.example.com", ConnectionResolver.Host(web).Value);
        Assert.Equal(2222, ConnectionResolver.Port(web).Value);
        Assert.Equal("deploy", ConnectionResolver.User(web).Value);
        Assert.Equal(@"C:\keys\deploy.ppk", ConnectionResolver.Identity(web).Value);
        Assert.Equal("jump.example.com", ConnectionResolver.Gateway(web).Value);
        Assert.Equal(ConnectionProtocol.Ssh, ConnectionResolver.Protocol(web).Value);
    }

    [Fact]
    public void A_serial_session_is_not_called_ssh()
    {
        var root = new PuttySource(WithSessions()).Read();

        Assert.Equal(ConnectionProtocol.Serial,
            ConnectionResolver.Protocol(root.Entries().First(entry => entry.Name == "switch")).Value);
    }

    [Fact]
    public void A_port_stored_as_a_dword_is_read_either_way()
    {
        var registry = WithSessions();
        registry.Set($@"{Sessions}\web%2001", "PortNumber", "0x1a0b");

        var web = new PuttySource(registry).Read().Entries().First(entry => entry.Name == "web 01");

        Assert.Equal(0x1a0b, ConnectionResolver.Port(web).Value);
    }

    [Fact]
    public void Putty_is_flat_and_does_not_pretend_otherwise()
    {
        // PuTTY has no folders. A source for a format without a hierarchy must not invent one.
        var root = new PuttySource(WithSessions()).Read();

        Assert.Single(root.Folders());
        Assert.All(root.Children, child => Assert.IsType<ConnectionEntry>(child));
    }

    [Fact]
    public void There_are_no_passwords_to_find()
    {
        // Not an omission: PuTTY has never stored one, which is the right answer.
        var source = new PuttySource(WithSessions());

        Assert.Empty(source.ReadCredentials(source.Read()));
    }

    [Fact]
    public void An_edited_host_is_written_back()
    {
        var registry = WithSessions();
        var source = new PuttySource(registry);
        var root = source.Read();
        var web = root.Entries().First(entry => entry.Name == "web 01");
        web.Settings = web.Settings with { Host = Inherited<string>.Of("files2.example.com") };

        source.Write(root);

        Assert.Equal("files2.example.com", registry.ReadValue($@"{Sessions}\web%2001", "HostName"));
        Assert.Equal("xterm", registry.ReadValue($@"{Sessions}\web%2001", "TerminalType"));
    }

    [Fact]
    public void A_renamed_session_is_one_putty_can_still_open()
    {
        var registry = WithSessions();
        var source = new PuttySource(registry);
        var root = source.Read();
        root.Entries().First(entry => entry.Name == "web 01").Name = "web 02";

        source.Write(root);

        Assert.Contains("web%2002", registry.SubKeyNames(Sessions));
        Assert.DoesNotContain("web%2001", registry.SubKeyNames(Sessions));
        Assert.Equal("files.example.com", registry.ReadValue($@"{Sessions}\web%2002", "HostName"));
    }

    [Fact]
    public void Nothing_there_says_so_rather_than_reading_as_empty()
    {
        var source = new PuttySource(new FakeRegistry());

        Assert.False(source.Exists);
        Assert.Throws<ConnectionSourceException>(() => source.Read());
    }

    [Theory]
    [InlineData("web 01", "web%2001")]
    [InlineData("a/b", "a%2Fb")]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100%25")]
    public void Names_survive_both_ways(string name, string key)
    {
        Assert.Equal(key, PuttySource.Escape(name));
        Assert.Equal(name, PuttySource.Unescape(key));
    }

    /// <summary>A registry in a dictionary. Paths are keys; values hang off them.</summary>
    public sealed class FakeRegistry : IRegistryStore
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys =
            new(StringComparer.OrdinalIgnoreCase);

        public void Set(string path, string name, string value)
        {
            if (!_keys.TryGetValue(path, out var values))
            {
                _keys[path] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            values[name] = value;
        }

        public bool KeyExists(string path) =>
            _keys.ContainsKey(path) ||
            _keys.Keys.Any(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<string> SubKeyNames(string path) =>
        [
            .. _keys.Keys
                .Where(key => key.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(key => key[(path.Length + 1)..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];

        public string? ReadValue(string path, string name) =>
            _keys.TryGetValue(path, out var values) && values.TryGetValue(name, out var value) ? value : null;

        public void WriteValue(string path, string name, string value) => Set(path, name, value);

        public bool RenameSubKey(string path, string from, string to)
        {
            var source = $@"{path}\{from}";
            if (!_keys.TryGetValue(source, out var values)) return false;

            _keys.Remove(source);
            _keys[$@"{path}\{to}"] = values;
            return true;
        }
    }
}
