using System.Reflection;
using System.Runtime.Loader;
using OrbSolver.Contracts;

namespace OrbSolver.Worker;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string ContractsAssemblyName = "OrbSolver.Contracts";
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string solverAssemblyPath)
        : base(isCollectible: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solverAssemblyPath);
        _resolver = new AssemblyDependencyResolver(Path.GetFullPath(solverAssemblyPath));
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 在相依性解析之前，先將 OrbSolver.Contracts 委派給 AssemblyLoadContext.Default 載入，
        // 讓外掛與宿主共用完全相同的合約型別識別。
        if (string.Equals(assemblyName.Name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is not null ? LoadUnmanagedDllFromPath(libraryPath) : nint.Zero;
    }
}

internal sealed record SolverLoadOutcome(
    bool Success,
    IOrbSolver? Solver,
    string? SolverName,
    string? ModelTag,
    ExecutionMode Mode,
    string? ErrorType,
    string? ErrorMessage);

internal static class SolverLoader
{
    public static SolverLoadOutcome Load(string solverAssemblyPath)
    {
        if (string.IsNullOrWhiteSpace(solverAssemblyPath))
        {
            return Failure("FILE_NOT_FOUND", "Solver assembly path was empty.");
        }

        string fullPath = Path.GetFullPath(solverAssemblyPath);
        if (!File.Exists(fullPath))
        {
            return Failure("FILE_NOT_FOUND", $"Solver assembly not found: '{fullPath}'.");
        }

        Assembly assembly;
        try
        {
            var loadContext = new PluginLoadContext(fullPath);
            assembly = loadContext.LoadFromAssemblyPath(fullPath);
        }
        catch (Exception ex)
        {
            return Failure("LOAD_ERROR", $"Failed to load assembly '{fullPath}': {ex.GetType().Name}: {ex.Message}");
        }

        Type[] exportedTypes;
        try
        {
            exportedTypes = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            string loaderMessages = string.Join(
                "; ",
                ex.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message));
            return Failure("LOAD_ERROR", $"Failed to inspect exported types in '{fullPath}': {loaderMessages}");
        }
        catch (Exception ex)
        {
            return Failure("LOAD_ERROR", $"Failed to inspect assembly '{fullPath}': {ex.GetType().Name}: {ex.Message}");
        }

        List<Type> solverTypes = [];
        foreach (Type type in exportedTypes)
        {
            if (type.IsClass
                && !type.IsAbstract
                && !type.IsGenericTypeDefinition
                && typeof(IOrbSolver).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                solverTypes.Add(type);
            }
        }

        if (solverTypes.Count == 0)
        {
            return Failure("NO_SOLVER", $"No public non-abstract IOrbSolver with a parameterless constructor was found in '{fullPath}'.");
        }

        if (solverTypes.Count > 1)
        {
            string names = string.Join(", ", solverTypes.Select(t => t.FullName ?? t.Name));
            return Failure("MULTIPLE_SOLVERS", $"Multiple IOrbSolver implementations found in '{fullPath}': {names}.");
        }

        Type solverType = solverTypes[0];
        IOrbSolver solver;
        try
        {
            object? instance = Activator.CreateInstance(solverType);
            if (instance is not IOrbSolver typed)
            {
                return Failure("LOAD_ERROR", $"Instantiated '{solverType.FullName}' does not implement IOrbSolver.");
            }

            solver = typed;
        }
        catch (TargetInvocationException ex)
        {
            Exception inner = ex.InnerException ?? ex;
            return Failure("RE", $"Solver constructor threw {inner.GetType().Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            return Failure("LOAD_ERROR", $"Failed to instantiate '{solverType.FullName}': {ex.GetType().Name}: {ex.Message}");
        }

        string name;
        string modelTag;
        ExecutionMode mode;
        try
        {
            name = solver.Name;
            modelTag = solver.ModelTag;
            mode = solver.Mode;
        }
        catch (Exception ex)
        {
            return Failure("RE", $"Reading solver metadata threw {ex.GetType().Name}: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return Failure("LOAD_ERROR", $"Solver '{solverType.FullName}' returned an empty Name.");
        }

        if (!Enum.IsDefined(mode))
        {
            return Failure("LOAD_ERROR", $"Solver '{name}' returned an invalid ExecutionMode '{mode}'.");
        }

        return new SolverLoadOutcome(
            Success: true,
            Solver: solver,
            SolverName: name,
            ModelTag: modelTag ?? string.Empty,
            Mode: mode,
            ErrorType: null,
            ErrorMessage: null);
    }

    private static SolverLoadOutcome Failure(string errorType, string errorMessage) =>
        new(
            Success: false,
            Solver: null,
            SolverName: null,
            ModelTag: null,
            Mode: ExecutionMode.SingleThread,
            ErrorType: errorType,
            ErrorMessage: errorMessage);
}
