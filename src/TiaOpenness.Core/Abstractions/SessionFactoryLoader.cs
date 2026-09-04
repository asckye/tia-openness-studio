using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Core.Mock;

namespace TiaOpenness.Core.Abstractions
{
    /// <summary>
    /// Picks the session backend at run time. The Openness adapter is loaded by name rather
    /// than referenced, so the bridge builds and runs on a machine that has neither TIA Portal
    /// nor the Siemens assemblies; it then reports the real backend as unavailable rather than
    /// substituting the mock.
    /// </summary>
    public static class SessionFactoryLoader
    {
        /// <summary>Assembly that carries the real Siemens.Engineering adapter, when it was built.</summary>
        public const string OpennessAdapterAssembly = "TiaOpenness.Openness";

        /// <summary>Why the loader ended up with the backend it chose. Surfaced in the banner and in doctor output.</summary>
        public static string LastDecision { get; private set; } = "not resolved yet";

        /// <param name="forceMock">Skip Openness entirely; used by tests and by the demo mode.</param>
        /// <param name="opennessVersion">Version to bind, e.g. "21.0". Null selects the newest installed.</param>
        /// <remarks>
        /// The mock is never chosen as a silent fallback. An engineer who asked for their real
        /// project and quietly got a fixture would read invented block names as fact, so when
        /// the real backend is unavailable this returns a factory that fails loudly on first use
        /// and says why. <c>doctor.run</c> keeps working either way, because it needs no session.
        /// </remarks>
        public static ITiaSessionFactory Resolve(bool forceMock, string opennessVersion = null)
        {
            if (forceMock)
            {
                LastDecision = "mock requested explicitly";
                return new MockTiaSessionFactory();
            }

            var factory = LoadAdapter(opennessVersion);
            if (factory == null) return new UnavailableSessionFactory(LastDecision);

            try
            {
                factory.Configure(opennessVersion);
                return factory;
            }
            catch (Exception ex)
            {
                LastDecision = "the Openness adapter could not bind to a TIA installation: " + ex.Message;
                return new UnavailableSessionFactory(LastDecision);
            }
        }

        /// <summary>
        /// The instruction that actually applies here. A release carries the adapter sources and a
        /// compiler and builds them in place; a source checkout has neither but does have the
        /// build. Naming the wrong one sends people looking for something they do not have.
        /// </summary>
        private static string HowToBuildAdapter()
        {
            var bridgeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var payloadRoot = string.IsNullOrEmpty(bridgeDirectory)
                ? null
                : Path.GetDirectoryName(bridgeDirectory);

            if (payloadRoot != null && Directory.Exists(Path.Combine(payloadRoot, "compiler")))
            {
                return "On the machine with TIA Portal, press Enable Openness in the app; it builds " +
                       "the adapter against your installation and needs no .NET SDK.";
            }

            return "On a machine with TIA Portal, run tools\fetch-openness-dlls.ps1 and rebuild the solution.";
        }

        /// <summary>Returns null when the real backend cannot be used; <see cref="LastDecision"/> says why.</summary>
        private static ITiaSessionFactory LoadAdapter(string opennessVersion)
        {
            var adapterPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
                OpennessAdapterAssembly + ".dll");

            if (!File.Exists(adapterPath))
            {
                LastDecision = OpennessAdapterAssembly + ".dll is not deployed next to the bridge. " +
                               HowToBuildAdapter() + " Or pass --mock to use the synthetic project.";
                return null;
            }

            if (Environment.OpennessLocator.FindAll().Count == 0)
            {
                LastDecision = "the Openness adapter is present but no TIA Portal installation was found. " +
                               "Run doctor for details, or pass --mock to use the synthetic project.";
                return null;
            }

            // The resolver has to be in place before the adapter is even inspected. Scanning it
            // for the factory type makes the CLR load every type in it, and their signatures
            // mention Siemens.Engineering types that are not findable from the bridge's own
            // folder. Installing it later - when the factory is configured - was too late, and
            // showed up only as "unable to load one or more of the requested types".
            try
            {
                Environment.OpennessAssemblyResolver.Install(opennessVersion);
            }
            catch (Exception ex)
            {
                LastDecision = "could not bind to the Openness assemblies (" + ex.Message + ")";
                return null;
            }

            try
            {
                var assembly = Assembly.LoadFrom(adapterPath);
                var factoryType = TypesIn(assembly)
                    .FirstOrDefault(t => typeof(ITiaSessionFactory).IsAssignableFrom(t)
                                         && !t.IsAbstract && !t.IsInterface);

                if (factoryType == null)
                {
                    LastDecision = "no ITiaSessionFactory implementation in " + OpennessAdapterAssembly;
                    return null;
                }

                LastDecision = "using the Openness adapter from " + adapterPath;
                return (ITiaSessionFactory)Activator.CreateInstance(factoryType);
            }
            catch (Exception ex)
            {
                LastDecision = "failed to load the Openness adapter (" + Describe(ex) + ")";
                return null;
            }
        }

        /// <summary>
        /// The types an assembly actually managed to load. One type that cannot be resolved makes
        /// <see cref="Assembly.GetTypes"/> throw and take the whole scan with it, even though the
        /// factory type itself may be perfectly loadable.
        /// </summary>
        private static IEnumerable<Type> TypesIn(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }

        /// <summary>
        /// A message worth reading. <see cref="ReflectionTypeLoadException"/>'s own message only
        /// says to go and look at LoaderExceptions, which is exactly where the useful part - the
        /// name of the assembly or member that could not be found - actually is.
        /// </summary>
        private static string Describe(Exception ex)
        {
            if (!(ex is ReflectionTypeLoadException typeLoad)) return ex.GetType().Name + ": " + ex.Message;

            var reasons = (typeLoad.LoaderExceptions ?? new Exception[0])
                .Where(e => e != null)
                .Select(e => e.Message)
                .Distinct()
                .Take(3)
                .ToList();

            return reasons.Count == 0
                ? "ReflectionTypeLoadException with no loader detail"
                : "ReflectionTypeLoadException: " + string.Join(" | ", reasons);
        }

        /// <summary>True when the resolved factory talks to a real TIA Portal.</summary>
        public static bool IsReal(ITiaSessionFactory factory)
        {
            return factory != null && factory.Mode == SessionMode.Openness;
        }
    }
}
