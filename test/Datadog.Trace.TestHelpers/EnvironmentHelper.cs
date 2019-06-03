using System;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit.Abstractions;

namespace Datadog.Trace.TestHelpers
{
    public class EnvironmentHelper
    {
        public const string ProfilerClsId = "{846F5F1C-F9AE-4B07-969E-05C26BC060D8}";
        public const string DotNetFramework = ".NETFramework";
        public const string CoreFramework = ".NETCoreApp";

        private static readonly Assembly EntryAssembly = Assembly.GetEntryAssembly();
        private static readonly Assembly ExecutingAssembly = Assembly.GetExecutingAssembly();
        private static readonly string RuntimeFrameworkDescription = RuntimeInformation.FrameworkDescription.ToLower();

        private readonly ITestOutputHelper _output;
        private readonly int _major;
        private readonly int _minor;
        private readonly string _patch = null;

        private readonly string _runtime;
        private readonly bool _isCoreClr;
        private readonly string _samplesDirectory;
        private readonly string _disabledIntegrations;
        private readonly Type _anchorType;
        private readonly Assembly _anchorAssembly;
        private readonly TargetFrameworkAttribute _targetFramework;

        private string _integrationsFileLocation;
        private string _profilerFileLocation;

        public EnvironmentHelper(
            string sampleName,
            Type anchorType,
            ITestOutputHelper output,
            string samplesDirectory = "samples",
            string disabledIntegrations = null)
        {
            SampleName = sampleName;
            _samplesDirectory = samplesDirectory ?? "samples";
            _disabledIntegrations = disabledIntegrations;
            _anchorType = anchorType;
            _anchorAssembly = Assembly.GetAssembly(_anchorType);
            _targetFramework = _anchorAssembly.GetCustomAttribute<TargetFrameworkAttribute>();
            _output = output;

            var parts = _targetFramework.FrameworkName.Split(',');
            _runtime = parts[0];
            _isCoreClr = _runtime.Equals(CoreFramework);

            var versionParts = parts[1].Replace("Version=v", string.Empty).Split('.');
            _major = int.Parse(versionParts[0]);
            _minor = int.Parse(versionParts[1]);

            if (versionParts.Length == 3)
            {
                _patch = versionParts[2];
            }
        }

        public string SampleName { get; }

        public static string GetExecutingAssembly()
        {
            return ExecutingAssembly.Location;
        }

        public static string GetOS()
        {
            return IsWindows()                                       ? "win" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "osx" :
                                                                       string.Empty;
        }

        public static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public static string GetPlatform()
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }

        public static string GetBuildConfiguration()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        public static bool IsCoreClr()
        {
            return RuntimeFrameworkDescription.Contains("core");
        }

        public static string GetRuntimeIdentifier()
        {
            return IsCoreClr()
                       ? string.Empty
                       : $"{EnvironmentHelper.GetOS()}-{GetPlatform()}";
        }

        public static string GetSolutionDirectory()
        {
            string currentDirectory = Environment.CurrentDirectory;

            int index = currentDirectory.Replace('\\', '/')
                                        .LastIndexOf("/test/", StringComparison.OrdinalIgnoreCase);

            return currentDirectory.Substring(0, index);
        }

        public static void ClearProfilerEnvironmentVariables()
        {
            var environmentVariables = new[]
            {
                // .NET Core
                "CORECLR_ENABLE_PROFILING",
                "CORECLR_PROFILER",
                "CORECLR_PROFILER_PATH",
                "CORECLR_PROFILER_PATH_32",
                "CORECLR_PROFILER_PATH_64",

                // .NET Framework
                "COR_ENABLE_PROFILING",
                "COR_PROFILER",
                "COR_PROFILER_PATH",

                // Datadog
                "DD_PROFILER_PROCESSES",
                "DD_INTEGRATIONS",
                "DD_DISABLED_INTEGRATIONS",
                "DATADOG_PROFILER_PROCESSES",
                "DATADOG_INTEGRATIONS",
            };

            foreach (string variable in environmentVariables)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }

        public void SetEnvironmentVariableDefaults(
            int agentPort,
            string processPath,
            StringDictionary environmentVariables)
        {
            if (IsCoreClr())
            {
                environmentVariables["CORECLR_ENABLE_PROFILING"] = "1";
                environmentVariables["CORECLR_PROFILER"] = EnvironmentHelper.ProfilerClsId;
                environmentVariables["CORECLR_PROFILER_PATH"] = GetProfilerPath();
            }
            else
            {
                environmentVariables["COR_ENABLE_PROFILING"] = "1";
                environmentVariables["COR_PROFILER"] = EnvironmentHelper.ProfilerClsId;
                environmentVariables["COR_PROFILER_PATH"] = GetProfilerPath();
            }

            environmentVariables["DD_PROFILER_PROCESSES"] = processPath;

            string integrations = string.Join(";", GetIntegrationsFilePaths());
            environmentVariables["DD_INTEGRATIONS"] = integrations;
            environmentVariables["DD_TRACE_AGENT_HOSTNAME"] = "localhost";
            environmentVariables["DD_TRACE_AGENT_PORT"] = agentPort.ToString();

            // for ASP.NET Core sample apps, set the server's port
            environmentVariables["ASPNETCORE_URLS"] = $"http://localhost:{agentPort}/";

            foreach (var name in new string[] { "REDIS_HOST" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    environmentVariables[name] = value;
                }
            }

            if (_disabledIntegrations != null)
            {
                environmentVariables["DD_DISABLED_INTEGRATIONS"] = _disabledIntegrations;
            }
        }

        public string[] GetIntegrationsFilePaths()
        {
            if (_integrationsFileLocation == null)
            {
                string fileName = "integrations.json";

                var directory = GetSampleApplicationOutputDirectory();

                var relativePath = Path.Combine(
                    "profiler-lib",
                    fileName);

                _integrationsFileLocation = Path.Combine(
                    directory,
                    relativePath);

                // TODO: get rid of the fallback options when we have a consistent convention

                if (!File.Exists(_integrationsFileLocation))
                {
                    _output.WriteLine($"Attempt 1: Unable to find integrations at {_integrationsFileLocation}.");
                    // Let's try the executing directory, as dotnet publish ignores the Copy attributes we currently use
                    _integrationsFileLocation = Path.Combine(
                        GetExecutingProjectBin(),
                        relativePath);
                }

                if (!File.Exists(_integrationsFileLocation))
                {
                    _output.WriteLine($"Attempt 2: Unable to find integrations at {_integrationsFileLocation}.");
                    // One last attempt at the solution root
                    _integrationsFileLocation = Path.Combine(
                        GetSolutionDirectory(),
                        fileName);
                }

                if (!File.Exists(_integrationsFileLocation))
                {
                    throw new Exception($"Attempt 3: Unable to find integrations at {_integrationsFileLocation}");
                }

                _output.WriteLine($"Found integrations at {_integrationsFileLocation}.");
            }

            return new[]
            {
                _integrationsFileLocation
            };
        }

        public string GetProfilerPath()
        {
            if (_profilerFileLocation == null)
            {
                string extension = IsWindows()
                                       ? "dll"
                                       : "so";

                string fileName = $"Datadog.Trace.ClrProfiler.Native.{extension}";

                var directory = GetSampleApplicationOutputDirectory();

                var relativePath = Path.Combine(
                    "profiler-lib",
                    fileName);

                _profilerFileLocation = Path.Combine(
                    directory,
                    relativePath);

                // TODO: get rid of the fallback options when we have a consistent convention

                if (!File.Exists(_profilerFileLocation))
                {
                    _output.WriteLine($"Attempt 1: Unable to find profiler at {_profilerFileLocation}.");
                    // Let's try the executing directory, as dotnet publish ignores the Copy attributes we currently use
                    _profilerFileLocation = Path.Combine(
                        GetExecutingProjectBin(),
                        relativePath);
                }

                if (!File.Exists(_profilerFileLocation))
                {
                    _output.WriteLine($"Attempt 2: Unable to find profiler at {_profilerFileLocation}.");
                    // One last attempt at the actual native project directory
                    _profilerFileLocation = Path.Combine(
                        GetProfilerProjectBin(),
                        fileName);
                }

                if (!File.Exists(_profilerFileLocation))
                {
                    throw new Exception($"Attempt 3: Unable to find profiler at {_profilerFileLocation}");
                }

                _output.WriteLine($"Found profiler at {_profilerFileLocation}.");
            }

            return _profilerFileLocation;
        }

        public string GetSampleApplicationPath()
        {
            string extension = "exe";

            if (EnvironmentHelper.IsCoreClr() || _samplesDirectory.Contains("aspnet"))
            {
                extension = "dll";
            }

            var appFileName = $"Samples.{SampleName}.{extension}";
            var sampleAppPath = Path.Combine(GetSampleApplicationOutputDirectory(), appFileName);
            return sampleAppPath;
        }

        public string GetSampleExecutionSource()
        {
            string executor;

            if (_samplesDirectory.Contains("aspnet"))
            {
                executor = $"C:\\Program Files{(Environment.Is64BitProcess ? string.Empty : " (x86)")}\\IIS Express\\iisexpress.exe";
            }
            else if (EnvironmentHelper.IsCoreClr())
            {
                executor = IsWindows() ? "dotnet.exe" : "dotnet";
            }
            else
            {
                var appFileName = $"Samples.{SampleName}.exe";
                executor = Path.Combine(GetSampleApplicationOutputDirectory(), appFileName);

                if (!File.Exists(executor))
                {
                    throw new Exception($"Unable to find executing assembly at {executor}");
                }
            }

            return executor;
        }

        public string GetSampleProjectDirectory()
        {
            var solutionDirectory = GetSolutionDirectory();
            var projectDir = Path.Combine(
                solutionDirectory,
                _samplesDirectory,
                $"Samples.{SampleName}");
            return projectDir;
        }

        public string GetSampleApplicationOutputDirectory()
        {
            var binDir = Path.Combine(
                GetSampleProjectDirectory(),
                "bin");

            string outputDir;

            if (_samplesDirectory.Contains("aspnet"))
            {
                outputDir = binDir;
            }
            else if (EnvironmentHelper.GetOS() == "win")
            {
                outputDir = Path.Combine(
                    binDir,
                    GetPlatform(),
                    GetBuildConfiguration(),
                    GetTargetFramework(),
                    EnvironmentHelper.GetRuntimeIdentifier());
            }
            else
            {
                outputDir = Path.Combine(
                    binDir,
                    GetBuildConfiguration(),
                    GetTargetFramework(),
                    EnvironmentHelper.GetRuntimeIdentifier(),
                    "publish");
            }

            return outputDir;
        }

        public string GetTargetFramework()
        {
            if (_isCoreClr)
            {
                return $"netcoreapp{_major}.{_minor}";
            }

            return $"net{_major}{_minor}{_patch ?? string.Empty}";
        }

        private string GetProfilerProjectBin()
        {
            return Path.Combine(
                GetSolutionDirectory(),
                "src",
                "Datadog.Trace.ClrProfiler.Native",
                "bin",
                GetBuildConfiguration(),
                GetPlatform().ToLower());
        }

        private string GetExecutingProjectBin()
        {
            return Path.GetDirectoryName(ExecutingAssembly.Location);
        }
    }
}