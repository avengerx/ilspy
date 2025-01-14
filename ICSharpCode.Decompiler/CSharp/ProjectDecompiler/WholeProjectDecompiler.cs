﻿// Copyright (c) 2016 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Solution;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

using Microsoft.Win32;

using static ICSharpCode.Decompiler.Metadata.MetadataExtensions;

namespace ICSharpCode.Decompiler.CSharp.ProjectDecompiler
{
	/// <summary>
	/// Decompiles an assembly into a visual studio project file.
	/// </summary>
	public class WholeProjectDecompiler : IProjectInfoProvider
	{
		#region Settings
		/// <summary>
		/// Gets the setting this instance uses for decompiling.
		/// </summary>
		public DecompilerSettings Settings { get; }

		LanguageVersion? languageVersion;

		public LanguageVersion LanguageVersion {
			get { return languageVersion ?? Settings.GetMinimumRequiredVersion(); }
			set {
				var minVersion = Settings.GetMinimumRequiredVersion();
				if (value < minVersion)
					throw new InvalidOperationException($"The chosen settings require at least {minVersion}." +
						$" Please change the DecompilerSettings accordingly.");
				languageVersion = value;
			}
		}

		public IAssemblyResolver AssemblyResolver { get; }

		public AssemblyReferenceClassifier AssemblyReferenceClassifier { get; }

		public IDebugInfoProvider DebugInfoProvider { get; }

		/// <summary>
		/// The MSBuild ProjectGuid to use for the new project.
		/// </summary>
		public Guid ProjectGuid { get; }

		/// <summary>
		/// The target directory that the decompiled files are written to.
		/// </summary>
		/// <remarks>
		/// This property is set by DecompileProject() and protected so that overridden protected members
		/// can access it.
		/// </remarks>
		public string TargetDirectory { get; protected set; }

		/// <summary>
		/// Path to the snk file to use for signing.
		/// <c>null</c> to not sign.
		/// </summary>
		public string StrongNameKeyFile { get; set; }

		public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

		public IProgress<DecompilationProgress> ProgressIndicator { get; set; }
		#endregion

		public WholeProjectDecompiler(IAssemblyResolver assemblyResolver)
			: this(new DecompilerSettings(), assemblyResolver, assemblyReferenceClassifier: null, debugInfoProvider: null)
		{
		}

		public WholeProjectDecompiler(
			DecompilerSettings settings,
			IAssemblyResolver assemblyResolver,
			AssemblyReferenceClassifier assemblyReferenceClassifier,
			IDebugInfoProvider debugInfoProvider)
			: this(settings, Guid.NewGuid(), assemblyResolver, assemblyReferenceClassifier, debugInfoProvider)
		{
		}

		protected WholeProjectDecompiler(
			DecompilerSettings settings,
			Guid projectGuid,
			IAssemblyResolver assemblyResolver,
			AssemblyReferenceClassifier assemblyReferenceClassifier,
			IDebugInfoProvider debugInfoProvider)
		{
			Settings = settings ?? throw new ArgumentNullException(nameof(settings));
			ProjectGuid = projectGuid;
			AssemblyResolver = assemblyResolver ?? throw new ArgumentNullException(nameof(assemblyResolver));
			AssemblyReferenceClassifier = assemblyReferenceClassifier ?? new AssemblyReferenceClassifier();
			DebugInfoProvider = debugInfoProvider;
			projectWriter = Settings.UseSdkStyleProjectFormat ? ProjectFileWriterSdkStyle.Create() : ProjectFileWriterDefault.Create();
		}

		// per-run members
		HashSet<string> directories = new HashSet<string>(Platform.FileNameComparer);
		readonly IProjectFileWriter projectWriter;

		#region Regular expressions

		private static string CharsToHexString(char[] charList)
		{
			var reHex = string.Empty;
			foreach (var chr in charList)
				reHex += string.Format("\\u{0:x4}", (int)chr);

			return reHex;
		}

		private readonly static string InvalidPathRePat = "[" + CharsToHexString(Path.GetInvalidPathChars()) + "]";
		private static Regex PathRe = new Regex(InvalidPathRePat);

		private readonly static string InvalidFileNameRePat = "[" + CharsToHexString(Path.GetInvalidFileNameChars()) + "]";
		private static Regex FileNameRe = new Regex(InvalidFileNameRePat);

		private const string WindowsColonRePat = "(^[a-zA-Z]:)?[^:]+$";
		private static Regex WindowsColonRe = new Regex(WindowsColonRePat);

		#endregion

		public void DecompileProject(PEFile moduleDefinition, string targetDirectory, CancellationToken cancellationToken = default(CancellationToken))
		{
			var targetDirectoryInfo = new DirectoryInfo(targetDirectory);

			if (TargetDirectory != targetDirectoryInfo.FullName)
			{
				TargetDirectory = targetDirectoryInfo.FullName;
			}

			string projectFileName = Path.Combine(TargetDirectory, CleanUpFileName(moduleDefinition.Name) + ".csproj");

			using (var writer = MakeStreamWriter(projectFileName))
			{
				DecompileProject(moduleDefinition, targetDirectoryInfo, writer, cancellationToken);
			}
		}

		public ProjectId DecompileProject(PEFile moduleDefinition, string targetDirectory, TextWriter projectFileWriter, CancellationToken cancellationToken = default(CancellationToken)) =>
			DecompileProject(moduleDefinition, new DirectoryInfo(targetDirectory), projectFileWriter, cancellationToken);

		public ProjectId DecompileProject(PEFile moduleDefinition, DirectoryInfo targetDirectory, TextWriter projectFileWriter, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (targetDirectory == null)
			{
				throw new InvalidOperationException("Must set TargetDirectory");
			}

			if (TargetDirectory != targetDirectory.FullName)
			{
				TargetDirectory = targetDirectory.FullName;
			}

			directories.Clear();
			var files = WriteCodeFilesInProject(moduleDefinition, cancellationToken).ToList();
			files.AddRange(WriteResourceFilesInProject(moduleDefinition));
			files.AddRange(WriteMiscellaneousFilesInProject(moduleDefinition));
			if (StrongNameKeyFile != null)
			{
				CopyFile(StrongNameKeyFile, Path.Combine(TargetDirectory, Path.GetFileName(StrongNameKeyFile)), overwrite: true);
			}

			projectWriter.Write(projectFileWriter, this, files, moduleDefinition);

			string platformName = TargetServices.GetPlatformName(moduleDefinition);
			return new ProjectId(platformName, ProjectGuid, ProjectTypeGuids.CSharpWindows);
		}

		#region WriteCodeFilesInProject
		protected virtual bool IncludeTypeWhenDecompilingProject(PEFile module, TypeDefinitionHandle type)
		{
			var metadata = module.Metadata;
			var typeDef = metadata.GetTypeDefinition(type);
			if (metadata.GetString(typeDef.Name) == "<Module>" || CSharpDecompiler.MemberIsHidden(module, type, Settings))
				return false;
			if (metadata.GetString(typeDef.Namespace) == "XamlGeneratedNamespace" && metadata.GetString(typeDef.Name) == "GeneratedInternalTypeHelper")
				return false;
			return true;
		}

		CSharpDecompiler CreateDecompiler(DecompilerTypeSystem ts)
		{
			var decompiler = new CSharpDecompiler(ts, Settings);
			decompiler.DebugInfoProvider = DebugInfoProvider;
			decompiler.AstTransforms.Add(new EscapeInvalidIdentifiers());
			decompiler.AstTransforms.Add(new RemoveCLSCompliantAttribute());
			return decompiler;
		}

		IEnumerable<(string itemType, string fileName)> WriteAssemblyInfo(DecompilerTypeSystem ts, CancellationToken cancellationToken)
		{
			var decompiler = CreateDecompiler(ts);
			decompiler.CancellationToken = cancellationToken;
			decompiler.AstTransforms.Add(new RemoveCompilerGeneratedAssemblyAttributes());
			SyntaxTree syntaxTree = decompiler.DecompileModuleAndAssemblyAttributes();

			const string prop = "Properties";
			if (directories.Add(prop))
				CreateDir(Path.Combine(TargetDirectory, prop));
			string assemblyInfo = Path.Combine(prop, "AssemblyInfo.cs");
			using (StreamWriter w = MakeStreamWriter(Path.Combine(TargetDirectory, assemblyInfo)))
			{
				syntaxTree.AcceptVisitor(new CSharpOutputVisitor(w, Settings.CSharpFormattingOptions));
			}
			return new[] { ("Compile", assemblyInfo) };
		}

		IEnumerable<(string itemType, string fileName)> WriteCodeFilesInProject(Metadata.PEFile module, CancellationToken cancellationToken)
		{
			var metadata = module.Metadata;
			var rootNamespace = module.Name.Replace(' ', '_');
			var files = module.Metadata.GetTopLevelTypeDefinitions().Where(td => IncludeTypeWhenDecompilingProject(module, td)).GroupBy(
				delegate (TypeDefinitionHandle h) {
					var type = metadata.GetTypeDefinition(h);
					var typeName = metadata.GetString(type.Name);

					int backtickPos = typeName.IndexOf('`');
					if (backtickPos > 1)
						typeName = typeName.Substring(0, backtickPos);

					string file = CleanUpFileName(typeName) + ".cs";
					string ns = metadata.GetString(type.Namespace);
					if (string.IsNullOrEmpty(ns))
					{
						return file;
					}
					else
					{
						string dir;
						if (Settings.UseNestedDirectoriesForNamespaces)
						{
							if (ns == rootNamespace)
								ns = string.Empty;
							else if (ns.StartsWith(rootNamespace + "."))
								ns = ns.Substring(rootNamespace.Length + 1);

							dir = CleanUpPath(ns);
						}
						else
						{
							dir = CleanUpDirectoryName(ns);
						}

						if (directories.Add(dir))
							CreateDir(Path.Combine(TargetDirectory, dir));
						return Path.Combine(dir, file);
					}
				}, StringComparer.OrdinalIgnoreCase).ToList();
			int total = files.Count;
			var progress = ProgressIndicator;
			DecompilerTypeSystem ts = new DecompilerTypeSystem(module, AssemblyResolver, Settings);
			Parallel.ForEach(
				Partitioner.Create(files, loadBalance: true),
				new ParallelOptions {
					MaxDegreeOfParallelism = this.MaxDegreeOfParallelism,
					CancellationToken = cancellationToken
				},
				delegate (IGrouping<string, TypeDefinitionHandle> file) {
					using (StreamWriter w = MakeStreamWriter(Path.Combine(TargetDirectory, file.Key)))
					{
						try
						{
							CSharpDecompiler decompiler = CreateDecompiler(ts);
							decompiler.CancellationToken = cancellationToken;
							var syntaxTree = decompiler.DecompileTypes(file.ToArray());
							syntaxTree.AcceptVisitor(new CSharpOutputVisitor(w, Settings.CSharpFormattingOptions));
						}
						catch (Exception innerException) when (!(innerException is OperationCanceledException || innerException is DecompilerException))
						{
							throw new DecompilerException(module, $"Error decompiling for '{file.Key}'", innerException);
						}
					}
					progress?.Report(new DecompilationProgress(total, file.Key));
				});
			return files.Select(f => ("Compile", f.Key)).Concat(WriteAssemblyInfo(ts, cancellationToken));
		}
		#endregion

		#region WriteResourceFilesInProject
		protected virtual IEnumerable<(string itemType, string fileName)> WriteResourceFilesInProject(Metadata.PEFile module)
		{
			var rootNamespace = module.Name.Replace(' ', '_');

			if (Settings.UseNestedDirectoriesForNamespaces)
			{
				var dirCandidates = new Dictionary<string, uint>();
				foreach (var r in module.Resources.Where(r => r.ResourceType == ResourceType.Embedded))
				{
					var resName = r.Name;

					if (resName.StartsWith(rootNamespace + "."))
						resName = resName.Substring(rootNamespace.Length + 1);

					var pathParts = resName.Split('.');
					if (pathParts.Length > 2)
					{
						var thisCandidate = "";
						for (int i = 0; i < pathParts.Length - 1; i++)
						{
							thisCandidate = Path.Combine(thisCandidate, pathParts[i]);
							if (dirCandidates.ContainsKey(thisCandidate))
								dirCandidates[thisCandidate]++;
							else
								dirCandidates.Add(thisCandidate, 1);
						}
					}
				}

				// Just creating the directory here should be enough. GetFileNameForResource()
				// will look up if a matching directory already exists for us.
				foreach (var candidate in dirCandidates)
					if (candidate.Value > 2 && directories.Add(candidate.Key))
						CreateDir(Path.Combine(TargetDirectory, candidate.Key));
			}

			foreach (var r in module.Resources.Where(r => r.ResourceType == ResourceType.Embedded))
			{
				Stream stream = r.TryOpenStream();
				stream.Position = 0;

				if (r.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
				{
					bool decodedIntoIndividualFiles;
					var individualResources = new List<(string itemType, string fileName)>();
					try
					{
						var resourcesFile = new ResourcesFile(stream);
						if (resourcesFile.AllEntriesAreStreams())
						{
							foreach (var (name, value) in resourcesFile)
							{
								string fileName = SanitizeFileName(name)
									.Replace('/', Path.DirectorySeparatorChar);

								if (fileName.StartsWith(rootNamespace + "."))
									fileName = fileName.Substring(rootNamespace.Length + 1);

								string dirName = Path.GetDirectoryName(fileName);
								if (!string.IsNullOrEmpty(dirName) && directories.Add(dirName))
								{
									CreateDir(Path.Combine(TargetDirectory, dirName));
								}
								Stream entryStream = (Stream)value;
								entryStream.Position = 0;
								individualResources.AddRange(
									WriteResourceToFile(fileName, name, entryStream));
							}
							decodedIntoIndividualFiles = true;
						}
						else
						{
							decodedIntoIndividualFiles = false;
						}
					}
					catch (BadImageFormatException)
					{
						decodedIntoIndividualFiles = false;
					}
					catch (EndOfStreamException)
					{
						decodedIntoIndividualFiles = false;
					}
					if (decodedIntoIndividualFiles)
					{
						foreach (var entry in individualResources)
						{
							yield return entry;
						}
					}
					else
					{
						stream.Position = 0;

						string fileName = r.Name;
						if (fileName.StartsWith(rootNamespace + "."))
							fileName = fileName.Substring(rootNamespace.Length + 1);
						fileName = GetFileNameForResource(fileName);

						foreach (var entry in WriteResourceToFile(fileName, r.Name, stream))
						{
							yield return entry;
						}
					}
				}
				else
				{
					string fileName = r.Name;
					if (fileName.StartsWith(rootNamespace + "."))
						fileName = fileName.Substring(rootNamespace.Length + 1);
					fileName = GetFileNameForResource(fileName);

					using (FileStream fs = MakeFileStream(Path.Combine(TargetDirectory, fileName), FileMode.Create, FileAccess.Write))
					{
						stream.Position = 0;
						stream.CopyTo(fs);
					}
					yield return ("EmbeddedResource", fileName);
				}
			}
		}

		protected virtual IEnumerable<(string itemType, string fileName)> WriteResourceToFile(string fileName, string resourceName, Stream entryStream)
		{
			if (fileName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
			{
				string resx = Path.ChangeExtension(fileName, ".resx");
				try
				{
					using (FileStream fs = MakeFileStream(Path.Combine(TargetDirectory, resx), FileMode.Create, FileAccess.Write))
					using (ResXResourceWriter writer = new ResXResourceWriter(fs))
					{
						foreach (var entry in new ResourcesFile(entryStream))
						{
							writer.AddResource(entry.Key, entry.Value);
						}
					}
					return new[] { ("EmbeddedResource", resx) };
				}
				catch (BadImageFormatException)
				{
					// if the .resources can't be decoded, just save them as-is
				}
				catch (EndOfStreamException)
				{
					// if the .resources can't be decoded, just save them as-is
				}
			}
			using (FileStream fs = MakeFileStream(Path.Combine(TargetDirectory, fileName), FileMode.Create, FileAccess.Write))
			{
				entryStream.CopyTo(fs);
			}
			return new[] { ("EmbeddedResource", fileName) };
		}

		string GetFileNameForResource(string fullName)
		{
			// Clean up the name first and ensure the length does not exceed the maximum length
			// supported by the OS.
			fullName = Settings.UseNestedDirectoriesForNamespaces ? CleanUpName(fullName, true, true, false) : SanitizeFileName(fullName);

			// The purpose of the below algorithm is to "maximize" the directory name and "minimize" the file name.
			// That is, a full name of the form "Namespace1.Namespace2{...}.NamespaceN.ResourceName" is split such that
			// the directory part Namespace1\Namespace2\... reuses as many existing directories as
			// possible, and only the remaining name parts are used as prefix for the filename.
			string[] splitName = fullName.Split(Path.DirectorySeparatorChar);
			string fileName = string.Join(".", splitName);
			string separator = Path.DirectorySeparatorChar.ToString();
			for (int i = splitName.Length - 1; i > 0; i--)
			{
				string ns = string.Join(separator, splitName, 0, i);
				if (directories.Contains(ns))
				{
					string name = string.Join(".", splitName, i, splitName.Length - i);
					fileName = Path.Combine(ns, name);
					break;
				}
			}
			return fileName;
		}
		#endregion

		#region WriteMiscellaneousFilesInProject
		protected virtual IEnumerable<(string itemType, string fileName)> WriteMiscellaneousFilesInProject(PEFile module)
		{
			var resources = module.Reader.ReadWin32Resources();
			if (resources == null)
				yield break;

			byte[] appIcon = CreateApplicationIcon(resources);
			if (appIcon != null)
			{
				WriteBytesTo(Path.Combine(TargetDirectory, "app.ico"), appIcon);
				yield return ("ApplicationIcon", "app.ico");
			}

			byte[] appManifest = CreateApplicationManifest(resources);
			if (appManifest != null && !IsDefaultApplicationManifest(appManifest))
			{
				WriteBytesTo(Path.Combine(TargetDirectory, "app.manifest"), appManifest);
				yield return ("ApplicationManifest", "app.manifest");
			}

			var appConfig = module.FileName + ".config";
			if (File.Exists(appConfig))
			{
				CopyFile(appConfig, Path.Combine(TargetDirectory, "app.config"), overwrite: true);
				yield return ("ApplicationConfig", Path.GetFileName(appConfig));
			}
		}

		const int RT_ICON = 3;
		const int RT_GROUP_ICON = 14;

		unsafe static byte[] CreateApplicationIcon(Win32ResourceDirectory resources)
		{
			var iconGroup = resources.Find(new Win32ResourceName(RT_GROUP_ICON))?.FirstDirectory()?.FirstData()?.Data;
			if (iconGroup == null)
				return null;

			var iconDir = resources.Find(new Win32ResourceName(RT_ICON));
			if (iconDir == null)
				return null;

			using var outStream = new MemoryStream();
			using var writer = new BinaryWriter(outStream);
			fixed (byte* pIconGroupData = iconGroup)
			{
				var pIconGroup = (GRPICONDIR*)pIconGroupData;
				writer.Write(pIconGroup->idReserved);
				writer.Write(pIconGroup->idType);
				writer.Write(pIconGroup->idCount);

				int iconCount = pIconGroup->idCount;
				uint offset = (2 * 3) + ((uint)iconCount * 0x10);
				for (int i = 0; i < iconCount; i++)
				{
					var pIconEntry = pIconGroup->idEntries + i;
					writer.Write(pIconEntry->bWidth);
					writer.Write(pIconEntry->bHeight);
					writer.Write(pIconEntry->bColorCount);
					writer.Write(pIconEntry->bReserved);
					writer.Write(pIconEntry->wPlanes);
					writer.Write(pIconEntry->wBitCount);
					writer.Write(pIconEntry->dwBytesInRes);
					writer.Write(offset);
					offset += pIconEntry->dwBytesInRes;
				}

				for (int i = 0; i < iconCount; i++)
				{
					var icon = iconDir.FindDirectory(new Win32ResourceName(pIconGroup->idEntries[i].nID))?.FirstData()?.Data;
					if (icon == null)
						return null;
					writer.Write(icon);
				}
			}

			return outStream.ToArray();
		}

		[StructLayout(LayoutKind.Sequential, Pack = 2)]
		unsafe struct GRPICONDIR
		{
			public ushort idReserved;
			public ushort idType;
			public ushort idCount;
			private fixed byte _idEntries[1];
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
			public GRPICONDIRENTRY* idEntries {
				get {
					fixed (byte* p = _idEntries)
						return (GRPICONDIRENTRY*)p;
				}
			}
		};

		[StructLayout(LayoutKind.Sequential, Pack = 2)]
		struct GRPICONDIRENTRY
		{
			public byte bWidth;
			public byte bHeight;
			public byte bColorCount;
			public byte bReserved;
			public ushort wPlanes;
			public ushort wBitCount;
			public uint dwBytesInRes;
			public short nID;
		};

		const int RT_MANIFEST = 24;

		unsafe static byte[] CreateApplicationManifest(Win32ResourceDirectory resources)
		{
			return resources.Find(new Win32ResourceName(RT_MANIFEST))?.FirstDirectory()?.FirstData()?.Data;
		}

		static bool IsDefaultApplicationManifest(byte[] appManifest)
		{
			const string DEFAULT_APPMANIFEST =
				"<?xmlversion=\"1.0\"encoding=\"UTF-8\"standalone=\"yes\"?><assemblyxmlns=\"urn:schemas-microsoft-com" +
				":asm.v1\"manifestVersion=\"1.0\"><assemblyIdentityversion=\"1.0.0.0\"name=\"MyApplication.app\"/><tr" +
				"ustInfoxmlns=\"urn:schemas-microsoft-com:asm.v2\"><security><requestedPrivilegesxmlns=\"urn:schemas-" +
				"microsoft-com:asm.v3\"><requestedExecutionLevellevel=\"asInvoker\"uiAccess=\"false\"/></requestedPri" +
				"vileges></security></trustInfo></assembly>";

			string s = CleanUpApplicationManifest(appManifest);
			return s == DEFAULT_APPMANIFEST;
		}

		static string CleanUpApplicationManifest(byte[] appManifest)
		{
			bool bom = appManifest.Length >= 3 && appManifest[0] == 0xEF && appManifest[1] == 0xBB && appManifest[2] == 0xBF;
			string s = Encoding.UTF8.GetString(appManifest, bom ? 3 : 0, appManifest.Length - (bom ? 3 : 0));
			var sb = new StringBuilder(s.Length);
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				switch (c)
				{
					case '\t':
					case '\n':
					case '\r':
					case ' ':
						continue;
				}
				sb.Append(c);
			}
			return sb.ToString();
		}
		#endregion

		static readonly Lazy<(bool longPathsEnabled, int maxPathLength, int maxSegmentLength)> longPathSupport =
			new Lazy<(bool longPathsEnabled, int maxPathLength, int maxSegmentLength)>(GetLongPathSupport, isThreadSafe: true);

		static (bool longPathsEnabled, int maxPathLength, int maxSegmentLength) GetLongPathSupport()
		{
			try
			{
				switch (Environment.OSVersion.Platform)
				{
					case PlatformID.MacOSX:
					case PlatformID.Unix:
						return (true, int.MaxValue, 255);
					case PlatformID.Win32NT:
						const string key = @"SYSTEM\CurrentControlSet\Control\FileSystem";
						var fileSystem = Registry.LocalMachine.OpenSubKey(key);
						var value = (int?)fileSystem.GetValue("LongPathsEnabled");
						if (value == 1)
						{
							// There are conflicting information on the max length for Microfost File Systems:
							// 32,760: https://docs.microsoft.com/en-us/windows/win32/fileio/filesystem-functionality-comparison?redirectedfrom=MSDN#limits
							// 32,767 (says value is approximate): https://docs.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation?tabs=cmd
							return (true, 32760, 255);
						}

						// This was what actually worked in a Windows 10 OSBuild 19043.1526, NTFS partition, no Long Paths support enabled.
						// Notice longer file name/paths could be created, but most applications won't be able to read them.
						// On the same system, a file with 256 characters (in C:\) could be read. In any subdirectory, 258 was the absolute
						// path limit, so 255 ends up a reasonable safe value across systems.
						return (false, 258, 255);
					default:
						// For the default, let's use the minimum across the different platforms
						return (false, 258, 255);
				}
			}
			catch
			{
				// For the default, let's use the minimum across the different platforms
				return (false, 258, 255);
			}
		}

		/// <summary>
		/// Cleans up a node name for use as a file name.
		/// </summary>
		public static string CleanUpFileName(string text)
		{
			return CleanUpName(text, separateAtDots: false, lookForExtension: false);
		}

		/// <summary>
		/// Removes invalid characters from file names and reduces their length,
		/// but keeps file extensions and path structure intact.
		/// </summary>
		public static string SanitizeFileName(string fileName)
		{
			return CleanUpName(fileName, separateAtDots: false, lookForExtension: true);
		}

		/// <summary>
		/// Cleans up a node name for use as a file system name. If <paramref name="separateAtDots"/> is active,
		/// dots are seen as segment separators. If <paramref name="lookForExtension"/> is active,
		/// we check for file a extension and try to preserve it when separating paths at dots.
		/// </summary>
		static string CleanUpName(string name, bool separateAtDots, bool lookForExtension, bool replaceInvalidChars = true)
		{
			char invalidCharPlaceholder = '_';

			string ext = string.Empty;
			if (replaceInvalidChars)
			{
				var changed = false;
				string dir, file;
				int pos;

				pos = name.LastIndexOf(Path.DirectorySeparatorChar);
				if (pos >= 0)
				{
					if (pos == name.Length - 1)
					{
						file = string.Empty;
						dir = name;
					}
					else
					{
						file = name.Substring(pos + 1);
						dir = name.Substring(0, pos);
					}
				}
				else
				{
					dir = string.Empty;
					file = name;
				}

				if (dir.Length > 0 && PathRe.IsMatch(dir))
				{
					changed = true;
					dir = PathRe.Replace(dir, invalidCharPlaceholder.ToString());
				}
				if (file.Length > 0 && FileNameRe.IsMatch(file))
				{
					changed = true;
					file = FileNameRe.Replace(file, invalidCharPlaceholder.ToString());
				}

				// Windows has issues handling colon, so we should only accept it as the second character
				// in paths if it meets the "<drive>:\" format, but as this method is not supposed to handle
				// rooted paths at all, let's just replace it
				if (Environment.OSVersion.Platform == PlatformID.Win32NT)
				{
					if (Regex.IsMatch(name, ":"))
					{
						changed = true;
						file.Replace(':', invalidCharPlaceholder);
						ext.Replace(':', invalidCharPlaceholder);
					}
				}

				// This shall raise an exception if we failed to clean up the path!
				if (changed)
					name = Path.Combine(dir, file);
			}

			string cleanName = name;

			if (separateAtDots)
			{
				if (lookForExtension)
				{
					ext = Path.GetExtension(name);
					cleanName = name.Substring(0, name.Length - ext.Length);
				}
				cleanName = cleanName.Replace('.', Path.DirectorySeparatorChar);
			}

			if (IsReservedFileSystemName(cleanName + ext))
				cleanName += invalidCharPlaceholder;
			else if (name == ".")
				cleanName = invalidCharPlaceholder.ToString();

			return cleanName + ext;
		}

		/// <summary>
		/// Cleans up a node name for use as a directory name.
		/// </summary>
		public static string CleanUpDirectoryName(string text)
		{
			return CleanUpName(text, separateAtDots: false, lookForExtension: false);
		}

		public static string CleanUpPath(string text)
		{
			return CleanUpName(text, separateAtDots: true, lookForExtension: false)
				.Replace('.', Path.DirectorySeparatorChar);
		}

		static bool IsReservedFileSystemName(string name)
		{
			switch (name.ToUpperInvariant())
			{
				case "AUX":
				case "COM1":
				case "COM2":
				case "COM3":
				case "COM4":
				case "COM5":
				case "COM6":
				case "COM7":
				case "COM8":
				case "COM9":
				case "CON":
				case "LPT1":
				case "LPT2":
				case "LPT3":
				case "LPT4":
				case "LPT5":
				case "LPT6":
				case "LPT7":
				case "LPT8":
				case "LPT9":
				case "NUL":
				case "PRN":
					return true;
				default:
					return false;
			}
		}

		public static bool CanUseSdkStyleProjectFormat(PEFile module)
		{
			return TargetServices.DetectTargetFramework(module).Moniker != null;
		}

		#region Full path length checking helpers

		static readonly bool UnderWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

		private DirectoryInfo CreateDir(string path) => Directory.CreateDirectory(ValidatePath(path, true));

		private void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, ValidatePath(destination), overwrite);

		private FileStream MakeFileStream(string path, FileMode mode, FileAccess access) => new FileStream(ValidatePath(path), mode, access);

		private StreamWriter MakeStreamWriter(string path) => new StreamWriter(ValidatePath(path));

		/// <summary>
		/// Validates whether a path is valid for a given file system
		/// </summary>
		/// <param name="path">Absolute path to validate.</param>
		private string ValidatePath(string path, bool directory = false)
		{
			var (supportsLongPaths, maxPathLength, maxSegmentLength) = longPathSupport.Value;
			if (!Path.IsPathRooted(path))
				throw new Exception("Non-root path passed to ValidatePath().");

			string dotnetPath;
			try
			{
				// FileSystemInfo siblings shall throw an exception in most cases where the path is not valid.
				if (directory)
				{
					if (UnderWindows)
					{
						// If OS is Windows, the maximum path should be deduced by 12 characters to ensure
						// 8.3 file-extensions would fit the created directory.
						// https://docs.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation?tabs=cmd
						dotnetPath = new FileInfo(Path.Combine(path, "filename.ext")).DirectoryName;

					}
					else
					{
						dotnetPath = new DirectoryInfo(path).FullName;
					}
				}
				else
				{
					dotnetPath = new FileInfo(path).FullName;
				}
			}
			catch (Exception ex)
			{
				if (path.Length > maxPathLength)
				{
					throw new PathTooLongException("Path too long. Max: " + maxPathLength + " - Length: " + path.Length + Environment.NewLine +
						"Path: " + path, ex);
				}
				throw new Exception("Invalid path: " + path, ex);
			}

			if (!dotnetPath.StartsWith(TargetDirectory))
				throw new Exception("Path resolved outside of output directory: " + path + Environment.NewLine +
					"Resolved to: " + dotnetPath + Environment.NewLine +
					"Output directory: " + TargetDirectory);

			if (dotnetPath.Length > maxPathLength)
			{
				if (UnderWindows && !supportsLongPaths)
				{
					throw new PathTooLongException("Path is too long. Files could be created, but they won't be accessible by most applications." + Environment.NewLine +
						"Path: " + dotnetPath + Environment.NewLine +
						"Length: " + dotnetPath.Length + " - Maximum allowed: " + maxPathLength);
				}
				else
					throw new PathTooLongException("Path is too long (" + dotnetPath.Length + " characters). ILSpy is configured not to allow paths with more than " +
						maxPathLength + " characters on this system." + Environment.NewLine +
						"Path: " + dotnetPath);
			}

			return dotnetPath;
		}

		void WriteBytesTo(string path, byte[] sequence) => File.WriteAllBytes(ValidatePath(path), sequence);

		#endregion Path checking helpers
	}

	public readonly struct DecompilationProgress
	{
		public readonly int TotalNumberOfFiles;
		public readonly string Status;

		public DecompilationProgress(int total, string status = null)
		{
			this.TotalNumberOfFiles = total;
			this.Status = status ?? "";
		}
	}
}
