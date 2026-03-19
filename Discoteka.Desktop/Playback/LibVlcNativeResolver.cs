using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace Discoteka.Desktop.Playback;

/// <summary>
/// Registers a custom <see cref="NativeLibrary"/> import resolver for libvlc and libvlccore
/// on Linux, where the NuGet package does not bundle native binaries.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Directories listed in <c>LD_LIBRARY_PATH</c>.</item>
///   <item>Standard system library directories: <c>/usr/lib/x86_64-linux-gnu</c>, <c>/usr/lib64</c>,
///         <c>/usr/lib</c>, <c>/lib/x86_64-linux-gnu</c>, <c>/lib64</c>, <c>/lib</c>, <c>/usr/local/lib</c>.</item>
///   <item>System-default DLL search (via <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/>).</item>
/// </list>
/// Falls back to <see cref="IntPtr.Zero"/> if no candidate succeeds, letting VLC emit its own error.
/// </para>
/// <para>
/// Registration is one-shot: subsequent calls to <see cref="Register"/> are no-ops, ensured by
/// an <see cref="Interlocked.Exchange"/> flag.
/// </para>
/// </summary>
internal static class LibVlcNativeResolver
{
    private static int _registered;

    /// <summary>
    /// Registers the custom resolver. Safe to call multiple times — only the first call has effect.
    /// On non-Linux platforms this is a no-op.
    /// </summary>
    public static void Register()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, Resolve);
    }

    /// <summary>Returns a human-readable hint for users whose libVLC installation is missing.</summary>
    public static string BuildLinuxDependencyHint()
    {
        return "Install system VLC libs (e.g. libvlc5, libvlccore9, vlc-plugin-base, libvlc-dev), then relaunch.";
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return IntPtr.Zero;
        }

        if (!libraryName.Equals("libvlc", StringComparison.Ordinal) &&
            !libraryName.Equals("libvlccore", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        // Try versioned filenames first (more specific), then unversioned fallbacks
        var candidateFileNames = libraryName.Equals("libvlc", StringComparison.Ordinal)
            ? new[] { "libvlc.so.5", "libvlc.so", "libvlc" }
            : new[] { "libvlccore.so.9", "libvlccore.so", "libvlccore" };

        foreach (var path in EnumerateCandidatePaths(candidateFileNames))
        {
            try
            {
                return NativeLibrary.Load(path);
            }
            catch
            {
                // This path didn't work — try the next candidate.
            }
        }

        // Final fallback: let the runtime search LD_LIBRARY_PATH and system defaults by filename alone
        foreach (var fileName in candidateFileNames)
        {
            if (NativeLibrary.TryLoad(fileName, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Yields fully-qualified candidate paths by combining each known library directory
    /// with each candidate file name. Directories that do not exist are skipped.
    /// </summary>
    private static IEnumerable<string> EnumerateCandidatePaths(IEnumerable<string> fileNames)
    {
        var directories = new List<string>();

        var envPaths = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrWhiteSpace(envPaths))
        {
            directories.AddRange(envPaths
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        directories.AddRange(new[]
        {
            "/usr/lib/x86_64-linux-gnu",
            "/usr/lib64",
            "/usr/lib",
            "/lib/x86_64-linux-gnu",
            "/lib64",
            "/lib",
            "/usr/local/lib"
        });

        foreach (var directory in directories.Where(Directory.Exists).Distinct(StringComparer.Ordinal))
        {
            foreach (var fileName in fileNames)
            {
                yield return Path.Combine(directory, fileName);
            }
        }
    }
}
