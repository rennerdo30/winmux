using System.Reflection;
using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

public sealed class TerminalEmulationEngineTests
{
    [Fact]
    public void WriteExposesGridCursorTitleColorsAndAttributes()
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        var titleChanges = new List<string>();
        engine.TitleChanged += titleChanges.Add;

        engine.Write(Encoding.UTF8.GetBytes("\e]0;WinMux test\a\e[1;38;2;1;2;3mA"));

        var row = new TerminalCell[engine.Columns];
        var info = engine.CopyRow(engine.ScrollbackCount, row);

        Assert.Equal(20, info.Length);
        Assert.False(info.Wrapped);
        Assert.Equal('A', row[0].Character);
        Assert.True((row[0].Attributes & TerminalCellAttributes.Bold) != 0);
        Assert.Equal(TerminalColor.FromRgb(1, 2, 3), row[0].Foreground);
        Assert.Equal(new TerminalCursor(0, 1, true, TerminalCursorStyle.SteadyBlock, false), engine.Cursor);
        Assert.Equal("WinMux test", engine.Title);
        Assert.Equal(["WinMux test"], titleChanges);
        Assert.True(engine.Version > 0);
    }

    [Fact]
    public void DeviceAttributesQueryRaisesResponseBytes()
    {
        var engine = new TerminalEmulationEngine(20, 4);
        byte[]? response = null;
        engine.Response += bytes => response = bytes.ToArray();

        engine.Write("\e[c"u8);

        Assert.NotNull(response);
        Assert.StartsWith("\e[?", Encoding.ASCII.GetString(response));
        Assert.EndsWith("c", Encoding.ASCII.GetString(response));
    }

    [Fact]
    public void ResizeReflowsAndPreservesText()
    {
        var engine = new TerminalEmulationEngine(40, 5, 10);
        engine.Write(Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog"));

        engine.Resize(12, 5, reflow: true);

        Assert.Equal(12, engine.Columns);
        Assert.Equal(5, engine.Rows);
        var flattened = ReadGrid(engine)
            .Replace(" ", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
        Assert.Contains("quickbrownfox", flattened);
    }

    [Fact]
    public void WideGlyphAndHyperlinkStayBehindOwnedCellTypes()
    {
        var engine = new TerminalEmulationEngine(20, 3);
        engine.Write(Encoding.UTF8.GetBytes("你\e]8;;https://example.com\aL\e]8;;\a"));
        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);

        Assert.True(row[0].IsWideLeading);
        Assert.True(row[1].IsWideTrailing);
        Assert.NotEqual((ushort)0, row[2].HyperlinkId);
        Assert.Equal("https://example.com", engine.GetHyperlink(row[2].HyperlinkId));
    }

    [Fact]
    public void PublicApiDoesNotExposeDependencyTypes()
    {
        var assembly = typeof(ITerminalEngine).Assembly;
        var exposedTypes = assembly.GetExportedTypes()
            .SelectMany(type => GetSignatureTypes(type).Append(type))
            .SelectMany(FlattenType)
            .Where(type => type.Namespace?.StartsWith("Terminal.Emulation", StringComparison.Ordinal) == true)
            .Distinct()
            .ToArray();

        Assert.Empty(exposedTypes);
    }

    [Fact]
    public void CopyRowRejectsUndersizedDestination()
    {
        var engine = new TerminalEmulationEngine(20, 3);

        var error = Assert.Throws<ArgumentException>(() => engine.CopyRow(0, new TerminalCell[19]));

        Assert.Equal("destination", error.ParamName);
    }

    private static string ReadGrid(ITerminalEngine engine)
    {
        var builder = new StringBuilder();
        var row = new TerminalCell[engine.Columns];

        for (var rowIndex = 0; rowIndex < engine.TotalRows; rowIndex++)
        {
            var info = engine.CopyRow(rowIndex, row);
            for (var column = 0; column < info.Length; column++)
            {
                row[column].AppendGlyph(builder);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static IEnumerable<Type> GetSignatureTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                                   BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(flags))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(flags))
        {
            yield return property.PropertyType;
            foreach (var parameter in property.GetIndexParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var eventInfo in type.GetEvents(flags))
        {
            if (eventInfo.EventHandlerType is not null)
            {
                yield return eventInfo.EventHandlerType;
            }
        }

        foreach (var method in type.GetMethods(flags))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nested in FlattenType(elementType))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in FlattenType(argument))
            {
                yield return nested;
            }
        }
    }
}
