using System;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;

using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.Metadata;

using NUnit.Framework;

namespace ICSharpCode.Decompiler.Tests
{
	[TestFixture]
	public sealed class PathValidationTests
	{
		static readonly string InputDir = Path.Combine(RoundtripAssembly.TestDir, "Random Tests", "TestCases");

		static readonly string TestCase1AsmPath = Path.Combine(InputDir, "TestCase-1.exe");

		static readonly string RootPathPrefix = Environment.OSVersion.Platform == PlatformID.Win32NT ? @"c:\" : "/";

		// This should have 270 characters.
		public const string ALongFileName = "00_456789_01_456789_02_456789_03_456789_04_456789_05_456789_06_456789_07_456789_08_456789_09_456789_10_456789_11_456789_12_456789_13_456789_14_456789_15_456789_16_456789_17_456789_18_456789_19_456789_20_456789_21_456789_22_456789_23_456789_24_456789_25_456789_26_456789_";

		[Test]
		public void CheckPathValidation()
		{
			using (var fileStream = new FileStream(TestCase1AsmPath, FileMode.Open, FileAccess.Read))
			{
				var decompiler = TestProjectDecompiler.GenerateInstance(fileStream);

				// Act - Fake output dir in WholeProjectDecompiler without actually running it
				var outDirPath = Path.Combine(TestCase1AsmPath, "LongPathTests");
				var outDirInfo = new DirectoryInfo(outDirPath);
				if (outDirInfo.Exists)
					throw new Exception("Long path checks will be tainted, because directory exists: " + outDirInfo.FullName);

				decompiler.FakeTargetDirectory(outDirInfo);

				bool testIsDirectory;
				string pathToTest;
				DirectoryInfo dirInfo;
				FileInfo fileInfo;

				void setTestPath(string testPath, bool file = false)
				{
					if (file)
					{
						fileInfo = new FileInfo(testPath);
						dirInfo = null;
					}
					else
					{
						fileInfo = null;
						dirInfo = new DirectoryInfo(testPath);
					}
					testIsDirectory = !file;
					pathToTest = testPath;
				}

				void setTestDirInfo(DirectoryInfo testDirInfo)
				{
					dirInfo = testDirInfo;
					fileInfo = null;
					testIsDirectory = true;
					pathToTest = testDirInfo.FullName;
				}

				void setTestFileInfo(FileInfo testFileInfo)
				{
					dirInfo = null;
					fileInfo = testFileInfo;
					testIsDirectory = false;
					pathToTest = testFileInfo.FullName;
				}

				// Assert
				setTestDirInfo(new DirectoryInfo(Path.Combine(outDirInfo.FullName, "..", "..")));
				Assert.Throws<Exception>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
					"Passing an absolute path to a directory outside output dir using relative pathing throws exception");

				setTestDirInfo(new DirectoryInfo(Path.Combine(RootPathPrefix, "data", "base")));
				Assert.Throws<Exception>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
					"Passing an absolute path to a directory outside output dir throws exception");

				setTestFileInfo(new FileInfo(Path.Combine(outDirInfo.FullName, "..", "..", "filename.ext")));
				Assert.Throws<Exception>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
					"Passing an absolute path to a file name outside output dir using relative pathing throws exception");

				Assert.Throws<Exception>(delegate { decompiler.WrapValidatePath(pathToTest); },
					"Passing an absolute path to a file name outside output dir throws exception (default validation path type)");

				setTestFileInfo(new FileInfo(Path.Combine(RootPathPrefix, "data", "base", "filename.ext")));
				Assert.Throws<Exception>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
					"Passing an absolute path to a file name outside output dir throws exception");

				setTestPath("filename.ext", true);
				Assert.That(fileInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
					"Relative file name is expanded to full path prepended by the TargetDirectory");

				setTestPath(Path.Combine("some-directory" , "filename.ext"), true);
				Assert.That(fileInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
					"Relative path+file is expanded to full path prepended by the TargetDirectory");

				setTestPath("adir");
				Assert.That(dirInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
					"Relative directory name is expanded to full path prepended by the TargetDirectory");

				var (supportsLongPath, maxPathLen, maxPartLen) = decompiler.GetPathSettings();

				if (Environment.OSVersion.Platform == PlatformID.Win32NT)
				{
					setTestPath(MakeLongPath(outDirInfo.FullName, maxPathLen - 12, maxPartLen));
					Assert.That(dirInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
						"Windows (long paths=" + supportsLongPath + "): able to validate a very long directory name");

					setTestPath(MakeLongPath(outDirInfo.FullName, maxPathLen - 11, maxPartLen));
					Assert.Throws<PathTooLongException>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
						"Windows (long paths=" + supportsLongPath + "): long directory one character short of space for 8.3 allocation throws PathTooLong");

					setTestPath(MakeLongPath(outDirInfo.FullName, maxPathLen, maxPartLen));
					Assert.Throws<PathTooLongException>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
						"Windows (long paths=" + supportsLongPath + "): long directory not considering 8.3 allocation throws PathTooLong");

					setTestPath(MakeLongPath(outDirInfo.FullName, "myfile.txt", maxPathLen, maxPartLen), true);
					Assert.That(fileInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
						"Windows (long paths=" + supportsLongPath + "): able to validate a very long path to a short file name");

					setTestPath(MakeLongPath(outDirInfo.FullName, "filename.ext", maxPathLen, maxPartLen), true);
					Assert.That(fileInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
						"Windows (long paths=" + supportsLongPath + "): able to validate a very long path to an exactly 8.3 file name");

					setTestPath(MakeLongPath(outDirInfo.FullName, "verylongfilenamewithtrailingextensioncharacters.extension", maxPathLen, maxPartLen), true);
					Assert.That(fileInfo.FullName == decompiler.WrapValidatePath(pathToTest, testIsDirectory),
						"Windows (long paths=" + supportsLongPath + "): able to validate a very long path to a file name longer than 8.3");

					setTestPath(MakeLongPath(outDirInfo.FullName, "myfile.txt", maxPathLen + 1, maxPartLen), true);
					Assert.Throws<PathTooLongException>(delegate { decompiler.WrapValidatePath(pathToTest, testIsDirectory); },
						"Windows (long paths=" + supportsLongPath + "): a file name one character over the length limit throws exception");
				}
			}
		}

		string MakeLongPath(string pathPrefix, string pathSuffix, int len, int maxPartLen)
		{
			var longPath = MakeLongPath(pathPrefix, len, maxPartLen);

			longPath = longPath.Substring(0, longPath.Length - 1 - pathSuffix.Length);

			if (longPath.EndsWith(Path.DirectorySeparatorChar))
				longPath = longPath.Substring(0, longPath.Length - 1) + "_";

			longPath += Path.DirectorySeparatorChar + pathSuffix;

			return longPath;
		}

		string MakeLongPath(string pathPrefix, int len, int maxPartLen)
		{
			var longPath = pathPrefix;

			var longPathPart = ALongFileName.Substring(0, maxPartLen);

			while (longPath.Length < len)
				longPath += Path.DirectorySeparatorChar + longPathPart;

			longPath = longPath.Substring(0, len);

			if (longPath.EndsWith(Path.DirectorySeparatorChar))
				longPath = longPath.Substring(0, len - 11) + Path.DirectorySeparatorChar + ALongFileName.Substring(0, 10);

			return longPath;
		}

		class TestProjectDecompiler : WholeProjectDecompiler
		{
			public TestProjectDecompiler(Guid projecGuid, IAssemblyResolver resolver, AssemblyReferenceClassifier assemblyReferenceClassifier, DecompilerSettings settings)
				: base(settings, projecGuid, resolver, assemblyReferenceClassifier, debugInfoProvider: null)
			{
			}

			public static TestProjectDecompiler GenerateInstance(FileStream fileStream)
			{
				// Arrange - Prepare a WholeProjectDecompiler instance
				PEFile module = new PEFile(TestCase1AsmPath, fileStream, PEStreamOptions.PrefetchEntireImage);
				var resolver = new TestAssemblyResolver(TestCase1AsmPath, InputDir, module.Metadata.DetectTargetFrameworkId());
				resolver.AddSearchDirectory(InputDir);
				resolver.RemoveSearchDirectory(".");

				// use a fixed GUID so that we can diff the output between different ILSpy runs without spurious changes
				var projectGuid = Guid.Parse("{E0CB3ED0-C8B5-436C-B67D-41C67430BD82}");

				var settings = new DecompilerSettings(LanguageVersion.CSharp11_0);
				return new TestProjectDecompiler(projectGuid, resolver, resolver, settings);

			}

			static BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
			static BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

			static RT CallPrivateMethod<RT>(WholeProjectDecompiler source, string name, params object[] args) =>
				(RT)typeof(WholeProjectDecompiler).GetMethod(name, PrivateInstance).Invoke(source, args);

			static RT CallPrivateMethod<RT>(string name, params object[] args) =>
				(RT)typeof(WholeProjectDecompiler).GetMethod(name, PrivateStatic).Invoke(null, args);

			static void CallPrivateMethod(WholeProjectDecompiler source, string name, params object[] args) =>
				typeof(WholeProjectDecompiler).GetMethod(name, PrivateInstance).Invoke(source, args);

			public string WrapValidatePath(string path)
			{
				return CallPrivateMethod<string>(this, "ValidatePath", path);
			}

			public string WrapValidatePath(string path, bool directory)
			{
				return CallPrivateMethod<string>(this, "ValidatePath", path, directory);
			}

			bool lps = false;
			int mpl = 0;
			int msl = 0;

			public (bool, int, int) GetPathSettings()
			{
				if (mpl == 0 || msl == 0)
					(lps, mpl, msl) = CallPrivateMethod<Tuple<bool, int, int>>("GetLongPathSupport");

				return (lps, mpl, msl);
			}

			public void FakeTargetDirectory(DirectoryInfo dirInfo)
			{
				TargetDirectory = dirInfo.FullName;
			}
		}

	}
}
