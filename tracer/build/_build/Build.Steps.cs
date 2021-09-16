using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static CustomDotNetTasks;

// #pragma warning disable SA1306
// #pragma warning disable SA1134
// #pragma warning disable SA1111
// #pragma warning disable SA1400
// #pragma warning disable SA1401

partial class Build
{
    [Solution("Datadog.Trace.sln")] readonly Solution Solution;
    AbsolutePath TracerDirectory => RootDirectory / "tracer";
    AbsolutePath MsBuildProject => TracerDirectory / "Datadog.Trace.proj";

    AbsolutePath OutputDirectory => TracerDirectory / "bin";
    AbsolutePath TracerHomeDirectory => TracerHome ?? (OutputDirectory / "tracer-home");
    AbsolutePath SymbolsDirectory => TracerHome ?? (OutputDirectory / "symbols");
    AbsolutePath DDTracerHomeDirectory => DDTracerHome ?? (OutputDirectory / "dd-tracer-home");
    AbsolutePath ArtifactsDirectory => Artifacts ?? (OutputDirectory / "artifacts");
    AbsolutePath WindowsTracerHomeZip => ArtifactsDirectory / "windows-tracer-home.zip";
    AbsolutePath WindowsSymbolsZip => ArtifactsDirectory / "windows-native-symbols.zip";
    AbsolutePath BuildDataDirectory => TracerDirectory / "build_data";

    const string LibDdwafVersion = "1.0.10";
    AbsolutePath LibDdwafDirectory => (NugetPackageDirectory ?? (RootDirectory / "packages")) / $"libddwaf.{LibDdwafVersion}";

    AbsolutePath SourceDirectory => TracerDirectory / "src";
    AbsolutePath BuildDirectory => TracerDirectory / "build";
    AbsolutePath TestsDirectory => TracerDirectory / "test";
    AbsolutePath DistributionHomeDirectory => Solution.GetProject(Projects.DatadogMonitoringDistribution).Directory / "home";

    AbsolutePath TempDirectory => (AbsolutePath)(IsWin ? Path.GetTempPath() : "/tmp/");
    string TracerLogDirectory => IsWin
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Datadog .NET Tracer", "logs")
        : "/var/log/datadog/dotnet/";

    Project NativeProfilerProject => Solution.GetProject(Projects.ClrProfilerNative);

    [LazyPathExecutable(name: "cmake")] readonly Lazy<Tool> CMake;
    [LazyPathExecutable(name: "make")] readonly Lazy<Tool> Make;
    [LazyPathExecutable(name: "fpm")] readonly Lazy<Tool> Fpm;
    [LazyPathExecutable(name: "gzip")] readonly Lazy<Tool> GZip;
    [LazyPathExecutable(name: "cmd")] readonly Lazy<Tool> Cmd;

    IEnumerable<MSBuildTargetPlatform> ArchitecturesForPlatform =>
        Equals(Platform, MSBuildTargetPlatform.x64)
            ? new[] { MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86 }
            : new[] { MSBuildTargetPlatform.x86 };

    bool IsArm64 => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    string LinuxArchitectureIdentifier => IsArm64 ? "arm64" : Platform.ToString();

    IEnumerable<string> LinuxPackageTypes => IsAlpine ? new[] { "tar" } : new[] { "deb", "rpm", "tar" };

    IEnumerable<Project> ProjectsToPack => new[]
    {
        Solution.GetProject(Projects.DatadogTrace),
        Solution.GetProject(Projects.DatadogTraceOpenTracing),
    };

    Project[] ParallelIntegrationTests => new[]
    {
        Solution.GetProject(Projects.TraceIntegrationTests),
        Solution.GetProject(Projects.OpenTracingIntegrationTests),
    };

    Project[] ClrProfilerIntegrationTests => new[]
    {
        Solution.GetProject(Projects.ClrProfilerIntegrationTests),
        Solution.GetProject(Projects.AppSecIntegrationTests),
    };

    readonly IEnumerable<TargetFramework> TargetFrameworks = new[]
    {
        TargetFramework.NET45,
        TargetFramework.NET461,
        TargetFramework.NETSTANDARD2_0,
        TargetFramework.NETCOREAPP3_1,
    };

    Target CreateRequiredDirectories => _ => _
        .Unlisted()
        .Executes(() =>
        {
            EnsureExistingDirectory(TracerHomeDirectory);
            EnsureExistingDirectory(ArtifactsDirectory);
            EnsureExistingDirectory(DDTracerHomeDirectory);
            EnsureExistingDirectory(BuildDataDirectory);
        });

    Target Restore => _ => _
        .After(Clean)
        .Unlisted()
        .Executes(() =>
        {
            if (IsWin)
            {
                NuGetTasks.NuGetRestore(s => s
                    .SetTargetPath(Solution)
                    .SetVerbosity(NuGetVerbosity.Normal)
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackagesDirectory(NugetPackageDirectory)));
            }
            else
            {
                DotNetRestore(s => s
                    .SetProjectFile(Solution)
                    .SetVerbosity(DotNetVerbosity.Normal)
                    // .SetTargetPlatform(Platform) // necessary to ensure we restore every project
                    .SetProperty("configuration", BuildConfiguration.ToString())
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackageDirectory(NugetPackageDirectory)));
            }
        });

    Target CompileNativeSrcWindows => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            // If we're building for x64, build for x86 too
            var platforms =
                Equals(Platform, MSBuildTargetPlatform.x64)
                    ? new[] { MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86 }
                    : new[] { MSBuildTargetPlatform.x86 };

            // Can't use dotnet msbuild, as needs to use the VS version of MSBuild
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetMSBuildPath()
                .SetTargets("BuildCppSrc")
                .DisableRestore()
                .SetMaxCpuCount(null)
                .CombineWith(platforms, (m, platform) => m
                    .SetTargetPlatform(platform)));
        });

    Target CompileNativeSrcLinux => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            var buildDirectory = NativeProfilerProject.Directory / "build";
            EnsureExistingDirectory(buildDirectory);

            CMake.Value(
                arguments: "../ -DCMAKE_BUILD_TYPE=Release",
                workingDirectory: buildDirectory);
            Make.Value(workingDirectory: buildDirectory);
        });

    Target CompileNativeSrcMacOs => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .OnlyWhenStatic(() => IsOsx)
        .Executes(() =>
        {
            var nativeProjectDirectory = NativeProfilerProject.Directory;
            CMake.Value(arguments: ".", workingDirectory: nativeProjectDirectory);
            Make.Value(workingDirectory: nativeProjectDirectory);
        });

    Target CompileNativeSrc => _ => _
        .Unlisted()
        .Description("Compiles the native loader")
        .DependsOn(CompileNativeSrcWindows)
        .DependsOn(CompileNativeSrcMacOs)
        .DependsOn(CompileNativeSrcLinux);

    Target CompileManagedSrc => _ => _
        .Unlisted()
        .Description("Compiles the managed code in the src directory")
        .After(CreateRequiredDirectories)
        .After(Restore)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetTargetPlatformAnyCPU()
                .SetConfiguration(BuildConfiguration)
                .DisableRestore()
                .SetTargets("BuildCsharpSrc")
            );
        });


    Target CompileNativeTestsWindows => _ => _
        .Unlisted()
        .After(CompileNativeSrc)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            // If we're building for x64, build for x86 too
            var platforms =
                Equals(Platform, MSBuildTargetPlatform.x64)
                    ? new[] { MSBuildTargetPlatform.x64, MSBuildTargetPlatform.x86 }
                    : new[] { MSBuildTargetPlatform.x86 };

            // Can't use dotnet msbuild, as needs to use the VS version of MSBuild
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetMSBuildPath()
                .SetTargets("BuildCppTests")
                .DisableRestore()
                .SetMaxCpuCount(null)
                .CombineWith(platforms, (m, platform) => m
                    .SetTargetPlatform(platform)));
        });

    Target CompileNativeTestsLinux => _ => _
        .Unlisted()
        .After(CompileNativeSrc)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            Logger.Error("We don't currently run unit tests on Linux");
        });

    Target CompileNativeTests => _ => _
        .Unlisted()
        .Description("Compiles the native loader unit tests")
        .DependsOn(CompileNativeTestsWindows)
        .DependsOn(CompileNativeTestsLinux);


    Target CopyIntegrationsJson => _ => _
        .Unlisted()
        .After(Clean)
        .After(CreateRequiredDirectories)
        .Executes(() =>
        {
            var source = TracerDirectory / "integrations.json";
            var dest = TracerHomeDirectory;

            Logger.Info($"Copying '{source}' to '{dest}'");
            CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);
        });

    Target DownloadLibDdwaf => _ => _
        .Unlisted()
        .After(CreateRequiredDirectories)
        .Executes(() =>
        {
            var wc = new WebClient();
            var libDdwafUri = new Uri($"https://www.nuget.org/api/v2/package/libddwaf/{LibDdwafVersion}");
            var libDdwafZip = TempDirectory / "libddwaf.zip";

            wc.DownloadFile(libDdwafUri, libDdwafZip);

            Console.WriteLine($"{libDdwafZip} downloaded. Extracting to {LibDdwafDirectory}...");

            UncompressZip(libDdwafZip, LibDdwafDirectory);
        });

    Target CopyLibDdwaf => _ => _
        .Unlisted()
        .After(Clean)
        .After(DownloadLibDdwaf)
        .OnlyWhenStatic(() => !IsArm64) // not supported yet
        .Executes(() =>
        {
            if (IsWin)
            {
                foreach (var architecture in new[] {"win-x86", "win-x64"})
                {
                    var source = LibDdwafDirectory / "runtimes" / architecture / "native" / "ddwaf.dll";
                    var dest = TracerHomeDirectory / architecture;
                    Logger.Info($"Copying '{source}' to '{dest}'");
                    CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);
                }
            }
            else
            {
                var (architecture, ext) = GetUnixArchitectureAndExtention();
                var ddwafFileName = $"libddwaf.{ext}";

                var source = LibDdwafDirectory / "runtimes" / architecture / "native" / ddwafFileName;
                var dest = TracerHomeDirectory;
                Logger.Info($"Copying '{source}' to '{dest}'");
                CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);

            }
        });

    Target PublishManagedProfiler => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            var targetFrameworks = IsWin
                ? TargetFrameworks
                : TargetFrameworks.Where(framework => !framework.ToString().StartsWith("net4"));

            // Publish Datadog.Trace.MSBuild which includes Datadog.Trace and Datadog.Trace.AspNet
            DotNetPublish(s => s
                .SetProject(Solution.GetProject(Projects.DatadogTraceMsBuild))
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .EnableNoBuild()
                .EnableNoRestore()
                .CombineWith(targetFrameworks, (p, framework) => p
                    .SetFramework(framework)
                    .SetOutput(TracerHomeDirectory / framework)));
        });

    Target PublishNativeSymbolsWindows => _ => _
      .Unlisted()
      .OnlyWhenStatic(() => IsWin)
      .After(CompileNativeSrc, PublishManagedProfiler)
      .Executes(() =>
       {
           foreach (var architecture in ArchitecturesForPlatform)
           {
               var source = NativeProfilerProject.Directory / "bin" / BuildConfiguration / architecture.ToString() /
                            $"{NativeProfilerProject.Name}.pdb";
               var dest = SymbolsDirectory / $"win-{architecture}";
               Logger.Info($"Copying '{source}' to '{dest}'");
               CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);
           }
       });

    Target PublishNativeProfilerWindows => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsWin)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            foreach (var architecture in ArchitecturesForPlatform)
            {
                var source = NativeProfilerProject.Directory / "bin" / BuildConfiguration / architecture.ToString() /
                             $"{NativeProfilerProject.Name}.dll";
                var dest = TracerHomeDirectory / $"win-{architecture}";
                Logger.Info($"Copying '{source}' to '{dest}'");
                CopyFileToDirectory(source, dest, FileExistsPolicy.Overwrite);
            }
        });

    Target PublishNativeProfilerLinux => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsLinux)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            // copy createLogPath.sh
            CopyFileToDirectory(
                BuildDirectory / "artifacts" / "createLogPath.sh",
                TracerHomeDirectory,
                FileExistsPolicy.Overwrite);

            // Copy Native file
            CopyFileToDirectory(
                NativeProfilerProject.Directory / "build" / "bin" / $"{NativeProfilerProject.Name}.so",
                TracerHomeDirectory,
                FileExistsPolicy.Overwrite);
        });

    Target PublishNativeProfilerMacOs => _ => _
        .Unlisted()
        .OnlyWhenStatic(() => IsOsx)
        .After(CompileNativeSrc, PublishManagedProfiler)
        .Executes(() =>
        {
            // copy createLogPath.sh
            CopyFileToDirectory(
                BuildDirectory / "artifacts" / "createLogPath.sh",
                TracerHomeDirectory,
                FileExistsPolicy.Overwrite);

            // Create home directory
            CopyFileToDirectory(
                NativeProfilerProject.Directory / "bin" / $"{NativeProfilerProject.Name}.dylib",
                TracerHomeDirectory,
                FileExistsPolicy.Overwrite);
        });

    Target PublishNativeProfiler => _ => _
        .Unlisted()
        .DependsOn(PublishNativeProfilerWindows)
        .DependsOn(PublishNativeProfilerLinux)
        .DependsOn(PublishNativeProfilerMacOs);

    Target CreateDdTracerHome => _ => _
       .Unlisted()
       .After(PublishNativeProfiler, CopyIntegrationsJson, PublishManagedProfiler, DownloadLibDdwaf, CopyLibDdwaf)
       .Executes(() =>
       {
           // start by copying everything from the tracer home dir
           CopyDirectoryRecursively(TracerHomeDirectory, DDTracerHomeDirectory, DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);

           if (IsWin)
           {
               // windows already has the expected layout
               return;
           }

           // Move the native file to the architecture-specific folder
           var (architecture, ext) = GetUnixArchitectureAndExtention();

           var profilerFileName = $"{NativeProfilerProject.Name}.{ext}";
           var ddwafFileName = $"libddwaf.{ext}";

           var outputDir = DDTracerHomeDirectory / architecture;

           EnsureCleanDirectory(outputDir);
           MoveFile(
               DDTracerHomeDirectory / profilerFileName,
               outputDir / profilerFileName);

           // won't exist yet for arm64 builds
           var srcDdwafFile = DDTracerHomeDirectory / ddwafFileName;
           if (File.Exists(srcDdwafFile))
           {
               MoveFile(
                   srcDdwafFile,
                   DDTracerHomeDirectory / architecture / ddwafFileName);
           }
       });

    Target BuildMsi => _ => _
        .Unlisted()
        .Description("Builds the .msi files from the compiled tracer home directory")
        .After(BuildTracerHome)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            MSBuild(s => s
                    .SetTargetPath(Solution.GetProject(Projects.WindowsInstaller))
                    .SetConfiguration(BuildConfiguration)
                    .SetMSBuildPath()
                    .AddProperty("RunWixToolsOutOfProc", true)
                    .SetProperty("TracerHomeDirectory", TracerHomeDirectory)
                    .SetProperty("LibDdwafDirectory", LibDdwafDirectory)
                    .SetMaxCpuCount(null)
                    .CombineWith(ArchitecturesForPlatform, (o, arch) => o
                        .SetProperty("MsiOutputPath", ArtifactsDirectory / arch.ToString())
                        .SetTargetPlatform(arch)),
                degreeOfParallelism: 2);
        });

    Target CreateDistributionHome => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .Executes(() =>
        {
            // Copy existing files from tracer home to the Distribution location
            CopyDirectoryRecursively(TracerHomeDirectory, DistributionHomeDirectory, DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);

            // Ensure createLogPath.sh is copied to the directory
            CopyFileToDirectory(
                BuildDirectory / "artifacts" / "createLogPath.sh",
                DistributionHomeDirectory,
                FileExistsPolicy.Overwrite);
        });

    /// <summary>
    /// This target is a bit of a hack, but means that we actually use the All CPU builds in intgration tests etc
    /// </summary>
    Target CreatePlatformlessSymlinks => _ => _
        .Description("Copies the build output from 'All CPU' platforms to platform-specific folders")
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileDependencyLibs)
        .After(CompileManagedTestHelpers)
        .Executes(() =>
        {
            // create junction for each directory
            var directories = TracerDirectory.GlobDirectories(
                $"src/**/bin/{BuildConfiguration}",
                $"tools/**/bin/{BuildConfiguration}",
                $"test/Datadog.Trace.TestHelpers/**/bin/{BuildConfiguration}",
                $"test/test-applications/integrations/dependency-libs/**/bin/{BuildConfiguration}"
            );

            directories.ForEach(existingDir =>
            {
                var newDir = existingDir.Parent / $"{Platform}" / BuildConfiguration;
                if (DirectoryExists(newDir))
                {
                    Logger.Info($"Skipping '{newDir}' as already exists");
                }
                else
                {
                    EnsureExistingDirectory(newDir.Parent);
                    Cmd.Value(arguments: $"cmd /c mklink /J \"{newDir}\" \"{existingDir}\"");
                }
            });
        });

    Target ZipSymbols => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .DependsOn(PublishNativeSymbolsWindows)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            CompressZip(SymbolsDirectory, WindowsSymbolsZip, fileMode: FileMode.Create);
        });

    Target ZipTracerHome => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .Requires(() => Version)
        .Executes(() =>
        {
            if (IsWin)
            {
                CompressZip(TracerHomeDirectory, WindowsTracerHomeZip, fileMode: FileMode.Create);
            }
            else if (IsLinux)
            {
                var fpm = Fpm.Value;
                var gzip = GZip.Value;
                var packageName = "datadog-dotnet-apm";

                var workingDirectory = ArtifactsDirectory / $"linux-{LinuxArchitectureIdentifier}";
                EnsureCleanDirectory(workingDirectory);

                foreach (var packageType in LinuxPackageTypes)
                {
                    var args = new List<string>()
                    {
                        "-f",
                        "-s dir",
                        $"-t {packageType}",
                        $"-n {packageName}",
                        $"-v {Version}",
                        packageType == "tar" ? "" : "--prefix /opt/datadog",
                        $"--chdir {TracerHomeDirectory}",
                        "netstandard2.0/",
                        "netcoreapp3.1/",
                        "Datadog.Trace.ClrProfiler.Native.so",
                        "integrations.json",
                        "createLogPath.sh",
                    };

                    if (!IsArm64)
                    {
                        args.Add("libddwaf.so");
                    }

                    var arguments = string.Join(" ", args);
                    fpm(arguments, workingDirectory: workingDirectory);
                }

                gzip($"-f {packageName}.tar", workingDirectory: workingDirectory);


                var suffix = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? string.Empty
                    : $".{RuntimeInformation.ProcessArchitecture.ToString().ToLower()}";

                var versionedName = IsAlpine
                    ? $"{packageName}-{Version}-musl{suffix}.tar.gz"
                    : $"{packageName}-{Version}{suffix}.tar.gz";

                RenameFile(
                    workingDirectory / $"{packageName}.tar.gz",
                    workingDirectory / versionedName);
            }
        });

    Target CompileManagedTestHelpers => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .SetProperty("BuildProjectReferences", false)
                .SetTargets("BuildCsharpTestHelpers"));
        });

    Target CompileManagedUnitTests => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .SetProperty("BuildProjectReferences", false)
                .SetTargets("BuildCsharpUnitTests"));
        });

    Target RunManagedUnitTests => _ => _
        .Unlisted()
        .After(CompileManagedUnitTests)
        .Executes(() =>
        {
            var testProjects = TracerDirectory.GlobFiles("test/**/*.Tests.csproj")
                .Select(x => Solution.GetProject(x))
                .ToList();

            testProjects.ForEach(EnsureResultsDirectory);

            try
            {
                DotNetTest(x => x
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatformAnyCPU()
                    .SetDDEnvironmentVariables("dd-tracer-dotnet")
                    .EnableMemoryDumps()
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(testProjects, (x, project) => x
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    Target RunNativeTestsWindows => _ => _
        .Unlisted()
        .After(CompileNativeSrcWindows)
        .After(CompileNativeTestsWindows)
        .OnlyWhenStatic(() => IsWin)
        .Executes(() =>
        {
            var workingDirectory = TestsDirectory / "Datadog.Trace.ClrProfiler.Native.Tests" / "bin" / BuildConfiguration.ToString() / Platform.ToString();
            var exePath = workingDirectory / "Datadog.Trace.ClrProfiler.Native.Tests.exe";
            var testExe = ToolResolver.GetLocalTool(exePath);
            testExe("--gtest_output=xml", workingDirectory: workingDirectory);
        });

    Target RunNativeTestsLinux => _ => _
        .Unlisted()
        .After(CompileNativeSrcLinux)
        .After(CompileNativeTestsLinux)
        .OnlyWhenStatic(() => IsLinux)
        .Executes(() =>
        {
            Logger.Error("We don't currently run unit tests on Linux");
        });

    Target RunNativeTests => _ => _
        .Unlisted()
        .DependsOn(RunNativeTestsWindows)
        .DependsOn(RunNativeTestsLinux);

    Target CompileDependencyLibs => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // Always AnyCPU
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .EnableNoDependencies()
                .SetTargets("BuildDependencyLibs")
            );
        });

    Target CompileRegressionDependencyLibs => _ => _
        .Unlisted()
        .After(Restore)
        .After(CompileManagedSrc)
        .Executes(() =>
        {
            // We run linux integration tests in AnyCPU, but Windows on the specific architecture
            var platform = IsLinux ? MSBuildTargetPlatform.MSIL : Platform;

            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetTargetPlatformAnyCPU()
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(platform)
                .SetTargets("BuildRegressionDependencyLibs")
            );
        });

    Target CompileRegressionSamples => _ => _
        .Unlisted()
        .After(Restore)
        .After(CreatePlatformlessSymlinks)
        .After(CompileRegressionDependencyLibs)
        .Executes(() =>
        {
            var regressionsDirectory = Solution.GetProject(Projects.EntityFramework6xMdTokenLookupFailure)
                .Directory.Parent;
            var regressionLibs = GlobFiles(regressionsDirectory / "**" / "*.csproj")
                .Where(x => !x.Contains("EntityFramework6x.MdTokenLookupFailure")
                            && !x.Contains("ExpenseItDemo")
                            && !x.Contains("StackExchange.Redis.AssemblyConflict.LegacyProject")
                            && !x.Contains("MismatchedTracerVersions")
                            && !x.Contains("dependency-libs"));

            // Allow restore here, otherwise things go wonky with runtime identifiers
            // in some target frameworks. No, I don't know why
            DotNetBuild(x => x
                // .EnableNoRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetNoWarnDotNetCore3()
                .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                    o.SetPackageDirectory(NugetPackageDirectory))
                .CombineWith(regressionLibs, (x, project) => x
                    .SetProjectFile(project)));
        });

    Target CompileFrameworkReproductions => _ => _
        .Unlisted()
        .Description("Builds .NET Framework projects (non SDK-based projects)")
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CreatePlatformlessSymlinks)
        .Requires(() => IsWin)
        .Executes(() =>
        {
            // We have to use the full MSBuild here, as dotnet msbuild doesn't copy the EDMX assets for embedding correctly
            // seems similar to https://github.com/dotnet/sdk/issues/8360
            MSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .SetMSBuildPath()
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetTargets("BuildFrameworkReproductions")
                .SetMaxCpuCount(null));
        });

    Target CompileIntegrationTests => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionSamples)
        .After(CompileFrameworkReproductions)
        .After(PublishIisSamples)
        .Requires(() => TracerHomeDirectory != null)
        .Executes(() =>
        {
            DotNetMSBuild(s => s
                .SetTargetPath(MsBuildProject)
                .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetTargets("BuildCsharpIntegrationTests")
                .SetMaxCpuCount(null));
        });

    Target CompileSamples => _ => _
        .Unlisted()
        .After(CompileDependencyLibs)
        .After(CreatePlatformlessSymlinks)
        .After(CompileFrameworkReproductions)
        .Requires(() => TracerHomeDirectory != null)
        .Executes(() =>
        {
            // This does some "unnecessary" rebuilding and restoring
            var includeIntegration = TracerDirectory.GlobFiles("test/test-applications/integrations/**/*.csproj");
            // Don't build aspnet full framework sample in this step
            var includeSecurity = TracerDirectory.GlobFiles("test/test-applications/security/*/*.csproj");

            var exclude = TracerDirectory.GlobFiles("test/test-applications/integrations/dependency-libs/**/*.csproj");

            var projects =  includeIntegration
                .Concat(includeSecurity)
                .Where(projectPath =>
                projectPath switch
                {
                    _ when exclude.Contains(projectPath) => false,
                    _ when projectPath.ToString().Contains("Samples.OracleMDA") => false,
                    _ when !string.IsNullOrWhiteSpace(SampleName) => projectPath.ToString().Contains(SampleName),
                     _ => true,
                }
            );

            // /nowarn:NU1701 - Package 'x' was restored using '.NETFramework,Version=v4.6.1' instead of the project target framework '.NETCoreApp,Version=v2.1'.
            DotNetBuild(config => config
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .EnableNoDependencies()
                .SetProperty("BuildInParallel", "false")
                .SetProperty("ExcludeManagedProfiler", true)
                .SetProperty("ExcludeNativeProfiler", true)
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetProperty("LoadManagedProfilerFromProfilerDirectory", false)
                .SetProcessArgumentConfigurator(arg => arg.Add("/nowarn:NU1701"))
                .CombineWith(projects, (s, project) => s
                    .SetProjectFile(project)));
        });

    Target PublishIisSamples => _ => _
        .Unlisted()
        .After(CompileManagedTestHelpers)
        .After(CompileRegressionSamples)
        .After(CompileFrameworkReproductions)
        .Executes(() =>
        {
            var aspnetFolder = TestsDirectory / "test-applications" / "aspnet";
            var securityAspnetFolder = TestsDirectory / "test-applications" / "security" / "aspnet";

            var aspnetProjects = aspnetFolder.GlobFiles("**/*.csproj");
            var securityAspnetProjects = securityAspnetFolder.GlobFiles("**/*.csproj");

            var publishProfile = aspnetFolder / "PublishProfiles" / "FolderProfile.pubxml";

            MSBuild(x => x
                .SetMSBuildPath()
                // .DisableRestore()
                .EnableNoDependencies()
                .SetConfiguration(BuildConfiguration)
                .SetTargetPlatform(Platform)
                .SetProperty("DeployOnBuild", true)
                .SetProperty("PublishProfile", publishProfile)
                .SetMaxCpuCount(null)
                .CombineWith(aspnetProjects.Concat(securityAspnetProjects), (c, project) => c
                    .SetTargetPath(project))
            );
        });

    Target RunWindowsIntegrationTests => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .After(CompileIntegrationTests)
        .After(CompileSamples)
        .After(CompileFrameworkReproductions)
        .After(BuildWindowsIntegrationTests)
        .Requires(() => IsWin)
        .Executes(() =>
        {
            ParallelIntegrationTests.ForEach(EnsureResultsDirectory);
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);

            try
            {
                DotNetTest(config => config
                    .SetDotnetPath(Platform)
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .When(!string.IsNullOrEmpty(Filter), c => c.SetFilter(Filter))
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(ParallelIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)), degreeOfParallelism: 4);


                // TODO: I think we should change this filter to run on Windows by default
                // (RunOnWindows!=False|Category=Smoke)&LoadFromGAC!=True&IIS!=True
                DotNetTest(config => config
                    .SetDotnetPath(Platform)
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter(Filter ?? "RunOnWindows=True&LoadFromGAC!=True&IIS!=True")
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    Target RunWindowsRegressionTests => _ => _
        .Unlisted()
        .After(BuildTracerHome)
        .After(CompileIntegrationTests)
        .After(CompileRegressionSamples)
        .After(CompileFrameworkReproductions)
        .Requires(() => IsWin)
        .Executes(() =>
        {
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);

            try
            {
                DotNetTest(config => config
                    .SetDotnetPath(Platform)
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter(Filter ?? "Category=Smoke&LoadFromGAC!=True")
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });


    Target RunWindowsIisIntegrationTests => _ => _
        .After(BuildTracerHome)
        .After(CompileIntegrationTests)
        .After(CompileSamples)
        .After(CompileFrameworkReproductions)
        .After(PublishIisSamples)
        .Executes(() =>
        {
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);
            try
            {
                // Different filter from RunWindowsIntegrationTests
                DotNetTest(config => config
                    .SetDotnetPath(Platform)
                    .SetConfiguration(BuildConfiguration)
                    .SetTargetPlatform(Platform)
                    .When(Framework != null, o => o.SetFramework(Framework))
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFilter(Filter ?? "(RunOnWindows=True)&LoadFromGAC=True")
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project)));
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    Target CompileSamplesLinux => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedTestHelpers)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // There's nothing specifically linux-y here, it's just that we only build a subset of projects
            // for testing on linux.
            var sampleProjects = TracerDirectory.GlobFiles("test/test-applications/integrations/*/*.csproj");
            var securitySampleProjects = TracerDirectory.GlobFiles("test/test-applications/security/*/*.csproj");
            var regressionProjects = TracerDirectory.GlobFiles("test/test-applications/regression/*/*.csproj");
            var instrumentationProjects = TracerDirectory.GlobFiles("test/test-applications/instrumentation/*/*.csproj");

            // These samples are currently skipped.
            var projectsToSkip = new[]
            {
                "Samples.Msmq",  // Doesn't run on Linux
                "Samples.Owin.WebApi2", // Doesn't run on Linux
                "Samples.MultiDomainHost.Runner",
                "Samples.RateLimiter", // I think we _should_ run this one (assuming it has tests)
                "Samples.SqlServer.NetFramework20",
                "Samples.TracingWithoutLimits", // I think we _should_ run this one (assuming it has tests)
                "Samples.Wcf",
                "Samples.WebRequest.NetFramework20",
                "AutomapperTest", // I think we _should_ run this one (assuming it has tests)
                "DogStatsD.RaceCondition",
                "EntityFramework6x.MdTokenLookupFailure",
                "LargePayload", // I think we _should_ run this one (assuming it has tests)
                "Sandbox.ManualTracing",
                "StackExchange.Redis.AssemblyConflict.LegacyProject",
                "Samples.OracleMDA", // We don't test these yet
                "Samples.OracleMDA.Core", // We don't test these yet
                "MismatchedTracerVersions",
            };

            // These sample projects are built using RestoreAndBuildSamplesForPackageVersions
            // so no point building them now
            List<string> multiPackageProjects;
            var samplesFile = BuildDirectory / "PackageVersionsGeneratorDefinitions.json";
            using (var fs = File.OpenRead(samplesFile))
            {
                var json = JsonDocument.Parse(fs);
                multiPackageProjects = json.RootElement
                                       .EnumerateArray()
                                       .Select(e => e.GetProperty("SampleProjectName").GetString())
                                       .Distinct()
                                       .Where(name => name switch
                                        {
                                            "Samples.MySql" => false, // the "non package version" is _ALSO_ tested separately
                                            _ => true
                                        })
                                       .ToList();
            }

            var projectsToBuild = sampleProjects
                .Concat(securitySampleProjects)
                .Concat(regressionProjects)
                .Concat(instrumentationProjects)
                .Where(path =>
                {
                    var project = Solution.GetProject(path);
                    return project?.Name switch
                    {
                        "Samples.AspNetCoreMvc21" => Framework == TargetFramework.NETCOREAPP2_1,
                        "Samples.AspNetCoreMvc30" => Framework == TargetFramework.NETCOREAPP3_0,
                        "Samples.AspNetCoreMvc31" => Framework == TargetFramework.NETCOREAPP3_1,
                        "Samples.AspNetCore2" => Framework == TargetFramework.NETCOREAPP2_1,
                        "Samples.AspNetCore5" => Framework == TargetFramework.NET5_0 || Framework == TargetFramework.NETCOREAPP3_1 || Framework == TargetFramework.NETCOREAPP3_0,
                        "Samples.GraphQL4" => Framework == TargetFramework.NETCOREAPP3_1 || Framework == TargetFramework.NET5_0,
                        var name when projectsToSkip.Contains(name) => false,
                        var name when multiPackageProjects.Contains(name) => false,
                        _ when !string.IsNullOrWhiteSpace(SampleName) => project?.Name?.Contains(SampleName) ?? false,
                        _ => true,
                    };
                });

            // do the build and publish separately to avoid dependency issues

            // Always AnyCPU
            DotNetBuild(x => x
                    // .EnableNoRestore()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .SetProperty("ExcludeManagedProfiler", "true")
                    .SetProperty("ExcludeNativeProfiler", "true")
                    .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o => o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(projectsToBuild, (c, project) => c
                        .SetProjectFile(project)));

            // Always AnyCPU
            DotNetPublish(x => x
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o => o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(projectsToBuild, (c, project) => c
                        .SetProject(project)));

        });

    Target CompileMultiApiPackageVersionSamples => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedTestHelpers)
        .After(CompileSamplesLinux)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // Build and restore for all versions
            // Annoyingly this rebuilds everything again and again.
            var targets = new[] { "RestoreSamplesForPackageVersionsOnly", "RestoreAndBuildSamplesForPackageVersionsOnly" };

            // /nowarn:NU1701 - Package 'x' was restored using '.NETFramework,Version=v4.6.1' instead of the project target framework '.NETCoreApp,Version=v2.1'.
            DotNetMSBuild(x => x
                .SetTargetPath(MsBuildProject)
                .SetConfiguration(BuildConfiguration)
                .EnableNoDependencies()
                .SetProperty("TargetFramework", Framework.ToString())
                .SetProperty("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                .SetProperty("BuildInParallel", "true")
                .SetProperty("ExcludeManagedProfiler", "true")
                .SetProperty("ExcludeNativeProfiler", "true")
                .SetProcessArgumentConfigurator(arg => arg.Add("/nowarn:NU1701"))
                .AddProcessEnvironmentVariable("TestAllPackageVersions", "true")
                .When(TestAllPackageVersions, o => o.SetProperty("TestAllPackageVersions", "true"))
                .CombineWith(targets, (c, target) => c.SetTargets(target))
            );
        });

    Target CompileLinuxIntegrationTests => _ => _
        .Unlisted()
        .After(CompileManagedSrc)
        .After(CompileRegressionDependencyLibs)
        .After(CompileDependencyLibs)
        .After(CompileManagedTestHelpers)
        .After(CompileSamplesLinux)
        .After(CompileMultiApiPackageVersionSamples)
        .Requires(() => TracerHomeDirectory != null)
        .Requires(() => Framework)
        .Executes(() =>
        {
            // Build the actual integration test projects for Any CPU
            var integrationTestProjects = TracerDirectory.GlobFiles("test/*.IntegrationTests/*.csproj");
            DotNetBuild(x => x
                    // .EnableNoRestore()
                    .EnableNoDependencies()
                    .SetConfiguration(BuildConfiguration)
                    .SetFramework(Framework)
                    // .SetTargetPlatform(Platform)
                    .SetNoWarnDotNetCore3()
                    .When(TestAllPackageVersions, o => o
                        .SetProperty("TestAllPackageVersions", "true"))
                    .AddProcessEnvironmentVariable("TestAllPackageVersions", "true")
                    .AddProcessEnvironmentVariable("ManagedProfilerOutputDirectory", TracerHomeDirectory)
                    .When(!string.IsNullOrEmpty(NugetPackageDirectory), o =>
                        o.SetPackageDirectory(NugetPackageDirectory))
                    .CombineWith(integrationTestProjects, (c, project) => c
                        .SetProjectFile(project)));

            IntegrationTestLinuxProfilerDirFudge(Projects.ClrProfilerIntegrationTests);
            IntegrationTestLinuxProfilerDirFudge(Projects.AppSecIntegrationTests);
        });

    Target RunLinuxIntegrationTests => _ => _
        .After(CompileLinuxIntegrationTests)
        .Description("Runs the linux integration tests")
        .Requires(() => Framework)
        .Requires(() => IsLinux)
        .Executes(() =>
        {
            ParallelIntegrationTests.ForEach(EnsureResultsDirectory);
            ClrProfilerIntegrationTests.ForEach(EnsureResultsDirectory);


            var filter = (string.IsNullOrEmpty(Filter), IsArm64) switch
            {
                (true, false) => "Category!=LinuxUnsupported",
                (true, true) => "(Category!=ArmUnsupported)&(Category!=LinuxUnsupported)",
                _ => Filter
            };

            try
            {
                // Run these ones in parallel
                // Always AnyCPU
                DotNetTest(config => config
                        .SetConfiguration(BuildConfiguration)
                        // .SetTargetPlatform(Platform)
                        .EnableNoRestore()
                        .EnableNoBuild()
                        .SetFramework(Framework)
                        .EnableMemoryDumps()
                        .SetFilter(filter)
                        .When(TestAllPackageVersions, o => o
                            .SetProcessEnvironmentVariable("TestAllPackageVersions", "true"))
                        .When(CodeCoverage, ConfigureCodeCoverage)
                        .CombineWith(ParallelIntegrationTests, (s, project) => s
                            .EnableTrxLogOutput(GetResultsDirectory(project))
                            .SetProjectFile(project)),
                    degreeOfParallelism: 2);

                // Run this one separately so we can tail output
                DotNetTest(config => config
                    .SetConfiguration(BuildConfiguration)
                    // .SetTargetPlatform(Platform)
                    .EnableNoRestore()
                    .EnableNoBuild()
                    .SetFramework(Framework)
                    .EnableMemoryDumps()
                    .SetFilter(filter)
                    .When(TestAllPackageVersions, o => o
                        .SetProcessEnvironmentVariable("TestAllPackageVersions", "true"))
                    .When(CodeCoverage, ConfigureCodeCoverage)
                    .CombineWith(ClrProfilerIntegrationTests, (s, project) => s
                        .EnableTrxLogOutput(GetResultsDirectory(project))
                        .SetProjectFile(project))
                );
            }
            finally
            {
                MoveLogsToBuildData();
            }
        });

    private AbsolutePath GetResultsDirectory(Project proj) => BuildDataDirectory / "results" / proj.Name;

    private void EnsureResultsDirectory(Project proj) => EnsureCleanDirectory(GetResultsDirectory(proj));

    private (string, string) GetUnixArchitectureAndExtention()
    {
        var archExt = IsOsx
            ? ("osx-x64", "dylib")
            : ($"linux-{LinuxArchitectureIdentifier}", "so");

        return archExt;
    }

    // the integration tests need their own copy of the profiler, this achived through build.props on Windows, but doesn't seem to work under Linux
    private void IntegrationTestLinuxProfilerDirFudge(string project)
    {
            // Not sure if/why this is necessary, and we can't just point to the correct output location
            var src = TracerHomeDirectory;
            var testProject = Solution.GetProject(project).Directory;
            var dest = testProject / "bin" / BuildConfiguration / Framework / "profiler-lib";
            CopyDirectoryRecursively(src, dest, DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);

            // not sure exactly where this is supposed to go, may need to change the original build
            foreach (var linuxDir in TracerHomeDirectory.GlobDirectories("linux-*"))
            {
                CopyDirectoryRecursively(linuxDir, dest, DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);
            }
    }

    private void MoveLogsToBuildData()
    {
        if (Directory.Exists(TracerLogDirectory))
        {
            CopyDirectoryRecursively(TracerLogDirectory, BuildDataDirectory / "logs",
                DirectoryExistsPolicy.Merge, FileExistsPolicy.Overwrite);
        }

        if (Directory.Exists(TempDirectory))
        {
            foreach (var dump in GlobFiles(TempDirectory, "coredump*"))
            {
                MoveFileToDirectory(dump, BuildDataDirectory / "dumps", FileExistsPolicy.Overwrite);
            }
        }
    }

    private DotNetTestSettings ConfigureCodeCoverage(DotNetTestSettings settings)
    {
        var strongNameKeyPath = Solution.Directory / "Datadog.Trace.snk";

        return settings.SetDataCollector("XPlat Code Coverage")
                .SetProcessArgumentConfigurator(
                     args =>
                         args.Add("--")
                             .Add("RunConfiguration.DisableAppDomain=true") // https://github.com/coverlet-coverage/coverlet/issues/347
                             .Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.SkipAutoProps=true")
                             .Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura")
                             .Add($"DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.StrongNameKey=\"{strongNameKeyPath}\"")
                             .Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile=\"**/NuGet/**/LibLog/**/*.cs\",")
                             .Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude=\"[*]Datadog.Trace.Vendors.*,[Datadog.Trace]System.*,[Datadog.Trace]Mono.*\",")
                             .Add("DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=\"[Datadog.Trace.ClrProfiler.*]*,[Datadog.Trace]*,[Datadog.Trace.AspNet]*\""));
    }

    protected override void OnTargetStart(string target)
    {
        if (PrintDriveSpace)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                Logger.Info($"Drive space available on '{drive.Name}': {PrettyPrint(drive.AvailableFreeSpace)} / {PrettyPrint(drive.TotalSize)}");
            }
        }
        base.OnTargetStart(target);

        static string PrettyPrint(long bytes)
        {
            var power = Math.Min((int)Math.Log(bytes, 1000), 4);
            var normalised = bytes / Math.Pow(1000, power);
            return power switch
            {
                4 => $"{normalised:F}TB",
                3 => $"{normalised:F}GB",
                2 => $"{normalised:F}MB",
                1 => $"{normalised:F}KB",
                _ => $"{bytes}B",
            };
        }
    }
}