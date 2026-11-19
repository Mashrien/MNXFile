using System.Reflection;

namespace MNX;

/// <summary>Identifies the archive engine independently of an opened archive's format version.</summary>
public static class LibraryInfo {
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion() {
        Assembly engine = typeof(Archive).Assembly;
        AssemblyInformationalVersionAttribute? information = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return information?.InformationalVersion ?? engine.GetName().Version?.ToString() ?? "unknown";
        }
    }
