using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Sentry.Extensibility;
using Sentry.Unity.Integrations;
using UnityEditor;

namespace Sentry.Unity.Editor.Android
{
    internal class DebugSymbolUpload
    {
        private readonly IDiagnosticLogger _logger;

        internal const string RelativeBuildOutputPathOld = "Temp/StagingArea/symbols";
        internal const string RelativeBuildOutputPathOldMono = "Temp/StagingArea/symbols";
        internal const string RelativeGradlePathOld = "Temp/gradleOut";
        internal const string RelativeBuildOutputPathNew = "Library/Bee/artifacts/Android";
        internal const string RelativeAndroidPathNew = "Library/Bee/Android";

        private readonly string _unityProjectPath;
        private readonly string _gradleProjectPath;
        private readonly string _gradleScriptPath;
        private readonly ScriptingImplementation _scriptingBackend;
        private readonly bool _isExporting;

        private readonly SentryCliOptions? _cliOptions;
        internal List<string> _symbolUploadPaths;

        private const string SymbolUploadTaskStartComment = "// Autogenerated Sentry symbol upload task [start]";
        private const string SymbolUploadTaskEndComment = "// Autogenerated Sentry symbol upload task [end]";

        private string _symbolUploadTask
        {
            get
            {
                var text = "";
                text += "// Credentials and project settings information are stored in the sentry.properties file\n";
                text += "gradle.taskGraph.whenReady {{\n";
                text += "    gradle.taskGraph.allTasks[-1].doLast {{\n";
                if (_isExporting)
                {
                    text += "        println 'Uploading symbols to Sentry.'\n";
                }
                else
                {
                    var logsDir = $"{ConvertSlashes(_unityProjectPath)}/Logs";
                    Directory.CreateDirectory(logsDir);
                    text += "        println 'Uploading symbols to Sentry. You can find the full log in ./Logs/sentry-symbols-upload.log (the file content may not be strictly sequential because it\\'s a merge of two streams).'\n";
                    text += $"        def sentryLogFile = new FileOutputStream('{logsDir}/sentry-symbols-upload.log')\n";
                }
                text += "        exec {{\n";
                text += "            environment 'SENTRY_PROPERTIES', './sentry.properties'\n";
                text += "            executable '{0}'\n";
                text += "            args = ['upload-dif'{1}]\n";
                if (!_isExporting)
                {
                    text += "            standardOutput sentryLogFile\n";
                    text += "            errorOutput sentryLogFile\n";
                }
                text += "        }}\n";
                text += "    }}\n";
                text += "}}";
                return text;
            }
        }// ConvertSlashes(_unityProjectPath)

        public DebugSymbolUpload(IDiagnosticLogger logger,
            SentryCliOptions? cliOptions,
            string unityProjectPath,
            string gradleProjectPath,
            ScriptingImplementation scriptingBackend,
            bool isExporting = false,
            IApplication? application = null)
        {
            _logger = logger;

            _unityProjectPath = unityProjectPath;
            _gradleProjectPath = gradleProjectPath;
            _gradleScriptPath = Path.Combine(_gradleProjectPath, "build.gradle");
            _scriptingBackend = scriptingBackend;
            _isExporting = isExporting;

            _cliOptions = cliOptions;
            _symbolUploadPaths = GetSymbolUploadPaths(application);
        }

        public void AppendUploadToGradleFile(string sentryCliPath)
        {
            if (LoadGradleScript().Contains("sentry.properties"))
            {
                _logger.LogDebug("Symbol upload has already been added in a previous build.");
                return;
            }

            _logger.LogInfo("Appending debug symbols upload task to gradle file.");

            sentryCliPath = ConvertSlashes(sentryCliPath);
            if (!File.Exists(sentryCliPath))
            {
                throw new FileNotFoundException("Failed to find sentry-cli", sentryCliPath);
            }

            var uploadDifArguments = ", '--il2cpp-mapping'";
            if (_cliOptions?.UploadSources ?? false)
            {
                uploadDifArguments += ", '--include-sources'";
            }

            if (_isExporting)
            {
                uploadDifArguments += ", project.rootDir";
                sentryCliPath = $"./{Path.GetFileName(sentryCliPath)}";
            }
            else
            {
                foreach (var symbolUploadPath in _symbolUploadPaths)
                {
                    if (Directory.Exists(symbolUploadPath))
                    {
                        uploadDifArguments += $", '{ConvertSlashes(symbolUploadPath)}'";
                    }
                    else
                    {
                        throw new DirectoryNotFoundException($"Failed to find the symbols directory at {symbolUploadPath}");
                    }
                }
            }

            using var streamWriter = File.AppendText(_gradleScriptPath);
            streamWriter.WriteLine(SymbolUploadTaskStartComment);
            streamWriter.WriteLine(_symbolUploadTask, sentryCliPath, uploadDifArguments);
            streamWriter.WriteLine(SymbolUploadTaskEndComment);
        }

        private string LoadGradleScript()
        {
            if (!File.Exists(_gradleScriptPath))
            {
                throw new FileNotFoundException($"Failed to find the gradle config.", _gradleScriptPath);
            }
            return File.ReadAllText(_gradleScriptPath);
        }

        public void RemoveUploadFromGradleFile()
        {
            _logger.LogDebug("Removing the upload task from the gradle project.");
            var gradleBuildFile = LoadGradleScript();
            if (!gradleBuildFile.Contains("sentry.properties"))
            {
                _logger.LogDebug("No previous upload task found.");
                return;
            }

            var regex = new Regex(Regex.Escape(SymbolUploadTaskStartComment) + ".*" + Regex.Escape(SymbolUploadTaskEndComment), RegexOptions.Singleline);
            gradleBuildFile = regex.Replace(gradleBuildFile, "");

            using var streamWriter = File.CreateText(_gradleScriptPath);
            streamWriter.Write(gradleBuildFile);
        }

        public void TryCopySymbolsToGradleProject(IApplication? application = null)
        {
            if (!_isExporting)
            {
                return;
            }

            _logger.LogInfo("Copying debug symbols to exported gradle project.");
            var targetRoot = Path.Combine(_gradleProjectPath, "symbols");
            foreach (var symbolUploadPath in _symbolUploadPaths)
            {
                // Seems like not all paths exist all the time... e.g. Unity 2021.2.21 misses RelativeAndroidPathNew.
                if (!Directory.Exists(symbolUploadPath))
                {
                    continue;
                }
                foreach (var sourcePath in Directory.GetFiles(symbolUploadPath, "*.so", SearchOption.AllDirectories))
                {
                    var targetPath = sourcePath.Replace(symbolUploadPath, targetRoot);
                    _logger.LogDebug("Copying '{0}' to '{1}'", sourcePath, targetPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                    File.Copy(sourcePath, targetPath, true);
                }
            }
        }

        internal List<string> GetSymbolUploadPaths(IApplication? application = null)
        {
            var paths = new List<string>();
            if (IsNewBuildingBackend(application))
            {
                _logger.LogInfo("Unity version 2021.2 or newer detected. Root for symbols upload: 'Library'.");
                if (_scriptingBackend == ScriptingImplementation.IL2CPP)
                {
                    paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathNew));
                }
                paths.Add(Path.Combine(_unityProjectPath, RelativeAndroidPathNew));
            }
            else
            {
                _logger.LogInfo("Unity version 2021.1 or older detected. Root for symbols upload: 'Temp'.");
                if (_scriptingBackend == ScriptingImplementation.IL2CPP)
                {
                    paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathOld));
                }
                paths.Add(Path.Combine(_unityProjectPath, RelativeGradlePathOld));
            }
            return paths;
        }

        // Starting from 2021.2 Unity caches the build output inside 'Library' instead of 'Temp'
        internal static bool IsNewBuildingBackend(IApplication? application = null) => SentryUnityVersion.IsNewerOrEqualThan("2021.2", application);

        // Gradle doesn't support backslashes on path (Windows) so converting to forward slashes
        internal static string ConvertSlashes(string path) => path.Replace(@"\", "/");
    }
}
