using System.Text;

namespace LoadGenerator;

/// <summary>
/// Copies everything written to the console into a file as well (results/e03-x/console.txt), so the
/// console output of each scenario is saved by the program itself, not cut out of the compose log.
/// </summary>
public sealed class ConsoleTee : TextWriter
{
    private readonly TextWriter _console;
    private readonly StreamWriter _file;

    private ConsoleTee(TextWriter console, StreamWriter file)
    {
        _console = console;
        _file = file;
    }

    public override Encoding Encoding => _console.Encoding;

    public static ConsoleTee Start(string path)
    {
        var tee = new ConsoleTee(Console.Out, new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" });
        Console.SetOut(tee);
        return tee;
    }

    public override void Write(char value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        _file.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        _file.Write(value);
        _file.Write('\n');
    }

    public override void WriteLine()
    {
        _console.WriteLine();
        _file.Write('\n');
    }

    public override void Flush()
    {
        _console.Flush();
        _file.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Console.SetOut(_console);
            _file.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Creates output directories that the host user can clean up.</summary>
public static class ResultsDirectory
{
    /// <summary>
    /// Creates <paramref name="path"/> (and its parents). On Linux the load generator runs as root so it
    /// can write into the bind-mounted results/; subdirectories it creates would then be root-owned and
    /// their files not deletable by you. They are therefore made world-writable (rwxrwxrwx): a local lab
    /// output folder, nothing secret.
    /// </summary>
    public static string Create(string path)
    {
        var missing = new Stack<string>();
        for (var dir = Path.GetFullPath(path); !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)!)
            missing.Push(dir);
        while (missing.TryPop(out var dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_111);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
            }
        }
        return path;
    }
}
