// Copyright Epic Games, Inc. All Rights Reserved.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.IO;
using System.Security.Cryptography;
using AutomationTool;
using UnrealBuildTool;
using EpicGames.Localization;
using Tools.DotNETCommon;

[Help("Updates the external localization data using the arguments provided.")]
[Help("UEProjectRoot", "Optional root-path to the project we're gathering for (defaults to CmdEnv.LocalRoot if unset).")]
[Help("UEProjectDirectory", "Sub-path to the project we're gathering for (relative to UEProjectRoot).")]
[Help("UEProjectName", "Optional name of the project we're gathering for (should match its .uproject file, eg QAGame).")]
[Help("LocalizationProjectNames", "Comma separated list of the projects to gather text from.")]
[Help("LocalizationBranch", "Optional suffix to use when uploading the new data to the localization provider.")]
[Help("LocalizationProvider", "Optional localization provide override.")]
[Help("LocalizationSteps", "Optional comma separated list of localization steps to perform [Download, Gather, Import, Export, Compile, GenerateReports, Upload] (default is all). Only valid for projects using a modular config.")]
[Help("IncludePlugins", "Optional flag to include plugins from within the given UEProjectDirectory as part of the gather. This may optionally specify a comma separated list of the specific plugins to gather (otherwise all plugins will be gathered).")]
[Help("ExcludePlugins", "Optional comma separated list of plugins to exclude from the gather.")]
[Help("IncludePlatforms", "Optional flag to include platforms from within the given UEProjectDirectory as part of the gather.")]
[Help("AdditionalCommandletArguments", "Optional arguments to pass to the gather process.")]
[Help("ParallelGather", "Run the gather processes for a single batch in parallel rather than sequence.")]
class Localize : BuildCommand
{
	private class LocalizationBatch
	{
		public LocalizationBatch(string InUEProjectDirectory, string InLocalizationTargetDirectory, string InRemoteFilenamePrefix, IReadOnlyList<string> InLocalizationProjectNames)
		{
			UEProjectDirectory = InUEProjectDirectory;
			LocalizationTargetDirectory = InLocalizationTargetDirectory;
			RemoteFilenamePrefix = InRemoteFilenamePrefix;
			LocalizationProjectNames = InLocalizationProjectNames;
		}

		public string UEProjectDirectory { get; private set; }
		public string LocalizationTargetDirectory { get; private set; }
		public string RemoteFilenamePrefix { get; private set; }
		public IReadOnlyList<string> LocalizationProjectNames { get; private set; }
	};

	private class LocalizationTask
	{
		public LocalizationTask(LocalizationBatch InBatch, string InUEProjectRoot, string InLocalizationProviderName, int InPendingChangeList, BuildCommand InCommand)
		{
			Batch = InBatch;
			RootWorkingDirectory = CombinePaths(InUEProjectRoot, Batch.UEProjectDirectory);
			RootLocalizationTargetDirectory = CombinePaths(InUEProjectRoot, Batch.LocalizationTargetDirectory);

			// Try and find our localization provider
			{
				LocalizationProvider.LocalizationProviderArgs LocProviderArgs;
				LocProviderArgs.RootWorkingDirectory = RootWorkingDirectory;
				LocProviderArgs.RootLocalizationTargetDirectory = RootLocalizationTargetDirectory;
				LocProviderArgs.RemoteFilenamePrefix = Batch.RemoteFilenamePrefix;
				LocProviderArgs.Command = InCommand;
				LocProviderArgs.PendingChangeList = InPendingChangeList;
				LocProvider = LocalizationProvider.GetLocalizationProvider(InLocalizationProviderName, LocProviderArgs);
			}
		}

		public LocalizationBatch Batch;
		public string RootWorkingDirectory;
		public string RootLocalizationTargetDirectory;
		public LocalizationProvider LocProvider = null;
		public List<ProjectInfo> ProjectInfos = new List<ProjectInfo>();
		public List<IProcessResult> GatherProcessResults = new List<IProcessResult>();
	};

	public override void ExecuteBuild()
	{
		var UEProjectRoot = ParseParamValue("UEProjectRoot");
		if (UEProjectRoot == null)
		{
			UEProjectRoot = CmdEnv.LocalRoot;
		}

		var UEProjectDirectory = ParseParamValue("UEProjectDirectory");
		if (UEProjectDirectory == null)
		{
			throw new AutomationException("Missing required command line argument: 'UEProjectDirectory'");
		}

		var UEProjectName = ParseParamValue("UEProjectName");
		if (UEProjectName == null)
		{
			UEProjectName = "";
		}

		var LocalizationProjectNames = new List<string>();
		{
			var LocalizationProjectNamesStr = ParseParamValue("LocalizationProjectNames");
			if (LocalizationProjectNamesStr != null)
			{
				foreach (var ProjectName in LocalizationProjectNamesStr.Split(','))
				{
					LocalizationProjectNames.Add(ProjectName.Trim());
				}
			}
		}

		var LocalizationProviderName = ParseParamValue("LocalizationProvider");
		if (LocalizationProviderName == null)
		{
			LocalizationProviderName = "";
		}

		var LocalizationStepNames = new List<string>();
		{
			var LocalizationStepNamesStr = ParseParamValue("LocalizationSteps");
			if (LocalizationStepNamesStr == null)
			{
				LocalizationStepNames.AddRange(new string[] { "Download", "Gather", "Import", "Export", "Compile", "GenerateReports", "Upload" });
			}
			else
			{
				foreach (var StepName in LocalizationStepNamesStr.Split(','))
				{
					LocalizationStepNames.Add(StepName.Trim());
				}
			}
			LocalizationStepNames.Add("Monolithic"); // Always allow the monolithic scripts to run as we don't know which steps they do
		}

		var ShouldGatherPlugins = ParseParam("IncludePlugins");
		var IncludePlugins = new List<string>();
		var ExcludePlugins = new List<string>();
		if (ShouldGatherPlugins)
		{
			var IncludePluginsStr = ParseParamValue("IncludePlugins");
			if (IncludePluginsStr != null)
			{
				foreach (var PluginName in IncludePluginsStr.Split(','))
				{
					IncludePlugins.Add(PluginName.Trim());
				}
			}

			var ExcludePluginsStr = ParseParamValue("ExcludePlugins");
			if (ExcludePluginsStr != null)
			{
				foreach (var PluginName in ExcludePluginsStr.Split(','))
				{
					ExcludePlugins.Add(PluginName.Trim());
				}
			}
		}

		var ShouldGatherPlatforms = ParseParam("IncludePlatforms");

		var AdditionalCommandletArguments = ParseParamValue("AdditionalCommandletArguments");
		if (AdditionalCommandletArguments == null)
		{
			AdditionalCommandletArguments = "";
		}

		var EnableParallelGather = ParseParam("ParallelGather");

		var StartTime = DateTime.UtcNow;

		var LocalizationBatches = new List<LocalizationBatch>();

		// Add the static set of localization projects as a batch
		if (LocalizationProjectNames.Count > 0)
		{
			LocalizationBatches.Add(new LocalizationBatch(UEProjectDirectory, UEProjectDirectory, "", LocalizationProjectNames));
		}

		// Build up any additional batches needed for platforms
		if (ShouldGatherPlatforms)
		{
			var PlatformsRootDirectory = new DirectoryReference(CombinePaths(UEProjectRoot, UEProjectDirectory, "Platforms"));
			if (DirectoryReference.Exists(PlatformsRootDirectory))
			{
				foreach (DirectoryReference PlatformDirectory in DirectoryReference.EnumerateDirectories(PlatformsRootDirectory))
				{
					// Find the localization targets defined for this platform
					var PlatformTargetNames = GetLocalizationTargetsFromDirectory(new DirectoryReference(CombinePaths(PlatformDirectory.FullName, "Config", "Localization")));
					if (PlatformTargetNames.Count > 0)
					{
						var RootRelativePluginPath = PlatformDirectory.MakeRelativeTo(new DirectoryReference(UEProjectRoot));
						RootRelativePluginPath = RootRelativePluginPath.Replace('\\', '/'); // Make sure we use / as these paths are used with P4

						LocalizationBatches.Add(new LocalizationBatch(UEProjectDirectory, RootRelativePluginPath, "", PlatformTargetNames));
					}
				}
			}
		}

		// Build up any additional batches needed for plugins
		if (ShouldGatherPlugins)
		{
			var PluginsRootDirectory = new DirectoryReference(CombinePaths(UEProjectRoot, UEProjectDirectory));
			IReadOnlyList<PluginInfo> AllPlugins = Plugins.ReadPluginsFromDirectory(PluginsRootDirectory, "Plugins", UEProjectName.Length == 0 ? PluginType.Engine : PluginType.Project);

			// Add a batch for each plugin that meets our criteria
			var AvailablePluginNames = new HashSet<string>();
			foreach (var PluginInfo in AllPlugins)
			{
				AvailablePluginNames.Add(PluginInfo.Name);

				bool ShouldIncludePlugin = (IncludePlugins.Count == 0 || IncludePlugins.Contains(PluginInfo.Name)) && !ExcludePlugins.Contains(PluginInfo.Name);
				if (ShouldIncludePlugin && PluginInfo.Descriptor.LocalizationTargets != null && PluginInfo.Descriptor.LocalizationTargets.Length > 0)
				{
					var RootRelativePluginPath = PluginInfo.Directory.MakeRelativeTo(new DirectoryReference(UEProjectRoot));
					RootRelativePluginPath = RootRelativePluginPath.Replace('\\', '/'); // Make sure we use / as these paths are used with P4

					var PluginTargetNames = new List<string>();
					foreach (var LocalizationTarget in PluginInfo.Descriptor.LocalizationTargets)
					{
						PluginTargetNames.Add(LocalizationTarget.Name);
					}

					LocalizationBatches.Add(new LocalizationBatch(UEProjectDirectory, RootRelativePluginPath, PluginInfo.Name, PluginTargetNames));
				}
			}

			// If we had an explicit list of plugins to include, warn if any were missing
			foreach (string PluginName in IncludePlugins)
			{
				if (!AvailablePluginNames.Contains(PluginName))
				{
					LogWarning("The plugin '{0}' specified by -IncludePlugins wasn't found and will be skipped.", PluginName);
				}
			}
		}

		// Create a single changelist to use for all changes
		int PendingChangeList = 0;
		if (P4Enabled)
		{
			var ChangeListCommitMessage = String.Format("Localization Automation using CL {0}", P4Env.Changelist);
			if (File.Exists(CombinePaths(CmdEnv.LocalRoot, @"Engine/Restricted/NotForLicensees/Build/EpicInternal.txt")))
			{
				ChangeListCommitMessage += "\n#okforgithub ignore";
			}

			PendingChangeList = P4.CreateChange(P4Env.Client, ChangeListCommitMessage);
		}

		// Prepare to process each localization batch
		var LocalizationTasks = new List<LocalizationTask>();
		foreach (var LocalizationBatch in LocalizationBatches)
		{
			var LocalizationTask = new LocalizationTask(LocalizationBatch, UEProjectRoot, LocalizationProviderName, PendingChangeList, this);
			LocalizationTasks.Add(LocalizationTask);

			// Make sure the Localization configs and content is up-to-date to ensure we don't get errors later on
			if (P4Enabled)
			{
				LogInformation("Sync necessary content to head revision");
				P4.Sync(P4Env.Branch + "/" + LocalizationTask.Batch.LocalizationTargetDirectory + "/Config/Localization/...");
				P4.Sync(P4Env.Branch + "/" + LocalizationTask.Batch.LocalizationTargetDirectory + "/Content/Localization/...");
			}

			// Generate the info we need to gather for each project
			foreach (var ProjectName in LocalizationTask.Batch.LocalizationProjectNames)
			{
				LocalizationTask.ProjectInfos.Add(GenerateProjectInfo(LocalizationTask.RootLocalizationTargetDirectory, ProjectName, LocalizationStepNames));
			}
		}

		// Hash the current PO files on disk so we can work out whether they actually change
		Dictionary<string, byte[]> InitalPOFileHashes = null;
		if (P4Enabled)
		{
			InitalPOFileHashes = GetPOFileHashes(LocalizationBatches, UEProjectRoot);
		}

		// Download the latest translations from our localization provider
		if (LocalizationStepNames.Contains("Download"))
		{
			foreach (var LocalizationTask in LocalizationTasks)
			{
				if (LocalizationTask.LocProvider != null)
				{
					foreach (var ProjectInfo in LocalizationTask.ProjectInfos)
					{
						LocalizationTask.LocProvider.DownloadProjectFromLocalizationProvider(ProjectInfo.ProjectName, ProjectInfo.ImportInfo);
					}
				}
			}
		}

		// Begin the gather command for each task
		// These can run in parallel when ParallelGather is enabled
		{
			var EditorExe = CombinePaths(CmdEnv.LocalRoot, @"Engine/Binaries/Win64/UE4Editor-Cmd.exe");

			// Set the common basic editor arguments
			var EditorArguments = P4Enabled 
				? String.Format("-SCCProvider=Perforce -P4Port={0} -P4User={1} -P4Client={2} -P4Passwd={3} -P4Changelist={4} -EnableSCC -DisableSCCSubmit", P4Env.ServerAndPort, P4Env.User, P4Env.Client, P4.GetAuthenticationToken(), PendingChangeList)
				: "-SCCProvider=None";
			if (IsBuildMachine)
			{
				EditorArguments += " -BuildMachine";
			}
			EditorArguments += " -Unattended -LogLocalizationConflicts";
			if (EnableParallelGather)
			{
				EditorArguments += " -multiprocess";
			}
			if (!String.IsNullOrEmpty(AdditionalCommandletArguments))
			{
				EditorArguments += " " + AdditionalCommandletArguments;
			}

			// Set the common process run options
			var CommandletRunOptions = ERunOptions.Default | ERunOptions.NoLoggingOfRunCommand; // Disable logging of the run command as it will print the exit code which GUBP can pick up as an error (we do that ourselves later)
			if (EnableParallelGather)
			{
				CommandletRunOptions |= ERunOptions.NoWaitForExit;
			}

			foreach (var LocalizationTask in LocalizationTasks)
			{
				var ProjectArgument = String.IsNullOrEmpty(UEProjectName) ? "" : String.Format("\"{0}\"", Path.Combine(LocalizationTask.RootWorkingDirectory, String.Format("{0}.uproject", UEProjectName)));

				foreach (var ProjectInfo in LocalizationTask.ProjectInfos)
				{
					var LocalizationConfigFiles = new List<string>();
					foreach (var LocalizationStep in ProjectInfo.LocalizationSteps)
					{
						if (LocalizationStepNames.Contains(LocalizationStep.Name))
						{
							LocalizationConfigFiles.Add(LocalizationStep.LocalizationConfigFile);
						}
					}

					if (LocalizationConfigFiles.Count > 0)
					{
						var Arguments = String.Format("{0} -run=GatherText -config=\"{1}\" {2}", ProjectArgument, String.Join(";", LocalizationConfigFiles), EditorArguments);
						LogInformation("Running localization commandlet for '{0}': {1}", ProjectInfo.ProjectName, Arguments);
						LocalizationTask.GatherProcessResults.Add(Run(EditorExe, Arguments, null, CommandletRunOptions));
					}
					else
					{
						LocalizationTask.GatherProcessResults.Add(null);
					}
				}
			}
		}

		// Wait for each commandlet process to finish and report the result.
		// This runs even for non-parallel execution to log the exit state of the process.
		foreach (var LocalizationTask in LocalizationTasks)
		{
			for (int ProjectIndex = 0; ProjectIndex < LocalizationTask.ProjectInfos.Count; ++ProjectIndex)
			{
				var ProjectInfo = LocalizationTask.ProjectInfos[ProjectIndex];
				var RunResult = LocalizationTask.GatherProcessResults[ProjectIndex];

				if (RunResult != null)
				{
					RunResult.WaitForExit();
					RunResult.OnProcessExited();
					RunResult.DisposeProcess();
					
					if (RunResult.ExitCode == 0)
					{
						LogInformation("The localization commandlet for '{0}' exited with code 0.", ProjectInfo.ProjectName);
					}
					else
					{
						LogWarning("The localization commandlet for '{0}' exited with code {1} which likely indicates a crash.", ProjectInfo.ProjectName, RunResult.ExitCode);
					}
				}
			}
		}

		// Upload the latest sources to our localization provider
		if (LocalizationStepNames.Contains("Upload"))
		{
			foreach (var LocalizationTask in LocalizationTasks)
			{
				if (LocalizationTask.LocProvider != null)
				{
					// Upload all text to our localization provider
					for (int ProjectIndex = 0; ProjectIndex < LocalizationTask.ProjectInfos.Count; ++ProjectIndex)
					{
						var ProjectInfo = LocalizationTask.ProjectInfos[ProjectIndex];
						var RunResult = LocalizationTask.GatherProcessResults[ProjectIndex];

						if (RunResult != null && RunResult.ExitCode == 0)
						{
							// Recalculate the split platform paths before doing the upload, as the export may have changed them
							ProjectInfo.ExportInfo.CalculateSplitPlatformNames(LocalizationTask.RootLocalizationTargetDirectory);
							LocalizationTask.LocProvider.UploadProjectToLocalizationProvider(ProjectInfo.ProjectName, ProjectInfo.ExportInfo);
						}
						else
						{
							LogWarning("Skipping upload to the localization provider for '{0}' due to an earlier commandlet failure.", ProjectInfo.ProjectName);
						}
					}
				}
			}
		}

		// Clean-up the changelist so it only contains the changed files, and then submit it (if we were asked to)
		if (P4Enabled)
		{
			// Revert any PO files that haven't changed aside from their header
			{
				var POFilesToRevert = new List<string>();

				var CurrentPOFileHashes = GetPOFileHashes(LocalizationBatches, UEProjectRoot);
				foreach (var CurrentPOFileHashPair in CurrentPOFileHashes)
				{
					byte[] InitialPOFileHash;
					if (InitalPOFileHashes.TryGetValue(CurrentPOFileHashPair.Key, out InitialPOFileHash) && InitialPOFileHash.SequenceEqual(CurrentPOFileHashPair.Value))
					{
						POFilesToRevert.Add(CurrentPOFileHashPair.Key);
					}
				}

				if (POFilesToRevert.Count > 0)
				{
					var P4RevertArgsFilename = CombinePaths(CmdEnv.LocalRoot, "Engine", "Intermediate", String.Format("LocalizationP4RevertArgs-{0}.txt", Guid.NewGuid().ToString()));

					using (StreamWriter P4RevertArgsWriter = File.CreateText(P4RevertArgsFilename))
					{
						foreach (var POFileToRevert in POFilesToRevert)
						{
							P4RevertArgsWriter.WriteLine(POFileToRevert);
						}
					}

					P4.LogP4(String.Format("-x{0} revert", P4RevertArgsFilename));
					DeleteFile_NoExceptions(P4RevertArgsFilename);
				}
			}

			// Revert any other unchanged files
			P4.RevertUnchanged(PendingChangeList);

			// Submit that single changelist now
			if (AllowSubmit)
			{
				int SubmittedChangeList;
				P4.Submit(PendingChangeList, out SubmittedChangeList);
			}
		}

		var RunDuration = (DateTime.UtcNow - StartTime).TotalMilliseconds;
		LogInformation("Localize command finished in {0} seconds", RunDuration / 1000);
	}

	private ProjectInfo GenerateProjectInfo(string RootWorkingDirectory, string ProjectName, IReadOnlyList<string> LocalizationStepNames)
	{
		var LocalizationSteps = new List<ProjectStepInfo>();
		ProjectImportExportInfo ImportInfo = null;
		ProjectImportExportInfo ExportInfo = null;

		// Projects generated by the localization dashboard will use multiple config files that must be run in a specific order
		// Older projects (such as the Engine) would use a single config file containing all the steps
		// Work out which kind of project we're dealing with...
		var MonolithicConfigFile = CombinePaths(RootWorkingDirectory, String.Format(@"Config/Localization/{0}.ini", ProjectName));
		if (File.Exists(MonolithicConfigFile))
		{
			LocalizationSteps.Add(new ProjectStepInfo("Monolithic", MonolithicConfigFile));

			ImportInfo = GenerateProjectImportExportInfo(RootWorkingDirectory, MonolithicConfigFile);
			ExportInfo = ImportInfo;
		}
		else
		{
			var FileSuffixes = new[] { 
				new { Suffix = "Gather", Required = LocalizationStepNames.Contains("Gather") }, 
				new { Suffix = "Import", Required = LocalizationStepNames.Contains("Import") || LocalizationStepNames.Contains("Download") },	// Downloading needs the parsed ImportInfo
				new { Suffix = "Export", Required = LocalizationStepNames.Contains("Gather") || LocalizationStepNames.Contains("Upload")},		// Uploading needs the parsed ExportInfo
				new { Suffix = "Compile", Required = LocalizationStepNames.Contains("Compile") }, 
				new { Suffix = "GenerateReports", Required = false } 
			};

			foreach (var FileSuffix in FileSuffixes)
			{
				var ModularConfigFile = CombinePaths(RootWorkingDirectory, String.Format(@"Config/Localization/{0}_{1}.ini", ProjectName, FileSuffix.Suffix));

				if (File.Exists(ModularConfigFile))
				{
					LocalizationSteps.Add(new ProjectStepInfo(FileSuffix.Suffix, ModularConfigFile));

					if (FileSuffix.Suffix == "Import")
					{
						ImportInfo = GenerateProjectImportExportInfo(RootWorkingDirectory, ModularConfigFile);
					}
					else if (FileSuffix.Suffix == "Export")
					{
						ExportInfo = GenerateProjectImportExportInfo(RootWorkingDirectory, ModularConfigFile);
					}
				}
				else if (FileSuffix.Required)
				{
					throw new AutomationException("Failed to find a required config file! '{0}'", ModularConfigFile);
				}
			}
		}

		return new ProjectInfo(ProjectName, LocalizationSteps, ImportInfo, ExportInfo);
	}

	private ProjectImportExportInfo GenerateProjectImportExportInfo(string RootWorkingDirectory, string LocalizationConfigFile)
	{
		ConfigFile File = new ConfigFile(new FileReference(LocalizationConfigFile), ConfigLineAction.Add);
		var LocalizationConfig = new ConfigHierarchy(new ConfigFile[] { File });

		string DestinationPath;
		if (!LocalizationConfig.GetString("CommonSettings", "DestinationPath", out DestinationPath))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'DestinationPath', File: '{0}'", LocalizationConfigFile);
		}

		string ManifestName;
		if (!LocalizationConfig.GetString("CommonSettings", "ManifestName", out ManifestName))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'ManifestName', File: '{0}'", LocalizationConfigFile);
		}

		string ArchiveName;
		if (!LocalizationConfig.GetString("CommonSettings", "ArchiveName", out ArchiveName))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'ArchiveName', File: '{0}'", LocalizationConfigFile);
		}

		string PortableObjectName;
		if (!LocalizationConfig.GetString("CommonSettings", "PortableObjectName", out PortableObjectName))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'PortableObjectName', File: '{0}'", LocalizationConfigFile);
		}

		string NativeCulture;
		if (!LocalizationConfig.GetString("CommonSettings", "NativeCulture", out NativeCulture))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'NativeCulture', File: '{0}'", LocalizationConfigFile);
		}

		List<string> CulturesToGenerate;
		if (!LocalizationConfig.GetArray("CommonSettings", "CulturesToGenerate", out CulturesToGenerate))
		{
			throw new AutomationException("Failed to find a required config key! Section: 'CommonSettings', Key: 'CulturesToGenerate', File: '{0}'", LocalizationConfigFile);
		}

		bool bUseCultureDirectory;
		if (!LocalizationConfig.GetBool("CommonSettings", "bUseCultureDirectory", out bUseCultureDirectory))
		{
			// bUseCultureDirectory is optional, default is true
			bUseCultureDirectory = true;
		}

		var ProjectImportExportInfo = new ProjectImportExportInfo(DestinationPath, ManifestName, ArchiveName, PortableObjectName, NativeCulture, CulturesToGenerate, bUseCultureDirectory);
		ProjectImportExportInfo.CalculateSplitPlatformNames(RootWorkingDirectory);
		return ProjectImportExportInfo;
	}

	private List<string> GetLocalizationTargetsFromDirectory(DirectoryReference ConfigDirectory)
	{
		var LocalizationTargets = new List<string>();

		if (DirectoryReference.Exists(ConfigDirectory))
		{
			var FileSuffixes = new[] {
				"_Gather",
				"_Import",
				"_Export",
				"_Compile",
				"_GenerateReports",
			};

			foreach (FileReference ConfigFile in DirectoryReference.EnumerateFiles(ConfigDirectory))
			{
				string LocalizationTarget = ConfigFile.GetFileNameWithoutExtension();
				foreach (var FileSuffix in FileSuffixes)
				{
					if (LocalizationTarget.EndsWith(FileSuffix))
					{
						LocalizationTarget = LocalizationTarget.Remove(LocalizationTarget.Length - FileSuffix.Length);
					}
				}
				if (!LocalizationTargets.Contains(LocalizationTarget))
				{
					LocalizationTargets.Add(LocalizationTarget);
				}
			}
		}

		return LocalizationTargets;
	}

	private Dictionary<string, byte[]> GetPOFileHashes(IReadOnlyList<LocalizationBatch> LocalizationBatches, string UEProjectRoot)
	{
		var AllFiles = new Dictionary<string, byte[]>();

		foreach (var LocalizationBatch in LocalizationBatches)
		{
			var LocalizationPath = CombinePaths(UEProjectRoot, LocalizationBatch.LocalizationTargetDirectory, "Content", "Localization");
			if (!Directory.Exists(LocalizationPath))
			{
				continue;
			}

			string[] POFileNames = Directory.GetFiles(LocalizationPath, "*.po", SearchOption.AllDirectories);
			foreach (var POFileName in POFileNames)
			{
				using (StreamReader POFileReader = File.OpenText(POFileName))
				{
					// Don't include the PO header (everything up to the first empty line) in the hash as it contains transient information (like timestamps) that we don't care about
					bool bHasParsedHeader = false;
					var POFileHash = MD5.Create();

					string POFileLine;
					while ((POFileLine = POFileReader.ReadLine()) != null)
					{
						if (!bHasParsedHeader)
						{
							bHasParsedHeader = POFileLine.Length == 0;
							continue;
						}

						var POFileLineBytes = Encoding.UTF8.GetBytes(POFileLine);
						POFileHash.TransformBlock(POFileLineBytes, 0, POFileLineBytes.Length, null, 0);
					}

					POFileHash.TransformFinalBlock(new byte[0], 0, 0);

					AllFiles.Add(POFileName, POFileHash.Hash);
				}
			}
		}

		return AllFiles;
	}
}

[Help("OnlyLoc", "Optional.  Only submit generated loc files, do not submit any other generated file.")]
[Help("NoRobomerge", "Optional.  Do not include the markup in the CL description to allow robomerging to other branches.")]
public class ExportMcpTemplates : BuildCommand
{
	public static string GetGameBackendFolder(FileReference ProjectFile)
	{
		return Path.Combine(ProjectFile.Directory.FullName, "Content", "Backend");
	}

	public static void RunExportTemplates(FileReference ProjectFile, bool bCheckoutAndSubmit, bool bOnlyLoc, bool bbNoRobomerge, string CommandletOverride)
	{
		string EditorExe = "UE4Editor.exe";
		EditorExe = HostPlatform.Current.GetUE4ExePath(EditorExe);

		string GameBackendFolder = GetGameBackendFolder(ProjectFile);
		if (!DirectoryExists_NoExceptions(GameBackendFolder))
		{
			throw new AutomationException("Error: RunExportTemplates failure. GameBackendFolder not found. {0}", GameBackendFolder);
		}

		string FolderToGenerateIn = GameBackendFolder;

		string Parameters = "-GenerateLoc";

		int WorkingCL = -1;
		if (bCheckoutAndSubmit)
		{
			if (!CommandUtils.P4Enabled)
			{
				throw new AutomationException("Error: RunExportTemplates failure. bCheckoutAndSubmit used without access to P4");
			}

			// Check whether all templates in folder are latest.  If not skip exporting.
			List<string> FilesPreviewSynced;
			CommandUtils.P4.PreviewSync(out FilesPreviewSynced, FolderToGenerateIn + "/...");
			if (FilesPreviewSynced.Count() > 0)
			{
				CommandUtils.LogInformation("Some files in folder {0} are not latest, which means that these files might have already been updated by an earlier exporting job.  Skip this one.", FolderToGenerateIn);
				return;
			}

			String CLDescription = String.Format("RunExportTemplates Updated mcp templates using CL {0}", P4Env.Changelist);
			if (bOnlyLoc)
			{
				CLDescription += " [OnlyLoc]";
			}
			if (!bbNoRobomerge)
			{
				CLDescription += "\r\n#robomerge[ALL] #DisregardExcludedAuthors";
			}

			WorkingCL = CommandUtils.P4.CreateChange(CommandUtils.P4Env.Client, CLDescription);
			CommandUtils.P4.Edit(WorkingCL, FolderToGenerateIn + "/...");
		}

		string Commandlet = string.IsNullOrWhiteSpace(CommandletOverride) ? "ExportTemplatesCommandlet" : CommandletOverride;
		CommandUtils.RunCommandlet(ProjectFile, EditorExe, Commandlet, Parameters);

		if (WorkingCL > 0)
		{
			CommandUtils.P4.RevertUnchanged(WorkingCL);

			if (bOnlyLoc)
			{
				// Revert all folders and files except GeneratedLoc.json
				foreach (string DirPath in Directory.GetDirectories(FolderToGenerateIn))
				{
					DirectoryInfo Dir = new DirectoryInfo(DirPath);
					CommandUtils.P4.Revert(WorkingCL, FolderToGenerateIn + "/" + Dir.Name + "/...");
				}

				foreach (string FilePath in Directory.GetFiles(FolderToGenerateIn))
				{
					FileInfo File = new FileInfo(FilePath);
					if (File.Name != "GeneratedLoc.json")
					{
						CommandUtils.P4.Revert(WorkingCL, FolderToGenerateIn + "/" + File.Name);
					}
				}
			}

			// If the CL is empty after the RevertUnchanged, the submit call will just delete the CL and return cleanly
			int SubmittedCL;
			CommandUtils.P4.Submit(WorkingCL, out SubmittedCL, false, true);
		}
	}

	public override void ExecuteBuild()
	{
		string ProjectName = ParseParamValue("ProjectName", null);
		if (string.IsNullOrWhiteSpace(ProjectName))
		{
			throw new AutomationException("Error: ExportMcpTemplates failure. No ProjectName defined!");
		}

		FileReference ProjectFile = new FileReference(CombinePaths(CmdEnv.LocalRoot, ProjectName, String.Format("{0}.uproject", ProjectName)));
		bool bOnlyLoc = ParseParam("OnlyLoc");
		bool bNoRobomerge = ParseParam("NoRobomerge");
		string CommandletOverride = ParseParamValue("Commandlet", null);
		RunExportTemplates(ProjectFile, true, bOnlyLoc, bNoRobomerge, CommandletOverride);
	}
}

// Legacy alias
class Localise : Localize
{
};
