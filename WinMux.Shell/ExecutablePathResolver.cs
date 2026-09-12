using WinMux.Core.Model;

namespace WinMux.Shell;

/// <summary>The result of resolving a program name without hiding why resolution failed.</summary>
public sealed record ExecutablePathResolution(string Program, string? AbsolutePath, string? Error)
{
    public bool Succeeded => AbsolutePath is not null;
}

/// <summary>
/// Resolves executable names before they enter a restore descriptor, so a later restore does not
/// depend on a changed PATH. The same operation can normalize descriptors loaded from older files.
/// </summary>
public static class ExecutablePathResolver
{
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    public static ExecutablePathResolution Resolve(
        string program,
        string? path = null,
        string? pathExtensions = null)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return Failure(program ?? string.Empty, "The executable name is empty.");
        }

        program = program.Trim();
        try
        {
            if (Path.IsPathFullyQualified(program))
            {
                return File.Exists(program)
                    ? Success(program, program)
                    : Failure(program, $"Executable '{program}' does not exist.");
            }

            if (ContainsDirectorySeparator(program))
            {
                var absoluteCandidate = Path.GetFullPath(program);
                return File.Exists(absoluteCandidate)
                    ? Success(program, absoluteCandidate)
                    : Failure(program, $"Executable '{program}' does not exist at '{absoluteCandidate}'.");
            }

            var extensions = CandidateExtensions(program, pathExtensions).ToArray();
            var searchPath = path ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in SplitSearchPath(searchPath))
            {
                foreach (var extension in extensions)
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory, program + extension));
                    if (File.Exists(candidate))
                    {
                        return Success(program, candidate);
                    }
                }
            }

            return Failure(
                program,
                $"Could not resolve executable '{program}' as an existing path or through PATH/PATHEXT.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(program, $"Could not resolve executable '{program}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns a terminal descriptor whose program is absolute when resolution succeeds. On
    /// failure the original descriptor is retained and the caller receives a surfaceable error.
    /// </summary>
    public static RestoreDescriptorResolution ResolveTerminalDescriptor(
        RestoreDescriptor descriptor,
        string? path = null,
        string? pathExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Kind != PaneKind.Terminal)
        {
            return new RestoreDescriptorResolution(
                descriptor,
                false,
                $"Cannot resolve a terminal executable for pane kind '{descriptor.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.Program))
        {
            return new RestoreDescriptorResolution(
                descriptor,
                false,
                "The terminal restore descriptor has no executable program.");
        }

        var resolution = Resolve(descriptor.Program, path, pathExtensions);
        return resolution.Succeeded
            ? new RestoreDescriptorResolution(
                descriptor with { Program = resolution.AbsolutePath },
                true,
                null)
            : new RestoreDescriptorResolution(descriptor, false, resolution.Error);
    }

    private static IEnumerable<string> SplitSearchPath(string path)
    {
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = raw.Trim().Trim('"');
            if (directory.Length > 0)
            {
                yield return directory;
            }
        }
    }

    private static IEnumerable<string> CandidateExtensions(string program, string? pathExtensions)
    {
        if (Path.HasExtension(program))
        {
            yield return string.Empty;
            yield break;
        }

        // An extensionless executable is legal, even though ordinary Windows tools use PATHEXT.
        yield return string.Empty;

        var configured = string.IsNullOrWhiteSpace(pathExtensions)
            ? Environment.GetEnvironmentVariable("PATHEXT")
            : pathExtensions;
        configured = string.IsNullOrWhiteSpace(configured) ? DefaultPathExtensions : configured;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var extension = raw.Trim();
            if (extension.Length == 0)
            {
                continue;
            }

            if (extension[0] != '.')
            {
                extension = "." + extension;
            }

            if (seen.Add(extension))
            {
                yield return extension;
            }
        }
    }

    private static bool ContainsDirectorySeparator(string program) =>
        program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar);

    private static ExecutablePathResolution Success(string program, string path) =>
        new(program, path, null);

    private static ExecutablePathResolution Failure(string program, string error) =>
        new(program, null, error);
}

/// <summary>The normalized descriptor, or the original descriptor plus a surfaceable failure.</summary>
public sealed record RestoreDescriptorResolution(
    RestoreDescriptor Descriptor,
    bool Succeeded,
    string? Error);
