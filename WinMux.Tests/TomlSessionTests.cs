using Tomlyn;
using Tomlyn.Model;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public class TomlSessionTests
{
    private static Pane P(string t) => Pane.Terminal(t);

    private static LayoutTree BusyTree(out Pane focused)
    {
        var a = P("shell");
        var tree = new LayoutTree(a) { Bounds = new Rect(10, 20, 1280, 720) };
        var b = P("editor");
        var c = P("logs");
        var d = P("notes");

        tree.Split(a.Id, SplitDirection.Columns, b, 0.4);
        tree.Split(b.Id, SplitDirection.Rows, c, 0.3);
        tree.AddTab(c.Id, d);

        focused = c;
        tree.Focus(c.Id);
        return tree;
    }

    private static SessionSnapshot SnapshotOf(LayoutTree tree) => new()
    {
        SavedAt = new DateTimeOffset(2026, 9, 10, 12, 4, 11, TimeSpan.Zero),
        Windows = [SessionMapper.ToSnapshot(tree, "main")],
    };

    // ---------------- round trip ----------------

    [Fact]
    public void A_session_survives_a_trip_through_actual_toml_text()
    {
        var tree = BusyTree(out var focused);
        var original = SnapshotOf(tree);

        var text = SessionFile.Serialize(original);
        var restored = SessionFile.Deserialize(text);

        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.SavedAt, restored.SavedAt);
        var back = SessionMapper.FromSnapshot(restored.Windows.Single());

        Assert.Equal(tree.Bounds, back.Bounds);
        Assert.Equal(focused.Id, back.Focused);
        Assert.Equal(
            tree.Panes.Select(p => p.Id.Value).OrderBy(x => x),
            back.Panes.Select(p => p.Id.Value).OrderBy(x => x));

        // and the geometry is identical, which is the property that actually matters on restore
        var before = tree.Arrange();
        var after = back.Arrange();
        foreach (var (id, rect) in before.PaneRects) Assert.Equal(rect, after[id]);
    }

    [Fact]
    public void Serializing_is_stable_so_a_session_file_does_not_churn_in_a_diff()
    {
        var tree = BusyTree(out _);
        var first = SessionFile.Serialize(SnapshotOf(tree));
        var second = SessionFile.Serialize(SnapshotOf(tree));
        Assert.Equal(first, second);

        // and a load/save cycle reproduces the file byte for byte
        var reloaded = SessionFile.Deserialize(first);
        Assert.Equal(first, SessionFile.Serialize(reloaded));
    }

    /// <summary>
    /// The writer builds TOML by hand for formatting control, so its escaping is verified against
    /// an independent parser rather than against itself.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\renne\AppData\Local\Temp")]
    [InlineData(@"C:\path with spaces\and ünïcode")]
    [InlineData("it's a directory")]
    [InlineData("quote\" and backslash\\ together")]
    [InlineData("tab\there")]
    [InlineData("newline\nhere")]
    [InlineData("")]
    public void Awkward_strings_survive_and_the_file_stays_valid_toml(string nasty)
    {
        var pane = new Pane(PaneId.New(), PaneKind.Terminal, nasty, new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = nasty,
            Program = nasty,
            Args = [nasty, "plain"],
            Cwd = new WorkingDirectory(nasty.Length == 0 ? "x" : nasty, CwdSource.ShellReported, DateTimeOffset.UnixEpoch),
            Extras = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = nasty },
        });
        var tree = new LayoutTree(pane);
        var text = SessionFile.Serialize(SnapshotOf(tree));

        // 1. an independent parser accepts it
        var model = TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions());
        Assert.NotNull(model);

        // 2. and our reader gets the values back unchanged
        var back = SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows.Single());
        var restored = back.Panes.Single();
        Assert.Equal(nasty, restored.Title);
        Assert.Equal(nasty, restored.Restore.Program);
        Assert.Equal([nasty, "plain"], restored.Restore.Args);
        Assert.Equal(nasty, restored.Restore.Extras["k"]);
    }

    [Fact]
    public void A_long_extras_map_becomes_a_sub_table_instead_of_one_enormous_line()
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window_class"] = "CabinetWClass",
            ["window_match"] = "class",
            ["launch_delay_ms"] = "2000",
            ["note"] = "explorer.exe exits immediately; the window belongs to the running shell",
        };
        var pane = new Pane(PaneId.New(), PaneKind.ForeignApp, "Explorer",
            new RestoreDescriptor { Kind = PaneKind.ForeignApp, Extras = extras });

        var text = SessionFile.Serialize(SnapshotOf(new LayoutTree(pane)));

        Assert.Contains("[windows.panes.extras]", text, StringComparison.Ordinal);
        Assert.All(text.Split('\n'), line =>
            Assert.True(line.TrimEnd().Length <= 140, "over-long line: " + line));

        // and it still round-trips
        var back = SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows.Single()).Panes.Single();
        Assert.Equal(extras.Count, back.Restore.Extras.Count);
        foreach (var (k, v) in extras) Assert.Equal(v, back.Restore.Extras[k]);
    }

    [Fact]
    public void A_short_extras_map_stays_inline()
    {
        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "t", new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Extras = new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "pwsh" },
        });
        var text = SessionFile.Serialize(SnapshotOf(new LayoutTree(pane)));

        Assert.Contains("extras          = { profile = 'pwsh' }", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[windows.panes.extras]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_env_and_extras_can_be_long_at_once()
    {
        // Two sub-tables on one pane: the second must not swallow the first, and no scalar key
        // may be written after a sub-table header.
        var big = Enumerable.Range(0, 6).ToDictionary(i => "key_number_" + i, i => "value number " + i, StringComparer.Ordinal);
        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "t", new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Program = @"C:\tool.exe",
            EnvOverrides = new Dictionary<string, string>(big, StringComparer.OrdinalIgnoreCase),
            Extras = big,
        });

        var text = SessionFile.Serialize(SnapshotOf(new LayoutTree(pane)));
        var back = SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows.Single()).Panes.Single();

        Assert.Equal(@"C:\tool.exe", back.Restore.Program);
        Assert.Equal(6, back.Restore.EnvOverrides.Count);
        Assert.Equal(6, back.Restore.Extras.Count);
        foreach (var (k, v) in big)
        {
            Assert.Equal(v, back.Restore.EnvOverrides[k]);
            Assert.Equal(v, back.Restore.Extras[k]);
        }
    }

    [Fact]
    public void Cwd_provenance_survives_the_file()
    {
        var captured = new WorkingDirectory(@"C:\work", CwdSource.ProcessDeepest,
            new DateTimeOffset(2026, 9, 10, 8, 30, 0, TimeSpan.Zero));
        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "t",
            new RestoreDescriptor { Kind = PaneKind.Terminal, Cwd = captured });

        var text = SessionFile.Serialize(SnapshotOf(new LayoutTree(pane)));
        var back = SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows.Single()).Panes.Single();

        Assert.Equal(captured.Path, back.Restore.Cwd.Path);
        Assert.Equal(CwdSource.ProcessDeepest, back.Restore.Cwd.Source);
        Assert.Equal(captured.CapturedAt, back.Restore.Cwd.CapturedAt);
    }

    // ---------------- readability, which is why TOML was chosen ----------------

    [Fact]
    public void The_tree_is_flattened_rather_than_nested()
    {
        var text = SessionFile.Serialize(SnapshotOf(BusyTree(out _)));

        Assert.Contains("[[windows.nodes]]", text, StringComparison.Ordinal);
        Assert.Contains("[[windows.panes]]", text, StringComparison.Ordinal);

        // The whole point of the flat encoding (ADR 0006): no deep table paths.
        Assert.DoesNotContain("children.children", text, StringComparison.Ordinal);
        Assert.DoesNotContain("root.children", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_paths_are_written_as_literal_strings_not_escaped_ones()
    {
        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "t", new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Program = @"C:\Program Files\PowerShell\7\pwsh.exe",
        });
        var text = SessionFile.Serialize(SnapshotOf(new LayoutTree(pane)));

        Assert.Contains(@"'C:\Program Files\PowerShell\7\pwsh.exe'", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\\Program Files", text, StringComparison.Ordinal);
    }

    /// <summary>A file a person typed, not one we generated. If this cannot be read, "hand-editable" is a lie.</summary>
    [Fact]
    public void A_hand_written_file_loads()
    {
        const string handWritten = """
            version = 1

            [[windows]]
            title   = "main"
            root    = "root"
            focused = "11111111-1111-1111-1111-111111111111"
            bounds  = { x = 0, y = 0, width = 1920, height = 1080 }

            [[windows.nodes]]
            id        = "root"
            kind      = "split"
            direction = "columns"
            children  = ["left", "right"]
            ratios    = [0.35, 0.65]

            [[windows.nodes]]
            id   = "left"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id   = "right"
            kind = "leaf"
            pane = "22222222-2222-2222-2222-222222222222"

            [[windows.panes]]
            id      = "11111111-1111-1111-1111-111111111111"
            kind    = "terminal"
            title   = "shell"
            program = 'C:\Program Files\PowerShell\7\pwsh.exe'
            args    = ["-NoLogo"]
            cwd     = 'C:\work\winmux'
            cwd_source = "shell-reported"
            cwd_captured_at = 2026-09-10T12:00:00Z

            [[windows.panes]]
            id    = "22222222-2222-2222-2222-222222222222"
            kind  = "file-browser"
            title = "files"
            """;

        var snapshot = SessionFile.Deserialize(handWritten);
        var tree = SessionMapper.FromSnapshot(snapshot.Windows.Single());

        Assert.Equal(2, tree.Panes.Count());
        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(SplitDirection.Columns, split.Direction);
        Assert.Equal(0.35, split.Ratios[0], 6);

        var shell = tree.Panes.First(p => p.Title == "shell");
        Assert.Equal(@"C:\work\winmux", shell.Restore.Cwd.Path);
        Assert.Equal(CwdSource.ShellReported, shell.Restore.Cwd.Source);
        Assert.Equal(PaneKind.FileBrowser, tree.Panes.First(p => p.Title == "files").Kind);
    }

    // ---------------- refusing to guess ----------------

    private const string Minimal = """
        version = 1
        [[windows]]
        root = "a"
        focused = "11111111-1111-1111-1111-111111111111"
        [[windows.nodes]]
        id = "a"
        kind = "leaf"
        pane = "11111111-1111-1111-1111-111111111111"
        [[windows.panes]]
        id = "11111111-1111-1111-1111-111111111111"
        kind = "terminal"
        """;

    [Fact]
    public void The_minimal_file_is_accepted()
    {
        var tree = SessionMapper.FromSnapshot(SessionFile.Deserialize(Minimal).Windows.Single());
        Assert.Single(tree.Panes);
    }

    [Fact]
    public void Malformed_toml_is_reported_as_such()
    {
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize("version = = 1"));
        Assert.Contains("not valid TOML", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_future_version_says_upgrade_rather_than_discarding_the_layout()
    {
        var ex = Assert.Throws<SessionFormatException>(
            () => SessionFile.Deserialize(Minimal.Replace("version = 1", "version = 99", StringComparison.Ordinal)));
        Assert.Contains("Upgrade WinMux", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_zero_is_migrated_to_current_with_deterministic_cwd_provenance()
    {
        var legacy = Minimal
            .Replace("version = 1", "version = 0", StringComparison.Ordinal)
            .Replace(
                "kind = \"terminal\"",
                "kind = \"terminal\"\ncwd = 'C:\\legacy\\work'",
                StringComparison.Ordinal);

        var migrated = SessionFile.Deserialize(legacy);
        var pane = migrated.Windows.Single().Root.Pane!;

        Assert.Equal(SessionSnapshot.CurrentVersion, migrated.Version);
        Assert.Equal(@"C:\legacy\work", pane.Restore.Cwd.Path);
        Assert.Equal(CwdSource.LaunchDirectory, pane.Restore.Cwd.Source);
        Assert.Equal(DateTimeOffset.UnixEpoch, pane.Restore.Cwd.CapturedAt);

        var currentText = SessionFile.Serialize(migrated);
        Assert.Contains("version = 1", currentText, StringComparison.Ordinal);
        Assert.Contains("cwd_source      = 'launch-directory'", currentText, StringComparison.Ordinal);
        Assert.Contains("cwd_captured_at = 1970-01-01T00:00:00.000Z", currentText, StringComparison.Ordinal);

        var reloaded = SessionFile.Deserialize(currentText);
        Assert.Equal(pane.Restore.Cwd, reloaded.Windows.Single().Root.Pane!.Restore.Cwd);
    }

    [Fact]
    public void Version_one_cwd_requires_source_metadata()
    {
        var bad = Minimal.Replace(
            "kind = \"terminal\"",
            "kind = \"terminal\"\ncwd = 'C:\\work'\ncwd_captured_at = 2026-09-12T00:00:00Z",
            StringComparison.Ordinal);

        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));

        Assert.Contains("cwd_source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_one_cwd_requires_capture_time_metadata()
    {
        var bad = Minimal.Replace(
            "kind = \"terminal\"",
            "kind = \"terminal\"\ncwd = 'C:\\work'\ncwd_source = 'shell-reported'",
            StringComparison.Ordinal);

        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));

        Assert.Contains("cwd_captured_at", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dangling_node_reference_names_the_missing_node()
    {
        var bad = Minimal.Replace("root = \"a\"", "root = \"nope\"", StringComparison.Ordinal);
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_focused_pane_must_be_reachable_from_the_window_root()
    {
        const string unreachable = "22222222-2222-2222-2222-222222222222";
        const string bad = """
            version = 1
            [[windows]]
            root = "root"
            focused = "22222222-2222-2222-2222-222222222222"

            [[windows.nodes]]
            id = "root"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.panes]]
            id = "11111111-1111-1111-1111-111111111111"
            kind = "terminal"

            [[windows.panes]]
            id = "22222222-2222-2222-2222-222222222222"
            kind = "terminal"
            """;

        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));

        Assert.Contains("focused", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(unreachable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_leaf_pointing_at_a_missing_pane_is_refused()
    {
        var bad = Minimal.Replace(
            "id = \"11111111-1111-1111-1111-111111111111\"\nkind = \"terminal\"",
            "id = \"33333333-3333-3333-3333-333333333333\"\nkind = \"terminal\"",
            StringComparison.Ordinal);
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));
        Assert.Contains("no [[windows.panes]] entry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_is_detected_instead_of_hanging()
    {
        const string cyclic = """
            version = 1
            [[windows]]
            root = "a"
            focused = "11111111-1111-1111-1111-111111111111"
            [[windows.nodes]]
            id = "a"
            kind = "split"
            direction = "rows"
            children = ["b", "a"]
            ratios = [0.5, 0.5]
            [[windows.nodes]]
            id = "b"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"
            [[windows.panes]]
            id = "11111111-1111-1111-1111-111111111111"
            kind = "terminal"
            """;
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(cyclic));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_noncanonical_container_is_rejected_during_file_load()
    {
        const string oneChildSplit = """
            version = 1
            [[windows]]
            root = "split"
            focused = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id = "split"
            kind = "split"
            direction = "columns"
            children = ["leaf"]
            ratios = [1.0]

            [[windows.nodes]]
            id = "leaf"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.panes]]
            id = "11111111-1111-1111-1111-111111111111"
            kind = "terminal"
            """;

        var error = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(oneChildSplit));

        Assert.Contains("at least 2 children", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreachable_node_is_refused_rather_than_silently_dropping_a_pane()
    {
        // Written out in full rather than appended to Minimal: re-opening [[windows.nodes]] after
        // [[windows.panes]] does not extend the earlier array, so a concatenated fixture quietly
        // tests something else entirely.
        const string orphaned = """
            version = 1
            [[windows]]
            root = "a"
            focused = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id = "a"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id = "orphan"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.panes]]
            id = "11111111-1111-1111-1111-111111111111"
            kind = "terminal"
            """;
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(orphaned));
        Assert.Contains("reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_enum_value_lists_what_was_expected()
    {
        var bad = Minimal.Replace("kind = \"terminal\"", "kind = \"hologram\"", StringComparison.Ordinal);
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(bad));
        Assert.Contains("hologram", ex.Message, StringComparison.Ordinal);
        Assert.Contains("terminal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicate_node_id_is_refused()
    {
        const string duplicated = """
            version = 1
            [[windows]]
            root = "a"
            focused = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id = "a"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.nodes]]
            id = "a"
            kind = "leaf"
            pane = "11111111-1111-1111-1111-111111111111"

            [[windows.panes]]
            id = "11111111-1111-1111-1111-111111111111"
            kind = "terminal"
            """;
        var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Deserialize(duplicated));
        Assert.Contains("Duplicate node id", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- on disk ----------------

    [Fact]
    public void Save_and_load_round_trip_through_a_real_file()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, SessionFile.DefaultFileName);
            var snapshot = SnapshotOf(BusyTree(out var focused));

            SessionFile.Save(path, snapshot);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(dir, $".{SessionFile.DefaultFileName}.*.tmp"));

            var tree = SessionMapper.FromSnapshot(SessionFile.Load(path).Windows.Single());
            Assert.Equal(focused.Id, tree.Focused);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Saving_over_an_existing_session_replaces_it_intact()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, SessionFile.DefaultFileName);
            SessionFile.Save(path, SnapshotOf(BusyTree(out _)));

            var second = new LayoutTree(P("only")) { Bounds = new Rect(0, 0, 100, 100) };
            SessionFile.Save(path, SnapshotOf(second));

            var tree = SessionMapper.FromSnapshot(SessionFile.Load(path).Windows.Single());
            Assert.Single(tree.Panes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_corrupt_file_is_preserved_and_copied_aside_rather_than_replaced()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, SessionFile.DefaultFileName);
            const string garbage = "version = 1\nthis is not toml at all [[[";
            File.WriteAllText(path, garbage);

            var ex = Assert.Throws<SessionFormatException>(() => SessionFile.Load(path));

            Assert.Contains("copy saved to", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(garbage, File.ReadAllText(path));       // original untouched

            var quarantined = Directory.GetFiles(dir, "*.corrupt-*");
            Assert.Single(quarantined);
            Assert.Equal(garbage, File.ReadAllText(quarantined[0]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadIfExists_returns_null_for_a_first_run()
    {
        var dir = NewTempDir();
        try { Assert.Null(SessionFile.LoadIfExists(Path.Combine(dir, "nothing.toml"))); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winmux-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
