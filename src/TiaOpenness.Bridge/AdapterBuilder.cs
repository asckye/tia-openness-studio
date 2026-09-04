using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Environment;

namespace TiaOpenness.Bridge
{
    /// <summary>
    /// Builds the Openness adapter against the TIA Portal installed on this machine.
    ///
    /// This is the one thing that cannot be done before shipping. The adapter has to be compiled
    /// against the Siemens.Engineering assemblies, and Siemens does not permit redistributing
    /// those - even their own NuGet package resolves them from the local install - so no build
    /// machine without TIA Portal can produce it.
    ///
    /// Doing it here rather than in a setup script means the app can offer it as a button, and
    /// the bridge picks the result up on the next connect without a restart.
    ///
    /// It compiles the real typed adapter rather than loading the API by reflection, so a member
    /// this build expects but that this TIA version lacks comes back as a compiler error naming a
    /// file and line - a usable bug report instead of a failure hours later on one code path.
    /// </summary>
    public static class AdapterBuilder
    {
        private const string AdapterAssembly = "TiaOpenness.Openness.dll";

        /// <summary>
        /// True when building is both possible and worth attempting here: the adapter is missing,
        /// the payload was unpacked, and there is a TIA Portal to compile against.
        /// </summary>
        public static bool CanBuild()
        {
            try
            {
                var bridgeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(bridgeDirectory)) return false;

                var payloadRoot = Path.GetDirectoryName(bridgeDirectory);
                if (payloadRoot == null) return false;

                if (!File.Exists(Path.Combine(payloadRoot, "compiler", "csc.exe"))) return false;
                if (!Directory.Exists(Path.Combine(payloadRoot, "adapter"))) return false;

                var installed = OpennessLocator.FindAll();
                if (installed.Count == 0) return false;

                return !File.Exists(Path.Combine(bridgeDirectory, AdapterAssembly))
                    || IsStale(bridgeDirectory, installed);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True when the adapter was built against a TIA version that is no longer installed -
        /// which is what upgrading TIA Portal leaves behind. The assembly is still there, so
        /// "missing" does not catch it; loading it just fails to bind. Rebuilding is the fix, and
        /// it is the same fix whether the operator knows to ask for it or not.
        /// </summary>
        private static bool IsStale(string bridgeDirectory, IEnumerable<OpennessInstallation> installed)
        {
            var stamp = Path.Combine(bridgeDirectory, "OPENNESS_ADAPTER.txt");
            if (!File.Exists(stamp)) return false;

            try
            {
                var builtAgainst = File.ReadAllLines(stamp)
                    .FirstOrDefault(line => line.StartsWith("version=", StringComparison.Ordinal))
                    ?.Substring("version=".Length)
                    .Trim();

                if (string.IsNullOrEmpty(builtAgainst)) return false;

                return !installed.Any(i => string.Equals(i.Version, builtAgainst, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static AdapterBuildResult Build(string opennessVersion)
        {
            var bridgeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var payloadRoot = Path.GetDirectoryName(bridgeDirectory);

            var compiler = Path.Combine(payloadRoot ?? string.Empty, "compiler", "csc.exe");
            var adapterSources = Path.Combine(payloadRoot ?? string.Empty, "adapter");
            var output = Path.Combine(bridgeDirectory ?? string.Empty, AdapterAssembly);

            if (!File.Exists(compiler))
            {
                throw new InvalidOperationException(
                    "The bundled C# compiler is missing (looked for " + compiler + "). This build of " +
                    "the bridge was not started from an unpacked payload.");
            }

            var sources = Directory.Exists(adapterSources)
                ? Directory.GetFiles(adapterSources, "*.cs")
                : new string[0];

            if (sources.Length == 0)
            {
                throw new InvalidOperationException("No adapter sources found in " + adapterSources + ".");
            }

            var install = OpennessLocator.Resolve(opennessVersion);
            if (install == null)
            {
                throw new OpennessNotInstalledException(opennessVersion);
            }

            var siemens = Directory.GetFiles(install.PublicApiDirectory, "Siemens.Engineering*.dll");
            if (siemens.Length == 0)
            {
                throw new InvalidOperationException(
                    "No Siemens.Engineering* assemblies in " + install.PublicApiDirectory + ".");
            }

            var result = new AdapterBuildResult
            {
                OpennessVersion = install.Version,
                ReferenceDirectory = install.PublicApiDirectory,
                ReferencedAssemblies = siemens.Length,
                OutputPath = output,
            };

            var log = Run(compiler, Arguments(output, siemens, sources, bridgeDirectory), out var exitCode);
            result.Succeeded = exitCode == 0;

            if (!result.Succeeded)
            {
                result.Errors = log
                    .Where(line => line.IndexOf("error CS", StringComparison.Ordinal) >= 0)
                    .Take(50)
                    .ToList();

                if (result.Errors.Count == 0) result.Errors.Add(string.Join(System.Environment.NewLine, log));

                // A half-written assembly would be loaded on the next connect and fail obscurely.
                TryDelete(output);
                return result;
            }

            File.WriteAllText(
                Path.Combine(bridgeDirectory, "OPENNESS_ADAPTER.txt"),
                "version=" + install.Version + System.Environment.NewLine +
                "modular=" + install.IsModular + System.Environment.NewLine +
                "source=" + install.PublicApiDirectory + System.Environment.NewLine +
                "built=" + DateTimeOffset.UtcNow.ToString("O") + System.Environment.NewLine);

            return result;
        }

        private static string Arguments(string output, IEnumerable<string> siemens,
            IEnumerable<string> sources, string bridgeDirectory)
        {
            // netstandard.dll is the facade that lets net48 code consume the netstandard2.0
            // contracts assembly. It ships with .NET Framework 4.7.2 and later, so it is beside
            // the other framework assemblies on any machine that can run this process at all.
            var framework = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
                @"Microsoft.NET\Framework64\v4.0.30319");

            var arguments = new StringBuilder();
            arguments.Append("/noconfig /nologo /target:library /platform:x64 /langversion:latest /optimize+ /debug-");
            arguments.Append(" \"/out:").Append(output).Append('"');

            foreach (var name in new[] { "System.dll", "System.Core.dll", "netstandard.dll" })
            {
                arguments.Append(" \"/r:").Append(Path.Combine(framework, name)).Append('"');
            }

            foreach (var name in new[] { "TiaOpenness.Core.dll", "TiaOpenness.Contracts.dll", "Newtonsoft.Json.dll" })
            {
                arguments.Append(" \"/r:").Append(Path.Combine(bridgeDirectory, name)).Append('"');
            }

            foreach (var reference in siemens) arguments.Append(" \"/r:").Append(reference).Append('"');
            foreach (var source in sources) arguments.Append(" \"").Append(source).Append('"');

            return arguments.ToString();
        }

        private static List<string> Run(string executable, string arguments, out int exitCode)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var output = new List<string>();
            using (var process = Process.Start(startInfo))
            {
                output.AddRange(process.StandardOutput.ReadToEnd()
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                output.AddRange(process.StandardError.ReadToEnd()
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

                process.WaitForExit();
                exitCode = process.ExitCode;
            }
            return output;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { /* the failed build is already the message */ }
        }
    }
}
