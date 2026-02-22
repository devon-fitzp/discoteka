using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

namespace discoteka.Playback;

internal static class LibVlcNativeResolver
{
    private static int _registered;

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
            }
        }

        foreach (var fileName in candidateFileNames)
        {
            if (NativeLibrary.TryLoad(fileName, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

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
