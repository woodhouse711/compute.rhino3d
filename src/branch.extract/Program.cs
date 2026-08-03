using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

using Microsoft.Win32;

namespace BranchExtract;

using ExtractionDocument = ExtractionRecord;

internal static class Program
{
    internal const string BranchPluginGuid = "bc174e4e-64cd-4b41-917d-3c54252b953e";

    private static string _rhinoSystemDir;
    private static string _grasshopperDir;
    private static string _branchDir;

    static Program() => RhinoInside.Resolver.Initialize();

    [STAThread]
    private static int Main(string[] args)
    {
        if (!CommandLineOptions.TryParse(args, out CommandLineOptions options, out string usageError))
        {
            Console.Error.WriteLine(usageError);
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 2;
        }

        StartParentWatchdog(options.ParentProcessId);

        RunCoordinator coordinator = null;
        try
        {
            Bootstrap();
            coordinator = new RunCoordinator(options, _branchDir);
            coordinator.StartWatchdog();

            ExtractionDocument result = RuntimeHost.Execute(options, coordinator);
            coordinator.Complete(result);
            Console.WriteLine($"wrote {options.OutputPath}");
            return 0;
        }
        catch (PluginLoadException ex)
        {
            Console.Error.WriteLine(ex.Message);
            if (coordinator == null)
                coordinator = new RunCoordinator(options, _branchDir);

            ExtractionDocument result = ExtractionDocumentFactory.CreateStatus(
                options,
                _branchDir,
                "plugin_load_failed",
                ex.Message);
            try
            {
                coordinator.Complete(result);
                return 0;
            }
            catch (Exception writeException)
            {
                Console.Error.WriteLine(writeException);
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (coordinator == null)
                coordinator = new RunCoordinator(options, _branchDir);

            try
            {
                ExtractionDocument result = ExtractionDocumentFactory.CreateStatus(
                    options,
                    _branchDir,
                    "crashed",
                    $"{ex.GetType().Name}: {ex.Message}");
                coordinator.Complete(result);
            }
            catch (Exception writeException)
            {
                Console.Error.WriteLine(writeException);
            }
            return 1;
        }
        finally
        {
            coordinator?.CancelWatchdog();
        }
    }

    /// <summary>
    /// Exit as soon as the supervisor does. We are a grandchild of rhino.compute, so a
    /// parent crash would otherwise leave us running indefinitely, holding a Rhino licence
    /// and a temp file that nobody will ever collect. A background thread is used rather
    /// than Exited events because the parent is not our own child process.
    /// </summary>
    private static void StartParentWatchdog(int parentProcessId)
    {
        if (parentProcessId <= 0)
            return;

        System.Diagnostics.Process parent;
        try
        {
            parent = System.Diagnostics.Process.GetProcessById(parentProcessId);
        }
        catch (ArgumentException)
        {
            // Already gone before we started — there is nothing to extract for.
            Console.Error.WriteLine($"Supervisor {parentProcessId} is not running; exiting.");
            Environment.Exit(3);
            return;
        }

        var watchdog = new System.Threading.Thread(() =>
        {
            try
            {
                parent.WaitForExit();
                Console.Error.WriteLine($"Supervisor {parentProcessId} exited; abandoning extraction.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Parent watchdog failed ({ex.GetType().Name}); abandoning extraction.");
            }

            // Hard exit: Rhino is mid-flight on the main thread and will not unwind cleanly.
            Environment.Exit(3);
        })
        {
            IsBackground = true,
            Name = "parent-watchdog"
        };
        watchdog.Start();
    }

    private static void Bootstrap()
    {
        _rhinoSystemDir = FindRhinoSystemDir();
        DirectoryInfo parent = Directory.GetParent(_rhinoSystemDir);
        _grasshopperDir = parent == null
            ? null
            : Path.Combine(parent.FullName, "Plug-ins", "Grasshopper");
        _branchDir = FindBranchDir();

        if (string.IsNullOrWhiteSpace(_branchDir) || !Directory.Exists(_branchDir))
        {
            throw new PluginLoadException(
                "Branch plugin directory was not resolvable from the Rhino 8 registry entry.");
        }

        Console.WriteLine($"Rhino System : {_rhinoSystemDir}");
        Console.WriteLine($"Branch dir   : {_branchDir}");
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
    }

    private static Assembly ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        string simpleName = name.Name;
        if (string.IsNullOrEmpty(simpleName))
            return null;

        if (simpleName.StartsWith("Rhino", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Eto", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("opennurbs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (string directory in new[] { _branchDir, _grasshopperDir })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            foreach (string extension in new[] { ".dll", ".rhp" })
            {
                string candidate = Path.Combine(directory, simpleName + extension);
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }

    private static string FindRhinoSystemDir()
    {
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey key = hive.OpenSubKey(
                @"SOFTWARE\McNeel\Rhinoceros\8.0\Install");
            if (key?.GetValue("Path") is string path && Directory.Exists(path))
                return path.TrimEnd('\\');
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Rhino 8",
            "System");
        if (!Directory.Exists(fallback))
            throw new DirectoryNotFoundException("Rhino 8 System directory was not found.");
        return fallback;
    }

    private static string FindBranchDir()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(
            $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{BranchPluginGuid}\PlugIn");
        if (key?.GetValue("FileName") is not string rhp || !File.Exists(rhp))
            return null;
        return Path.GetDirectoryName(rhp);
    }
}

internal sealed class CommandLineOptions
{
    internal const int DefaultTimeoutMs = 300_000;
    internal const string Usage =
        "usage: branch.extract.exe --model <path.3dm> --out <path.json> [--timeout-ms N]";

    internal string ModelPath { get; private set; }
    internal string OutputPath { get; private set; }
    internal int TimeoutMs { get; private set; } = DefaultTimeoutMs;

    /// <summary>Supervisor PID from -childof:&lt;pid&gt;, or 0 when unsupervised.</summary>
    internal int ParentProcessId { get; private set; }

    internal static bool TryParse(
        string[] args,
        out CommandLineOptions options,
        out string error)
    {
        options = null;
        error = null;
        string model = null;
        string output = null;
        int timeout = DefaultTimeoutMs;

        int parentProcessId = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];

            // `-childof:<pid>` carries its value inline as a single token — the convention
            // rhino.compute already uses to supervise compute.geometry (see Shutdown.cs) —
            // so it must NOT consume the following argument. The supervisor passes this so
            // we can self-terminate if it dies: our children are grandchildren of
            // rhino.compute and would otherwise survive a parent crash holding a Rhino
            // licence and a temp file indefinitely.
            if (argument.StartsWith("-childof:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--childof:", StringComparison.OrdinalIgnoreCase))
            {
                string pidText = argument.Substring(argument.IndexOf(':') + 1);
                if (!int.TryParse(pidText, out parentProcessId) || parentProcessId <= 0)
                {
                    error = $"Invalid parent process id in {argument}.";
                    return false;
                }
                continue;
            }

            if (argument is not ("--model" or "--out" or "--timeout-ms"))
            {
                error = $"Unknown argument: {argument}";
                return false;
            }

            if (++i >= args.Length)
            {
                error = $"Missing value for {argument}.";
                return false;
            }

            string value = args[i].Trim().Trim('"');
            switch (argument)
            {
                case "--model":
                    if (model != null)
                    {
                        error = "--model may only be supplied once.";
                        return false;
                    }
                    model = value;
                    break;
                case "--out":
                    if (output != null)
                    {
                        error = "--out may only be supplied once.";
                        return false;
                    }
                    output = value;
                    break;
                case "--timeout-ms":
                    if (!int.TryParse(
                            value,
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out timeout)
                        || timeout <= 0)
                    {
                        error = "--timeout-ms must be a positive 32-bit integer.";
                        return false;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(output))
        {
            error = "--model and --out are required.";
            return false;
        }

        string modelPath;
        string outputPath;
        try
        {
            modelPath = Path.GetFullPath(model);
            outputPath = Path.GetFullPath(output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Invalid path: {ex.Message}";
            return false;
        }

        if (!File.Exists(modelPath))
        {
            error = $"Model not found: {modelPath}";
            return false;
        }
        if (string.Equals(
                modelPath,
                outputPath,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "--out must not overwrite the input model.";
            return false;
        }

        options = new CommandLineOptions
        {
            ModelPath = modelPath,
            OutputPath = outputPath,
            TimeoutMs = timeout,
            ParentProcessId = parentProcessId
        };
        return true;
    }
}

internal static class RuntimeHost
{
    internal static ExtractionDocument Execute(
        CommandLineOptions options,
        RunCoordinator coordinator)
    {
        Rhino.Runtime.InProcess.RhinoCore core = null;
        try
        {
            core = new Rhino.Runtime.InProcess.RhinoCore(
                Array.Empty<string>(),
                Rhino.Runtime.InProcess.WindowStyle.NoWindow);
            Console.WriteLine($"Rhino {Rhino.RhinoApp.Version}");

            Guid pluginId = new(Program.BranchPluginGuid);
            if (!Rhino.PlugIns.PlugIn.LoadPlugIn(pluginId))
                throw new PluginLoadException("Branch plugin failed to load.");

            string lastDocumentWarning = SetPluginLastDocumentFileName(options.ModelPath);
            Console.WriteLine($"Opening: {Path.GetFileName(options.ModelPath)}");

            Rhino.RhinoDoc document = Rhino.RhinoDoc.OpenHeadless(options.ModelPath);
            if (document == null)
                throw new IOException($"RhinoDoc.OpenHeadless returned null for {options.ModelPath}.");
            Rhino.RhinoDoc.ActiveDoc = document;

            return Extractor.Extract(
                document,
                options,
                coordinator,
                lastDocumentWarning);
        }
        finally
        {
            try
            {
                core?.Dispose();
            }
            catch
            {
                // Process exit is the Branch document teardown boundary.
            }
        }
    }

    private static string SetPluginLastDocumentFileName(string path)
    {
        try
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate =>
                    candidate.GetName().Name == "Branch");
            Type type = assembly?.GetType("br.desktop.BranchPlugIn");
            object instance = type?.GetProperty(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            PropertyInfo property = type?.GetProperty(
                "LastDocumentFileName",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (instance == null || property == null)
                return "Could not resolve BranchPlugIn.LastDocumentFileName.";

            property.SetValue(instance, path);
            return null;
        }
        catch (Exception ex)
        {
            Exception actual = ex is TargetInvocationException invocation
                ? invocation.InnerException ?? ex
                : ex;
            return $"BranchPlugIn.LastDocumentFileName setter failed: {actual.Message}";
        }
    }
}

internal sealed class RunCoordinator
{
    private readonly CommandLineOptions _options;
    private readonly string _branchDir;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ManualResetEvent _cancel = new(false);
    private Thread _watchdog;
    private int _terminal;
    private int _branchDocumentLoaded;

    internal RunCoordinator(CommandLineOptions options, string branchDir)
    {
        _options = options;
        _branchDir = branchDir;

        string outputDirectory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
    }

    internal void StartWatchdog()
    {
        _watchdog = new Thread(WatchdogMain)
        {
            IsBackground = true,
            Name = "branch.extract timeout watchdog"
        };
        _watchdog.Start();
    }

    internal void MarkBranchDocumentLoaded()
    {
        Volatile.Write(ref _branchDocumentLoaded, 1);
    }

    internal void Complete(ExtractionDocument document)
    {
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
            return;

        Stamp(document);
        ExtractionJson.Write(_options.OutputPath, document);
    }

    internal void CancelWatchdog()
    {
        _cancel.Set();
    }

    private void WatchdogMain()
    {
        if (_cancel.WaitOne(_options.TimeoutMs))
            return;
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
            return;

        bool branchLoaded = Volatile.Read(ref _branchDocumentLoaded) != 0;
        string status = branchLoaded ? "timeout" : "needs_upgrade";
        string detail = branchLoaded
            ? $"Timed out after {_options.TimeoutMs} ms after BranchDoc materialized."
            : $"Timed out after {_options.TimeoutMs} ms before BranchDoc materialized; the model may require an interactive Branch upgrade.";

        try
        {
            ExtractionDocument document = ExtractionDocumentFactory.CreateStatus(
                _options,
                _branchDir,
                status,
                detail);
            Stamp(document);
            ExtractionJson.Write(_options.OutputPath, document);
            Console.Error.WriteLine(detail);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    private void Stamp(ExtractionDocument document)
    {
        DateTime generatedAt = DateTime.UtcNow;
        document.GeneratedAt = generatedAt.ToString(
            "O",
            CultureInfo.InvariantCulture);
        document.SnapshotDate = DateTime.Now.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        document.Extraction.ElapsedMs = _stopwatch.ElapsedMilliseconds;
    }
}

internal static class ExtractionDocumentFactory
{
    private static readonly Regex JobPattern = new(
        @"(?<![A-Z0-9])(S\d{3,})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static ExtractionDocument CreateBase(
        CommandLineOptions options,
        string branchDir)
    {
        DateTime generatedAt = DateTime.UtcNow;
        string job = InferJob(options.ModelPath);
        return new ExtractionDocument
        {
            GeneratedAt = generatedAt.ToString("O", CultureInfo.InvariantCulture),
            SnapshotDate = DateTime.Now.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            Job = job,
            JobName = InferJobName(options.ModelPath, job),
            ProductSubtype = null,
            Source = new SourceRecord
            {
                ExtractorVersion = typeof(Program).Assembly
                    .GetName()
                    .Version
                    ?.ToString(3),
                BranchPluginVersion = InferBranchVersion(branchDir),
                RhinoVersion = null,
                ModelFile = Path.GetFileName(options.ModelPath),
                ModelRevision = File.GetLastWriteTimeUtc(options.ModelPath)
                    .ToString("O", CultureInfo.InvariantCulture),
                BranchProjectGuid = null
            },
            Extraction = new ExtractionMetadata
            {
                Status = "crashed",
                StatusDetail = null
            },
            Lod = null,
            Panels = new(),
            Elements = new(),
            Summary = SummaryBuilder.Build(
                new System.Collections.Generic.List<Panel>())
        };
    }

    internal static ExtractionDocument CreateStatus(
        CommandLineOptions options,
        string branchDir,
        string status,
        string detail)
    {
        ExtractionDocument document = CreateBase(options, branchDir);
        document.Extraction.Status = status;
        document.Extraction.StatusDetail = detail;
        return document;
    }

    private static string InferBranchVersion(string branchDir)
    {
        if (string.IsNullOrWhiteSpace(branchDir))
            return null;
        return Path.GetFileName(branchDir.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
    }

    private static string InferJob(string modelPath)
    {
        Match match = JobPattern.Match(modelPath);
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant()
            : Path.GetFileNameWithoutExtension(modelPath);
    }

    private static string InferJobName(string modelPath, string job)
    {
        string directory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        string[] components = directory.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string component in components.Reverse())
        {
            Match match = Regex.Match(
                component,
                $@"^{Regex.Escape(job)}[\s_-]+(.+)$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            string candidate = match.Groups[1].Value.Trim();
            if (candidate.Length > 0 && !candidate.Equals("DLT", StringComparison.OrdinalIgnoreCase))
                return DisplayCase(candidate);
        }

        return null;
    }

    private static string DisplayCase(string text)
    {
        return string.Join(
            " ",
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token =>
                    token.Length <= 4 && token.All(character =>
                        !char.IsLetter(character) || char.IsUpper(character))
                        ? token
                        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                            token.ToLowerInvariant())));
    }

}

internal static class ExtractionJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    internal static void Write(string outputPath, ExtractionDocument document)
    {
        string directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = outputPath + $".{Environment.ProcessId}.tmp";
        string json = JsonSerializer.Serialize(document, Options);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, outputPath, true);
    }
}

internal sealed class PluginLoadException : Exception
{
    internal PluginLoadException(string message) : base(message)
    {
    }
}
