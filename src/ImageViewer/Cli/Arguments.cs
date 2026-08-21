using System.Globalization;

namespace ImageViewer.Cli;

/// <summary>
/// A parsed command line: positional values, plus flags and their values.
/// </summary>
/// <remarks>
/// Deliberately small and dependency-free. A parsing library would be more capable than anything
/// here needs and would put a package on the startup path of an application whose entire premise is
/// how little it loads.
/// </remarks>
internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    /// <summary>Values that were not flags, in the order given.</summary>
    internal IReadOnlyList<string> Positional => _positional;

    internal Arguments(IEnumerable<string> args)
    {
        var literal = false;

        using var walker = args.GetEnumerator();

        while (walker.MoveNext())
        {
            var argument = walker.Current;
            if (string.IsNullOrEmpty(argument)) continue;

            // Everything after a bare "--" is a value, however much it looks like a flag. This is
            // what lets a file called "--odd.jpg" be addressed at all.
            if (literal || !argument.StartsWith('-'))
            {
                _positional.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                literal = true;
                continue;
            }

            var name = argument.TrimStart('-');

            // "--width=800" and "--width 800" are the same thing.
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                _flags[name[..equals]] = name[(equals + 1)..];
                continue;
            }

            _flags[name] = null;
        }

        // A second pass pairs "--width 800": the value landed in the positional list because it
        // does not start with a dash, and only the flag itself knows it was expecting one.
        BindDetachedValues(args);
    }

    /// <summary>
    /// Moves "--flag value" pairs out of the positional list.
    /// </summary>
    /// <remarks>
    /// Done as a second pass rather than inline because a flag may legitimately take no value
    /// (<c>--overwrite</c>), and only the list of value-taking flags can tell the two apart.
    /// Guessing from position alone would swallow the first file name after every boolean flag.
    /// </remarks>
    private void BindDetachedValues(IEnumerable<string> args)
    {
        var sequence = args.Where(a => !string.IsNullOrEmpty(a)).ToArray();
        var literal = false;

        for (var i = 0; i < sequence.Length - 1; i++)
        {
            if (sequence[i] == "--") { literal = true; continue; }
            if (literal || !sequence[i].StartsWith('-')) continue;

            var name = sequence[i].TrimStart('-');
            if (name.Contains('=')) continue;
            if (!ValueTaking.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            var value = sequence[i + 1];
            if (value.StartsWith('-')) continue;

            _flags[name] = value;
            _positional.Remove(value);
        }
    }

    /// <summary>Flags that consume the argument after them.</summary>
    private static readonly string[] ValueTaking =
        ["width", "height", "size", "quality", "out-dir", "format", "slideshow"];

    internal bool Has(string name) => _flags.ContainsKey(name);

    internal string? Value(string name) =>
        _flags.TryGetValue(name, out var value) ? value : null;

    /// <summary>Reads a flag's value as a positive integer.</summary>
    /// <exception cref="UsageException">Present but not a usable number.</exception>
    internal int? Integer(string name)
    {
        var text = Value(name);
        if (text is null) return Has(name) ? throw new UsageException($"--{name} needs a number") : null;

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new UsageException($"--{name} needs a number, not '{text}'");

        if (value <= 0) throw new UsageException($"--{name} must be greater than zero");

        return value;
    }

    /// <summary>Rejects anything the command does not understand.</summary>
    /// <remarks>
    /// A silently ignored flag is worse than an error: someone who mistypes <c>--quality</c> would
    /// otherwise get a batch of files written at the wrong setting and no hint that it happened.
    /// </remarks>
    internal void RejectUnknown(params string[] known)
    {
        var unknown = _flags.Keys
            .Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (unknown.Length > 0)
            throw new UsageException($"unknown option '--{unknown[0]}'");
    }

    /// <summary>The single positional value a command requires.</summary>
    internal string RequireOne(string what)
    {
        if (_positional.Count == 0) throw new UsageException($"missing {what}");
        if (_positional.Count > 1) throw new UsageException($"expected one {what}, got {_positional.Count}");
        return _positional[0];
    }

    /// <summary>At least one positional value.</summary>
    internal IReadOnlyList<string> RequireSome(string what)
    {
        if (_positional.Count == 0) throw new UsageException($"missing {what}");
        return _positional;
    }
}
