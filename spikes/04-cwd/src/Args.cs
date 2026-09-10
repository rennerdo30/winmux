namespace CwdSpike;

internal sealed class Args
{
    private readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = argv[i][2..];
            var val = (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)) ? argv[++i] : "true";
            _v[key] = val;
        }
    }

    public string Str(string k, string def) => _v.TryGetValue(k, out var v) ? v : def;
    public int Int(string k, int def) => _v.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : def;
    public bool Flag(string k) => _v.ContainsKey(k);
}
