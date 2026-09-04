using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaOpenness.Client
{
    /// <summary>
    /// Unpacks the pieces that ship inside the executable but have to exist as files to be useful.
    ///
    /// Three things cannot live in the .NET 10 exe itself. The bridge is a .NET Framework 4.8
    /// process, because the Siemens assemblies it loads are .NET Framework only. The Openness
    /// adapter cannot ship compiled, because those assemblies are not redistributable and no
    /// machine without TIA Portal can build against them. And the C# compiler that builds it on
    /// the target machine has to be an exe to be run. Embedding all three and extracting on first
    /// use is what keeps the product a single file to hand someone.
    /// </summary>
    public static class ToolchainPayload
    {
        private const string Prefix = "payload/";
        private const string BridgeExe = "TiaOpenness.Bridge.exe";

        private static readonly object Gate = new object();
        private static string _root;

        /// <summary>Folder the payload was unpacked into, or null when nothing is embedded.</summary>
        public static string Root { get { return _root; } }

        public static string BridgeDirectory { get { return _root == null ? null : Path.Combine(_root, "bridge"); } }
        public static string CompilerDirectory { get { return _root == null ? null : Path.Combine(_root, "compiler"); } }
        public static string AdapterDirectory { get { return _root == null ? null : Path.Combine(_root, "adapter"); } }

        /// <summary>
        /// Extracts the payload if it is not already there, and returns the bridge executable's
        /// path. Returns null when this build carries no payload, so callers fall back to a
        /// bridge sitting beside the exe.
        /// </summary>
        public static string EnsureExtracted()
        {
            lock (Gate)
            {
                if (_root != null) return Path.Combine(_root, "bridge", BridgeExe);

                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                var names = assembly.GetManifestResourceNames()
                    .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
                    .ToList();

                if (names.Count == 0) return null;

                var target = VersionedFolder(assembly);
                var stamp = Path.Combine(target, ".complete");

                // Re-extracting on every start would fight a running bridge for its own exe, so
                // the marker is written last and checked first.
                if (!File.Exists(stamp))
                {
                    Extract(assembly, names, target);
                    File.WriteAllText(stamp, DateTimeOffset.UtcNow.ToString("O"));
                }

                _root = target;
                return Path.Combine(target, "bridge", BridgeExe);
            }
        }

        /// <summary>
        /// One folder per version, so an upgrade never runs last version's bridge, and so the
        /// adapter built for one version is not silently reused by another.
        /// </summary>
        private static string VersionedFolder(Assembly assembly)
        {
            var version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TiaOpenness", version);
        }

        private static void Extract(Assembly assembly, IEnumerable<string> names, string target)
        {
            foreach (var name in names)
            {
                var relative = name.Substring(Prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                using (var source = assembly.GetManifestResourceStream(name))
                {
                    if (source == null) continue;
                    using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        source.CopyTo(destination);
                    }
                }
            }
        }

        /// <summary>
        /// Where a built Openness adapter belongs: beside the bridge, which is where the bridge
        /// looks for it.
        /// </summary>
        public static string AdapterPath()
        {
            var bridge = BridgeDirectory;
            return bridge == null ? null : Path.Combine(bridge, "TiaOpenness.Openness.dll");
        }
    }
}
