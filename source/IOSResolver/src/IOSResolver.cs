﻿// <copyright file="VersionHandler.cs" company="Google Inc.">
// Copyright (C) 2016 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//    limitations under the License.
// </copyright>

#if UNITY_IOS
using Google;
using GooglePlayServices;
using Google.JarResolver;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Google {

[InitializeOnLoad]
public class IOSResolver : AssetPostprocessor {
    /// <summary>
    /// Reference to a Cocoapod.
    /// </summary>
    private class Pod {
        /// <summary>
        /// Name of the pod.
        /// </summary>
        public string name = null;

        /// <summary>
        /// This is a preformatted version expression for pod declarations.
        ///
        /// See: https://guides.cocoapods.org/syntax/podfile.html#pod
        /// </summary>
        public string version = null;

        /// <summary>
        /// Properties applied to the pod declaration.
        ///
        /// See:
        /// </summary>
        public Dictionary<string, string> propertiesByName = new Dictionary<string, string>();

        /// <summary>
        /// Whether this pod has been compiled with bitcode enabled.
        ///
        /// If any pods are present which have bitcode disabled, bitcode is
        /// disabled for an entire project.
        /// </summary>
        public bool bitcodeEnabled = true;

        /// <summary>
        /// Additional sources (repositories) to search for this pod.
        ///
        /// Since order is important sources specified in this list
        /// are interleaved across each Pod added to the resolver.
        /// e.g Pod1.source[0], Pod2.source[0] ...
        ////    Pod1.source[1], Pod2.source[1] etc.
        ///
        /// See: https://guides.cocoapods.org/syntax/podfile.html#source
        /// </summary>
        public List<string> sources = new List<string>() {
            "https://github.com/CocoaPods/Specs.git"
        };

        /// <summary>
        /// Minimum target SDK revision required by this pod.
        /// In the form major.minor
        /// </summary>
        public string minTargetSdk = null;
        
        /// <summary>
        /// The name of the target that this pod should apply to.
        /// For example, "MyAppleWatchExtension"
        /// </summary>
        public string targetName = null;
        
        /// <summary>
        /// The target platform for this pod.
        /// See https://guides.cocoapods.org/syntax/podfile.html#platform for the list of
        /// supported platforms.
        /// </summary>
        public string targetPlatform = DEFAULT_PLATFORM;

        /// <summary>
        /// Tag that indicates where this was created.
        /// </summary>
        public string createdBy = System.Environment.StackTrace;

        /// <summary>
        /// Whether this pod was read from an XML dependencies file.
        /// </summary>
        public bool fromXmlFile = false;

        /// <summary>
        /// Global set of sources for all pods.
        /// </summary>
        public static List<KeyValuePair<string, string>> Sources =
            new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Convert a dictionary of property key / value pairs to a string that can be appended
        /// to a Podfile pod declaration.
        /// </summary>
        public static string PropertyDictionaryToString(
                Dictionary<string, string> propertiesByName) {
            if (propertiesByName == null) return "";
            var propertyNamesAndValues = new List<string>();
            foreach (var propertyItem in propertiesByName) {
                propertyNamesAndValues.Add(String.Format(":{0} => {1}", propertyItem.Key,
                                           propertyItem.Value));
            }
            return String.Join(", ", propertyNamesAndValues.ToArray());
        }

        /// <summary>
        /// Get the path of a pod without quotes.  If the path isn't present, returns an empty
        /// string.
        /// </summary>
        public string LocalPath {
            get {
                string path;
                if (!propertiesByName.TryGetValue("path", out path)) return "";
                if (path.StartsWith("'") && path.EndsWith("'")) {
                    path = path.Substring(1, path.Length - 2);
                }
                return path;
            }
        }


        /// <summary>
        /// Format a "pod" line for a Podfile.
        /// </summary>
        public string PodFilePodLine {
            get {
                string podLine = String.Format("pod '{0}'", name);
                if (!String.IsNullOrEmpty(version)) podLine += String.Format(", '{0}'", version);

                var outputPropertiesByName = new Dictionary<string, string>(propertiesByName);
                var path = LocalPath;
                if (!String.IsNullOrEmpty(path)) {
                    outputPropertiesByName["path"] = String.Format("'{0}'", Path.GetFullPath(path));
                }
                var propertiesString = PropertyDictionaryToString(outputPropertiesByName);
                if (!String.IsNullOrEmpty(propertiesString)) podLine += ", " + propertiesString;

                return podLine;
            }
        }

        /// <summary>
        /// Create a pod reference.
        /// </summary>
        /// <param name="name">Name of the pod.</param>
        /// <param name="version">Version of the pod.</param>
        /// <param name="bitcodeEnabled">Whether this pod was compiled with
        /// bitcode.</param>
        /// <param name="minTargetSdk">Minimum target SDK revision required by
        /// this pod.</param>
        /// <param name="sources">List of sources to search for all pods.
        /// Each source is a URL that is injected in the source section of a Podfile
        /// See https://guides.cocoapods.org/syntax/podfile.html#source for the description of
        /// a source.</param>
        /// <param name="propertiesByName">Dictionary of additional properties for the pod
        /// reference.</param>
        /// <param name="targetName">The name of the target that this pod should apply to.
        /// For example, "MyAppleWatchExtension"</param>
        /// <param name="targetPlatform">The target platform for this pod.
        /// See https://guides.cocoapods.org/syntax/podfile.html#platform for the list of
        /// supported platforms.</param>
        public Pod(string name, string version, bool bitcodeEnabled, string minTargetSdk,
                   IEnumerable<string> sources, Dictionary<string, string> propertiesByName,
                   string targetName, string targetPlatform) {
            this.name = name;
            this.version = version;
            if (propertiesByName != null) {
                this.propertiesByName = new Dictionary<string, string>(propertiesByName);
            }
            this.bitcodeEnabled = bitcodeEnabled;
            this.minTargetSdk = minTargetSdk;
            this.targetName = targetName ?? TARGET_NAME;
            if (!String.IsNullOrEmpty(targetPlatform)) {
                this.targetPlatform = targetPlatform;
            }
            if (sources != null) {
                var allSources = new List<string>(sources);
                allSources.AddRange(this.sources);
                this.sources = allSources;
            }
        }

        /// <summary>
        /// Convert min target SDK to an integer in the form
        // (major * 10) + minor.
        /// </summary>
        /// <return>Numeric minimum SDK revision required by this pod.</return>
        public int MinTargetSdkToVersion() {
            string sdkString =
                String.IsNullOrEmpty(minTargetSdk) ? "0.0" : minTargetSdk;
            if (!sdkString.Contains(".")) {
                sdkString = sdkString + ".0";
            }
            return IOSResolver.TargetSdkStringToVersion(sdkString);
        }

        /// <summary>
        /// Compare with this object.
        /// This only compares values that can be encoded into a pod line in a Podfile.
        /// </summary>
        /// <param name="obj">Object to compare with.</param>
        /// <returns>true if both objects have the same contents, false otherwise.</returns>
        public override bool Equals(System.Object obj) {
            var pod = obj as Pod;
            return pod != null &&
                   name == pod.name &&
                   version == pod.version &&
                   propertiesByName.Count == pod.propertiesByName.Count &&
                   targetName == pod.targetName &&
                   targetPlatform == pod.targetPlatform &&
                   propertiesByName.Keys.All(key =>
                       pod.propertiesByName.ContainsKey(key) &&
                       propertiesByName[key] == pod.propertiesByName[key]);
        }

        /// <summary>
        /// Generate a hash of this object.
        /// </summary>
        /// <returns>Hash of this object.</returns>
        public override int GetHashCode() {
            int hash = 0;
            if (name != null) hash ^= name.GetHashCode();
            if (version != null) hash ^= version.GetHashCode();
            foreach (var item in propertiesByName) hash ^= item.GetHashCode();
            return hash;
        }

        /// <summary>
        /// Given a list of pods bucket them into a dictionary sorted by
        /// min SDK version.  Pods which specify no minimum version (e.g 0)
        /// are ignored.
        /// </summary>
        /// <param name="pods">Enumerable of pods to query.</param>
        /// <returns>Sorted dictionary of lists of pod names bucketed by
        /// minimum required SDK version.</returns>
        public static SortedDictionary<int, List<string>>
                BucketByMinSdkVersion(IEnumerable<Pod> pods) {
            var buckets = new SortedDictionary<int, List<string>>();
            foreach (var pod in pods) {
                int minVersion = pod.MinTargetSdkToVersion();
                if (minVersion == 0) {
                    continue;
                }
                List<string> nameList = null;
                if (!buckets.TryGetValue(minVersion, out nameList)) {
                    nameList = new List<string>();
                }
                nameList.Add(pod.name);
                buckets[minVersion] = nameList;
            }
            return buckets;
        }
    }

    private class IOSXmlDependencies : XmlDependencies {

        // Properties to parse from a XML pod specification and store in the propert1iesByName
        // dictionary of the Pod class. These are eventually expanded to the named arguments of the
        // pod declaration in a Podfile.
        // The value of each attribute with the exception of "path" is included as-is.
        // "path" is converted to a full path on the local filesystem when the Podfile is generated.
        private static string[] PODFILE_POD_PROPERTIES = new string[] {
            "configurations",
            "configuration",
            "modular_headers",
            "source",
            "subspecs",
            "path"
        };

        public IOSXmlDependencies() {
            dependencyType = "iOS dependencies";
        }

        /// <summary>
        /// Read XML declared dependencies.
        /// </summary>
        /// <param name="filename">File to read.</param>
        /// <param name="logger">Logger to log with.</param>
        ///
        /// Parses dependencies in the form:
        ///
        /// <dependencies>
        ///   <iosPods>
        ///     <iosPod name="name"
        ///             path="pathToLocal"
        ///             version="versionSpec"
        ///             bitcodeEnabled="enabled"
        ///             minTargetSdk="sdk">
        ///       <sources>
        ///         <source>uriToPodSource</source>
        ///       </sources>
        ///     </iosPod>
        ///   </iosPods>
        /// </dependencies>
        protected override bool Read(string filename, Logger logger) {
            IOSResolver.Log(String.Format("Reading iOS dependency XML file {0}", filename),
                            verbose: true);
            var sources = new List<string>();
            var trueStrings = new HashSet<string> { "true", "1" };
            var falseStrings = new HashSet<string> { "false", "0" };
            string podName = null;
            string versionSpec = null;
            bool bitcodeEnabled = true;
            string minTargetSdk = null;
            string targetName = null;
            string targetPlatform = null;
            var propertiesByName = new Dictionary<string, string>();
            if (!XmlUtilities.ParseXmlTextFileElements(
                filename, logger,
                (reader, elementName, isStart, parentElementName, elementNameStack) => {
                    if (elementName == "dependencies" && parentElementName == "") {
                        return true;
                    } else if (elementName == "iosPods" &&
                               (parentElementName == "dependencies" ||
                                parentElementName == "")) {
                        return true;
                    } else if (elementName == "iosPod" &&
                               parentElementName == "iosPods") {
                        if (isStart) {
                            podName = reader.GetAttribute("name");
                            propertiesByName = new Dictionary<string, string>();
                            foreach (var propertyName in PODFILE_POD_PROPERTIES) {
                                string propertyValue = reader.GetAttribute(propertyName);
                                if (!String.IsNullOrEmpty(propertyValue)) {
                                    propertiesByName[propertyName] = propertyValue;
                                }
                            }
                            versionSpec = reader.GetAttribute("version");
                            var bitcodeEnabledString =
                                (reader.GetAttribute("bitcode") ?? "").ToLower();
                            bitcodeEnabled |= trueStrings.Contains(bitcodeEnabledString);
                            bitcodeEnabled &= !falseStrings.Contains(bitcodeEnabledString);
                            minTargetSdk = reader.GetAttribute("minTargetSdk");
                            targetName = reader.GetAttribute("targetName");
                            targetPlatform = reader.GetAttribute("targetPlatform");
                            sources = new List<string>();
                            if (podName == null) {
                                logger.Log(
                                    String.Format("Pod name not specified while reading {0}:{1}\n",
                                                  filename, reader.LineNumber),
                                    level: LogLevel.Warning);
                                return false;
                            }
                        } else {
                            AddPodInternal(podName, preformattedVersion: versionSpec,
                                           bitcodeEnabled: bitcodeEnabled,
                                           minTargetSdk: minTargetSdk,
                                           targetName:targetName,
                                           targetPlatform:targetPlatform,
                                           sources: sources,
                                           overwriteExistingPod: false,
                                           createdBy: String.Format("{0}:{1}",
                                                                    filename, reader.LineNumber),
                                           fromXmlFile: true,
                                           propertiesByName: propertiesByName);
                        }
                        return true;
                    } else if (elementName == "sources" &&
                               parentElementName == "iosPod") {
                        return true;
                    } else if (elementName == "sources" &&
                               parentElementName == "iosPods") {
                        if (isStart) {
                            sources = new List<string>();
                        } else {
                            foreach (var source in sources) {
                                Pod.Sources.Add(
                                    new KeyValuePair<string, string>(
                                        source, String.Format("{0}:{1}", filename,
                                                              reader.LineNumber)));
                            }
                        }
                        return true;
                    } else if (elementName == "source" &&
                               parentElementName == "sources") {
                        if (isStart && reader.Read() && reader.NodeType == XmlNodeType.Text) {
                            sources.Add(reader.ReadContentAsString());
                        }
                        return true;
                    }
                    return false;
                })) {
                return false;
            }
            return true;
        }
    }

    // Two-Dimensional Dictionary of pods to install in the generated Xcode project.
    // <string: Target Name, <string: Pod Name, Pod: Pod Object>>
    private static SortedDictionary<string, SortedDictionary<string, Pod>> pods =
        new SortedDictionary<string, SortedDictionary<string, Pod>>();

    // Order of post processing operations.
    private const int BUILD_ORDER_REFRESH_DEPENDENCIES = 1;
    private const int BUILD_ORDER_CHECK_COCOAPODS_INSTALL = 2;
    private const int BUILD_ORDER_PATCH_PROJECT = 3;
    private const int BUILD_ORDER_GEN_PODFILE = 4;
    private const int BUILD_ORDER_INSTALL_PODS = 5;
    private const int BUILD_ORDER_UPDATE_DEPS = 6;

    // This is appended to the Podfile filename to store a backup of the original Podfile.
    // ie. "Podfile_Unity".
    private const string UNITY_PODFILE_BACKUP_POSTFIX = "_Unity.backup";

    // Installation instructions for the CocoaPods command line tool.
    private const string COCOAPOD_INSTALL_INSTRUCTIONS = (
        "You can install CocoaPods with the Ruby gem package manager:\n" +
        " > sudo gem install -n /usr/local/bin cocoapods\n" +
        " > pod setup");

    // Pod executable filename.
    private static string POD_EXECUTABLE = "pod";
    // Default paths to search for the "pod" command before falling back to
    // querying the Ruby Gem tool for the environment.
    private static string[] POD_SEARCH_PATHS = new string[] {
        "/usr/local/bin",
        "/usr/bin",
    };
    // Ruby Gem executable filename.
    private static string GEM_EXECUTABLE = "gem";

    /// <summary>
    /// Name of the Xcode project generated by Unity.
    /// </summary>
    public const string PROJECT_NAME = "Unity-iPhone";

    /// <summary>
    /// Main executable target of the Xcode project generated by Unity.
    /// </summary>
    public static string TARGET_NAME = null;
    
    // Default target platform
    private const string DEFAULT_PLATFORM = "ios";
    // Keys in the editor preferences which control the behavior of this module.
    private const string PREFERENCE_NAMESPACE = "Google.IOSResolver.";
    // Whether Legacy Cocoapod installation (project level) is enabled.
    private const string PREFERENCE_COCOAPODS_INSTALL_ENABLED = PREFERENCE_NAMESPACE + "Enabled";
    // Whether Cocoapod uses project files, workspace files, or none (Unity 5.6+ only)
    private const string PREFERENCE_COCOAPODS_INTEGRATION_METHOD =
        PREFERENCE_NAMESPACE + "CocoapodsIntegrationMethod";
    // Whether the Podfile generation is enabled.
    private const string PREFERENCE_PODFILE_GENERATION_ENABLED =
        PREFERENCE_NAMESPACE + "PodfileEnabled";
    // Whether verbose logging is enabled.
    private const string PREFERENCE_VERBOSE_LOGGING_ENABLED =
        PREFERENCE_NAMESPACE + "VerboseLoggingEnabled";
    // Whether execution of the pod tool is performed via the shell.
    private const string PREFERENCE_POD_TOOL_EXECUTION_VIA_SHELL_ENABLED =
        PREFERENCE_NAMESPACE + "PodToolExecutionViaShellEnabled";
    // Whether to try to install Cocoapods tools when iOS is selected as the target platform.
    private const string PREFERENCE_AUTO_POD_TOOL_INSTALL_IN_EDITOR =
        PREFERENCE_NAMESPACE + "AutoPodToolInstallInEditor";
    // A nag prompt disabler setting for turning on workspace integration.
    private const string PREFERENCE_WARN_UPGRADE_WORKSPACE =
        PREFERENCE_NAMESPACE + "UpgradeToWorkspaceWarningDisabled";
    // Whether to skip pod install when using workspace integration.
    private const string PREFERENCE_SKIP_POD_INSTALL_WHEN_USING_WORKSPACE_INTEGRATION =
        PREFERENCE_NAMESPACE + "SkipPodInstallWhenUsingWorkspaceIntegration";
    // List of preference keys, used to restore default settings.
    private static string[] PREFERENCE_KEYS = new [] {
        PREFERENCE_COCOAPODS_INSTALL_ENABLED,
        PREFERENCE_COCOAPODS_INTEGRATION_METHOD,
        PREFERENCE_PODFILE_GENERATION_ENABLED,
        PREFERENCE_VERBOSE_LOGGING_ENABLED,
        PREFERENCE_POD_TOOL_EXECUTION_VIA_SHELL_ENABLED,
        PREFERENCE_AUTO_POD_TOOL_INSTALL_IN_EDITOR,
        PREFERENCE_WARN_UPGRADE_WORKSPACE,
        PREFERENCE_SKIP_POD_INSTALL_WHEN_USING_WORKSPACE_INTEGRATION
    };

    // Whether the xcode extension was successfully loaded.
    private static bool iOSXcodeExtensionLoaded = true;
    // Whether a functioning Cocoapods install is present.
    private static bool cocoapodsToolsInstallPresent = false;

    private static string IOS_PLAYBACK_ENGINES_PATH =
        Path.Combine("PlaybackEngines", "iOSSupport");

    // Directory containing downloaded CocoaPods relative to the project
    // directory.
    private const string PODS_DIR = "Pods";
    // Name of the project within PODS_DIR that references downloaded CocoaPods.
    private const string PODS_PROJECT_NAME = "Pods";
    // Prefix for static library filenames.
    private const string LIBRARY_FILENAME_PREFIX = "lib";
    // Extension for static library filenames.
    private const string LIBRARY_FILENAME_EXTENSION = ".a";
    // Pod variable that references the a source pod's root directory which is analogous to the
    // Xcode $(SRCROOT) variable.
    private const string PODS_VAR_TARGET_SRCROOT = "${PODS_TARGET_SRCROOT}";

    // Version of the CocoaPods installation.
    private static string podsVersion = "";

    private static string PODFILE_GENERATED_COMMENT = "# IOSResolver Generated Podfile";

    // Default iOS target SDK if the selected version is invalid.
    private const int DEFAULT_TARGET_SDK = 82;
    // Valid iOS target SDK version.
    private static Regex TARGET_SDK_REGEX = new Regex("^[0-9]+\\.[0-9]$");

    // Current window being used for a long running shell command.
    private static CommandLineDialog commandLineDialog = null;
    // Mutex for access to commandLineDialog.
    private static System.Object commandLineDialogLock = new System.Object();

    // Regex that matches a "pod" specification line in a pod file.
    // This matches the syntax...
    // pod POD_NAME, OPTIONAL_VERSION, :PROPERTY0 => VALUE0 ... , :PROPERTYN => VALUE0
    private static Regex PODFILE_POD_REGEX =
        new Regex(
            // Extract the Cocoapod name and store in the podname group.
            @"^\s*pod\s+'(?<podname>[^']+)'\s*" +
            // Extract the version field and store in the podversion group.
            @"(,\s*'(?<podversion>[^']+)')?" +
            // Match the end of the line or a list of property & value pairs.
            // Property & value pairs are stored in propertyname / propertyvalue groups.
            // Subsequent values are stored in the Captures property of each Group in the order
            // they're matched.  For example...
            // 1 => 2, 3 => 4
            // associates Capture objects with values 1, 3 with group "propertyname" and
            // Capture objects with values 2, 4 with group "propertyvalue".
            @"(|" +
                @"(,\s*:(?<propertyname>[^\s]+)\s*=>\s*(" +
                    // Unquoted property values.
                    @"(?<propertyvalue>[^\s,]+)\s*|" +
                    // Quoted string of the form 'foo'.
                    @"(?<propertyvalue>'[^']+')\s*|" +
                    // List of the form [1, 2, 3].
                    @"(?<propertyvalue>\[[^\]]+\])\s*" +
                @"))+" +
            @")" +
            @"$");

    // Parses a source URL from a Podfile.
    private static Regex PODFILE_SOURCE_REGEX = new Regex(@"^\s*source\s+'([^']*)'");

    // Parses dependencies from XML dependency files.
    private static IOSXmlDependencies xmlDependencies = new IOSXmlDependencies();

    // Project level settings for this module.
    private static ProjectSettings settings = new ProjectSettings(PREFERENCE_NAMESPACE);

    // Search for a file up to a maximum search depth stopping the
    // depth first search each time the specified file is found.
    private static List<string> FindFile(
            string searchPath, string fileToFind, int maxDepth,
            int currentDepth = 0) {
        if (Path.GetFileName(searchPath) == fileToFind) {
            return new List<string> { searchPath };
        } else if (maxDepth == currentDepth) {
            return new List<string>();
        }
        var foundFiles = new List<string>();
        foreach (var file in Directory.GetFiles(searchPath)) {
            if (Path.GetFileName(file) == fileToFind) {
                foundFiles.Add(file);
            }
        }
        foreach (var dir in Directory.GetDirectories(searchPath)) {
            foundFiles.AddRange(FindFile(dir, fileToFind, maxDepth,
                                         currentDepth: currentDepth + 1));
        }
        return foundFiles;
    }

    // Try to load the Xcode editor extension.
    private static Assembly ResolveUnityEditoriOSXcodeExtension(
            object sender, ResolveEventArgs args)
    {
        // Ignore null assembly references.
        if (String.IsNullOrEmpty(args.Name)) return null;
        // The UnityEditor.iOS.Extensions.Xcode.dll has the wrong name baked
        // into the assembly so references end up resolving as
        // Unity.iOS.Extensions.Xcode.  Catch this and redirect the load to
        // the UnityEditor.iOS.Extensions.Xcode.
        string assemblyName;
        try {
            assemblyName = (new AssemblyName(args.Name)).Name;
        } catch (Exception exception) {
            // AssemblyName can throw if the DLL isn't found so try falling back to parsing the
            // assembly name manually from the fully qualified name.
            if (!(exception is FileLoadException ||
                  exception is IOException)) {
                throw exception;
            }
            assemblyName = args.Name.Split(new [] {','})[0];
        }
        if (!(assemblyName.Equals("Unity.iOS.Extensions.Xcode") ||
              assemblyName.Equals("UnityEditor.iOS.Extensions.Xcode"))) {
            return null;
        }
        Log("Trying to load assembly: " + assemblyName, verbose: true);
        iOSXcodeExtensionLoaded = false;
        string fixedAssemblyName =
            assemblyName.Replace("Unity.", "UnityEditor.") + ".dll";
        Log("Redirecting to assembly name: " + fixedAssemblyName,
            verbose: true);

        // Get the managed DLLs folder.
        string folderPath = Path.GetDirectoryName(
            Assembly.GetAssembly(
                typeof(UnityEditor.AssetPostprocessor)).Location);
        // Try searching a common install location.
        folderPath = Path.Combine(
            (new DirectoryInfo(folderPath)).Parent.FullName,
            IOS_PLAYBACK_ENGINES_PATH);
        string assemblyPath = Path.Combine(folderPath, fixedAssemblyName);
        if (!File.Exists(assemblyPath)) {
            string searchPath = (new DirectoryInfo(folderPath)).FullName;
            if (UnityEngine.RuntimePlatform.OSXEditor ==
                UnityEngine.Application.platform) {
                // Unity likes to move their DLLs around between releases to
                // keep us on our toes, so search for the DLL under the
                // package path.
                searchPath = Path.GetDirectoryName(
                    searchPath.Substring(0, searchPath.LastIndexOf(".app")));
            } else {
                // Search under the Data directory.
                searchPath = Path.GetDirectoryName(
                    searchPath.Substring(
                        0, searchPath.LastIndexOf(
                            "Data" + Path.DirectorySeparatorChar.ToString())));
            }
            Log("Searching for assembly under " + searchPath, verbose: true);
            var files = FindFile(searchPath, fixedAssemblyName, 5);
            if (files.Count > 0) assemblyPath = files.ToArray()[0];
        }
        // Try to load the assembly.
        if (!File.Exists(assemblyPath)) {
            Log(assemblyPath + " does not exist", verbose: true);
            return null;
        }
        Log("Loading " + assemblyPath, verbose: true);
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        if (assembly != null) {
            Log("Load succeeded from " + assemblyPath, verbose: true);
            iOSXcodeExtensionLoaded = true;
        }
        return assembly;
    }

    /// <summary>
    /// Initialize the module.
    /// </summary>
    static IOSResolver() {
        // NOTE: We can't reference the UnityEditor.iOS.Xcode module in this
        // method as the Mono runtime in Unity 4 and below requires all
        // dependencies of a method are loaded before the method is executed
        // so we install the DLL loader first then try using the Xcode module.
        RemapXcodeExtension();
        // NOTE: It's not possible to catch exceptions a missing reference
        // to the UnityEditor.iOS.Xcode assembly in this method as the runtime
        // will attempt to load the assembly before the method is executed so
        // we handle exceptions here.
        try {
            InitializeTargetName();
        } catch (Exception exception) {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS) {
                Log("Failed: " + exception.ToString(), level: LogLevel.Error);
                if (exception is FileNotFoundException ||
                    exception is TypeInitializationException ||
                    exception is TargetInvocationException) {
                    // It's likely we failed to load the iOS Xcode extension.
                    Debug.LogWarning(
                        "Failed to load the " +
                        "UnityEditor.iOS.Extensions.Xcode dll.  " +
                        "Is iOS support installed?");
                } else {
                    throw exception;
                }
            }
        }

        // If Cocoapod tool auto-installation is enabled try installing on the first update of
        // the editor when the editor environment has been initialized.
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS &&
            AutoPodToolInstallInEditorEnabled && CocoapodsIntegrationEnabled &&
            !ExecutionEnvironment.InBatchMode) {
            RunOnMainThread.Run(() => { AutoInstallCocoapods(); }, runNow: false);
        }


        // Prompt the user to use workspaces if they aren't at least using project level
        // integration.
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS &&
            (CocoapodsIntegrationMethod)settings.GetInt(PREFERENCE_COCOAPODS_INTEGRATION_METHOD,
                CocoapodsIntegrationUpgradeDefault) == CocoapodsIntegrationMethod.None &&
            !ExecutionEnvironment.InBatchMode && !UpgradeToWorkspaceWarningDisabled) {

            switch (EditorUtility.DisplayDialogComplex(
                "Warning: CocoaPods integration is disabled!",
                "Would you like to enable CocoaPods integration with workspaces?\n\n" +
                "Unity 5.6+ now supports loading workspaces generated from CocoaPods.\n" +
                "If you enable this, and still use Unity less than 5.6, it will fallback " +
                "to integrating CocoaPods with the .xcodeproj file.\n",
                "Yes", "Not Now", "Silence Warning")) {
                case 0:  // Yes
                    settings.SetInt(PREFERENCE_COCOAPODS_INTEGRATION_METHOD,
                                    (int)CocoapodsIntegrationMethod.Workspace);
                    break;
                case 1:  // Not now
                    break;
                case 2:  // Ignore
                    UpgradeToWorkspaceWarningDisabled = true;
                    break;
            }
        }
    }

    // Display the iOS resolver settings menu.
    [MenuItem("Assets/Play Services Resolver/iOS Resolver/Settings")]
    public static void SettingsDialog() {
        IOSResolverSettingsDialog window = (IOSResolverSettingsDialog)
            EditorWindow.GetWindow(typeof(IOSResolverSettingsDialog), true,
                                   "iOS Resolver Settings");
        window.Initialize();
        window.Show();
    }

    /// <summary>
    /// Initialize the TARGET_NAME property.
    /// </summary>
    private static void InitializeTargetName() {
        TARGET_NAME = UnityEditor.iOS.Xcode.PBXProject.GetUnityTargetName();
    }

    // Fix loading of the Xcode extension dll.
    public static void RemapXcodeExtension() {
        AppDomain.CurrentDomain.AssemblyResolve -=
            ResolveUnityEditoriOSXcodeExtension;
        AppDomain.CurrentDomain.AssemblyResolve +=
            ResolveUnityEditoriOSXcodeExtension;
    }

    /// <summary>
    /// Reset settings of this plugin to default values.
    /// </summary>
    internal static void RestoreDefaultSettings() {
        settings.DeleteKeys(PREFERENCE_KEYS);
    }

    /// <summary>
    /// The method used to integrate Cocoapods with the build.
    /// </summary>
    public enum CocoapodsIntegrationMethod {
        None = 0,
        Project,
        Workspace
    };

    /// <summary>
    /// When first upgrading, decide on workspace integration based on previous settings.
    /// </summary>
    private static int CocoapodsIntegrationUpgradeDefault {
        get {
            return LegacyCocoapodsInstallEnabled ?
                (int)CocoapodsIntegrationMethod.Workspace :
                (int)CocoapodsIntegrationMethod.Project;
        }
    }

    /// <summary>
    /// IOSResolver Unity Preferences setting indicating which CocoaPods integration method to use.
    /// </summary>
    public static CocoapodsIntegrationMethod CocoapodsIntegrationMethodPref {
        get {
            return (CocoapodsIntegrationMethod)settings.GetInt(
                PREFERENCE_COCOAPODS_INTEGRATION_METHOD,
                defaultValue: CocoapodsIntegrationUpgradeDefault);
        }
        set { settings.SetInt(PREFERENCE_COCOAPODS_INTEGRATION_METHOD, (int)value); }
    }

    /// <summary>
    /// Deprecated: Enable / disable CocoaPods installation.
    /// Please use CocoapodsIntegrationEnabled instead.
    /// </summary>
    [System.Obsolete("CocoapodsInstallEnabled is deprecated, please use " +
                     "CocoapodsIntegrationEnabled instead.")]
    public static bool CocoapodsInstallEnabled {
        get { return LegacyCocoapodsInstallEnabled; }
        set { LegacyCocoapodsInstallEnabled = value; }
    }

    /// <summary>
    /// A formerly used setting for project integration.
    /// It's kept as a private function to seed the default for the new setting:
    /// CocoapodsIntegrationEnabled.
    /// </summary>
    private static bool LegacyCocoapodsInstallEnabled {
        get { return settings.GetBool(PREFERENCE_COCOAPODS_INSTALL_ENABLED,
                                         defaultValue: true); }
        set { settings.SetBool(PREFERENCE_COCOAPODS_INSTALL_ENABLED, value); }
    }

    /// <summary>
    /// Enable / disable Podfile generation.
    /// </summary>
    public static bool PodfileGenerationEnabled {
        get { return settings.GetBool(PREFERENCE_PODFILE_GENERATION_ENABLED,
                                         defaultValue: true); }
        set { settings.SetBool(PREFERENCE_PODFILE_GENERATION_ENABLED, value); }
    }

    /// <summary>
    /// Enable / disable execution of the pod tool via the shell.
    /// </summary>
    public static bool PodToolExecutionViaShellEnabled {
        get { return settings.GetBool(PREFERENCE_POD_TOOL_EXECUTION_VIA_SHELL_ENABLED,
                                      defaultValue: true); }
        set { settings.SetBool(PREFERENCE_POD_TOOL_EXECUTION_VIA_SHELL_ENABLED, value); }
    }

    /// <summary>
    /// Enable automated pod tool installation in the editor.  This is only performed when the
    /// editor isn't launched in batch mode.
    /// </summary>
    public static bool AutoPodToolInstallInEditorEnabled {
        get { return settings.GetBool(PREFERENCE_AUTO_POD_TOOL_INSTALL_IN_EDITOR,
                                      defaultValue: true); }
        set { settings.SetBool(PREFERENCE_AUTO_POD_TOOL_INSTALL_IN_EDITOR, value); }
    }

    /// <summary>
    /// Get / set the nag prompt disabler setting for turning on workspace integration.
    /// </summary>
    public static bool UpgradeToWorkspaceWarningDisabled {
        get { return settings.GetBool(PREFERENCE_WARN_UPGRADE_WORKSPACE, defaultValue: false); }
        set { settings.SetBool(PREFERENCE_WARN_UPGRADE_WORKSPACE, value); }
    }

    /// <summary>
    /// Enable / disable verbose logging.
    /// </summary>
    public static bool VerboseLoggingEnabled {
        get { return settings.GetBool(PREFERENCE_VERBOSE_LOGGING_ENABLED, defaultValue: false); }
        set { settings.SetBool(PREFERENCE_VERBOSE_LOGGING_ENABLED, value); }
    }

    /// <summary>
    /// Skip pod install when using workspace integration, let user manually run it.
    /// </summary>
    public static bool SkipPodInstallWhenUsingWorkspaceIntegration {
        get { return settings.GetBool(PREFERENCE_SKIP_POD_INSTALL_WHEN_USING_WORKSPACE_INTEGRATION,
                                      defaultValue: false); }
        set { settings.SetBool(PREFERENCE_SKIP_POD_INSTALL_WHEN_USING_WORKSPACE_INTEGRATION,
                               value); }
    }

    /// <summary>
    /// Whether to use project level settings.
    /// </summary>
    public static bool UseProjectSettings {
        get { return settings.UseProjectSettings; }
        set { settings.UseProjectSettings = value; }
    }

    /// <summary>
    /// Determine whether it's possible to perform iOS dependency injection.
    /// </summary>
    public static bool Enabled { get { return iOSXcodeExtensionLoaded; } }

    private const float epsilon = 1e-7f;

    /// <summary>
    /// Whether or not Unity can load a workspace file if it's present.
    /// </summary>
    private static bool UnityCanLoadWorkspace {
        get {
            // Unity started supporting workspace loading in the released version of Unity 5.6
            // but not in the beta. So check if this is exactly 5.6, but also beta.
            if (Math.Abs(
                    VersionHandler.GetUnityVersionMajorMinor() - 5.6f) < epsilon) {
                // Unity non-beta versions look like 5.6.0f1 while beta versions look like:
                // 5.6.0b11, so looking for the b in the string (especially confined to 5.6),
                // should be sufficient for determining that it's the beta.
                if (UnityEngine.Application.unityVersion.Contains(".0b")) {
                    return false;
                }
            }
            // If Unity was launched from Unity Cloud Build the build pipeline does not
            // open the xcworkspace so we need to force project level integration of frameworks.
            if (System.Environment.CommandLine.Contains("-bvrbuildtarget")) {
                return false;
            }
            return (VersionHandler.GetUnityVersionMajorMinor() >= 5.6f - epsilon);
        }
    }

    /// <summary>
    /// Whether or not we should do Xcode workspace level integration of CocoaPods.
    /// False if the Unity version doesn't support loading workspaces.
    /// </summary>
    private static bool CocoapodsWorkspaceIntegrationEnabled {
        get {
            return UnityCanLoadWorkspace &&
            CocoapodsIntegrationMethodPref == CocoapodsIntegrationMethod.Workspace;
        }
    }

    /// <summary>
    /// Whether or not we should do Xcode project level integration of CocoaPods.
    /// True if configured for project integration or workspace integration is enabled but using
    /// an older version of Unity that doesn't support loading workspaces (as a fallback).
    /// </summary>
    private static bool CocoapodsProjectIntegrationEnabled {
        get {
            return CocoapodsIntegrationMethodPref == CocoapodsIntegrationMethod.Project ||
                (!UnityCanLoadWorkspace &&
                CocoapodsIntegrationMethodPref == CocoapodsIntegrationMethod.Workspace);
        }
    }

    /// <summary>
    /// Whether or not we are integrating the pod dependencies into an Xcode build that Unity loads.
    /// </summary>
    public static bool CocoapodsIntegrationEnabled {
        get {
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS &&
                CocoapodsIntegrationMethodPref != CocoapodsIntegrationMethod.None;
        }
    }

    private delegate void LogMessageDelegate(string message, bool verbose = false,
                                            LogLevel level = LogLevel.Info);

    private static Google.Logger logger = new Google.Logger();

    /// <summary>
    /// Log a message.
    /// </summary>
    /// <param name="message">Message to log.</param>
    /// <param name="verbose">Whether the message should only be displayed if verbose logging is
    /// enabled.</param>
    /// <param name="level">Severity of the message.</param>
    internal static void Log(string message, bool verbose = false,
                             LogLevel level = LogLevel.Info) {
        logger.Level = (VerboseLoggingEnabled || ExecutionEnvironment.InBatchMode) ?
            LogLevel.Verbose : LogLevel.Info;
        logger.Log(message, level: verbose ? LogLevel.Verbose : level);
    }

    /// <summary>
    /// Display a message in a dialog and log to the console.
    /// </summary>
    internal static void LogToDialog(string message, bool verbose = false,
                             LogLevel level = LogLevel.Info) {
        if (!verbose) EditorUtility.DisplayDialog("iOS Resolver", message, "OK");
        Log(message, verbose: verbose, level: level);
    }

    /// <summary>
    /// Determine whether a Pod is present in any targets list of dependencies.
    /// </summary>
    public static bool PodPresent(string pod) {
        foreach (SortedDictionary<string, Pod> podDict in pods.Values) {
            if (new List<string>(podDict.Keys).Contains(pod)) {
                return true;
            }
        }
        return false;
    }
    
    /// <summary>
    /// Determine whether a Pod is present in the list of dependencies for a specific target.
    /// </summary>
    public static bool PodPresentInTarget(string pod,string target) {
        if (pods.TryGetValue(target, out var podDict)) {
            return (new List<string>(podDict.Keys)).Contains(pod);
        }
        return false;
    }

    /// <summary>
    /// Whether to inject iOS dependencies in the Unity generated Xcode
    /// project.
    /// </summary>
    private static bool InjectDependencies() {
        return EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS &&
            Enabled && pods.Count > 0;
    }

    /// <summary>
    /// Convert dependency version specifications to the version expression used by pods.
    /// </summary>
    /// <param name="dependencyVersion">
    /// Version specification string.
    ///
    /// If it ends with "+" the specified version up to the next major
    /// version is selected.
    /// If "LATEST", null or empty this pulls the latest revision.
    /// A version number "1.2.3" selects a specific version number.
    /// </param>
    /// <returns>The version expression formatted for pod dependencies.
    /// For example, "1.2.3+" would become "~> 1.2.3".</returns>
    private static string PodVersionExpressionFromVersionDep(string dependencyVersion) {
        if (String.IsNullOrEmpty(dependencyVersion) || dependencyVersion.Equals("LATEST")) {
            return null;
        }
        if (dependencyVersion.EndsWith("+")) {
            return String.Format("~> {0}",
                dependencyVersion.Substring(0, dependencyVersion.Length - 1));
        }
        return dependencyVersion;
    }

    /// <summary>
    /// Tells the app what pod dependencies are needed.
    /// This is called from a deps file in each API to aggregate all of the
    /// dependencies to automate the Podfile generation.
    /// </summary>
    /// <param name="podName">pod path, for example "Google-Mobile-Ads-SDK" to
    /// be included</param>
    /// <param name="version">Version specification.
    /// See PodVersionExpressionFromVersionDep for how the version string is processed.</param>
    /// <param name="bitcodeEnabled">Whether the pod was compiled with bitcode
    /// enabled.  If this is set to false on a pod, the entire project will
    /// be configured with bitcode disabled.</param>
    /// <param name="minTargetSdk">Minimum SDK revision required by this
    /// pod.</param>
    /// <param name="sources">List of sources to search for all pods.
    /// Each source is a URL that is injected in the source section of a Podfile
    /// See https://guides.cocoapods.org/syntax/podfile.html#source for the description of
    /// a source.</param>
    /// <param name="targetName">The name of the target that this pod should apply to.
    /// For example, "MyAppleWatchExtension"</param>
    /// <param name="targetPlatform">The target platform for this pod.
    /// See https://guides.cocoapods.org/syntax/podfile.html#platform for the list of
    /// supported platforms.</param>
    public static void AddPod(string podName, string version = null,
                              bool bitcodeEnabled = true,
                              string minTargetSdk = null,
                              IEnumerable<string> sources = null,
                              string targetName = null,
                              string targetPlatform = null) {
        AddPodInternal(podName,
                       preformattedVersion: PodVersionExpressionFromVersionDep(version),
                       bitcodeEnabled: bitcodeEnabled, minTargetSdk: minTargetSdk,
                       sources: sources,targetName:targetName,targetPlatform:targetPlatform);
    }

    /// <summary>
    /// Same as AddPod except the version string is used in the pod declaration directly.
    /// See AddPod.
    /// </summary>
    /// <param name="podName">pod path, for example "Google-Mobile-Ads-SDK" to
    /// be included</param>
    /// <param name="preformattedVersion">Podfile version specification similar to what is
    /// returned by PodVersionExpressionFromVersionDep().</param>
    /// <param name="bitcodeEnabled">Whether the pod was compiled with bitcode
    /// enabled.  If this is set to false on a pod, the entire project will
    /// be configured with bitcode disabled.</param>
    /// <param name="minTargetSdk">Minimum SDK revision required by this
    /// pod.</param>
    /// <param name="targetName">The name of the target that this pod should apply to.
    /// For example, "MyAppleWatchExtension"</param>
    /// <param name="targetPlatform">The target platform for this pod.
    /// See https://guides.cocoapods.org/syntax/podfile.html#platform for the list of
    /// supported platforms.</param>
    /// <param name="sources">List of sources to search for all pods.
    /// Each source is a URL that is injected in the source section of a Podfile
    /// See https://guides.cocoapods.org/syntax/podfile.html#source for the description of
    /// a source.</param>
    /// <param name="overwriteExistingPod">Overwrite an existing pod.</param>
    /// <param name="createdBy">Tag of the object that added this pod.</param>
    /// <param name="fromXmlFile">Whether this was added via an XML dependency.</param>
    /// <param name="propertiesByName">Dictionary of additional properties for the pod
    /// reference.</param>
    private static void AddPodInternal(string podName,
                                       string preformattedVersion = null,
                                       bool bitcodeEnabled = true,
                                       string minTargetSdk = null,
                                       string targetName = null,
                                       string targetPlatform = null,
                                       IEnumerable<string> sources = null,
                                       bool overwriteExistingPod = true,
                                       string createdBy = null,
                                       bool fromXmlFile = false,
                                       Dictionary<string, string> propertiesByName = null) {
        var pod = new Pod(podName, preformattedVersion, bitcodeEnabled, minTargetSdk,
                          sources, propertiesByName,targetName,targetPlatform);
        pod.createdBy = createdBy ?? pod.createdBy;
        pod.fromXmlFile = fromXmlFile;

        Log(String.Format(
            "AddPod - name: {0} version: {1} bitcode: {2} sdk: {3} sources: {4}, " +
            "properties: {5}\n" +
            "createdBy: {6}\n" +
            "targetName: {7}\n" +
            "targetPlatform: {8}\n\n",
            podName, preformattedVersion ?? "null", bitcodeEnabled.ToString(),
            minTargetSdk ?? "null",
            sources != null ? String.Join(", ", (new List<string>(sources)).ToArray()) : "(null)",
            Pod.PropertyDictionaryToString(pod.propertiesByName),
            createdBy ?? pod.createdBy,
            targetName ?? "null",
            targetPlatform ?? "null"),
            verbose: true);

        if (!overwriteExistingPod && pods.TryGetValue(pod.targetName, out var targetDict) && 
            targetDict.TryGetValue(podName,out var existingPod)) {
            // Only warn if the existing pod differs to the newly added pod.
            if (!pod.Equals(existingPod)) {
                Log(String.Format("Pod {0} already present, ignoring.\n" +
                                  "Original declaration {1}\n" +
                                  "Ignored declaration {2}\n", podName,
                        existingPod.createdBy, createdBy ?? "(unknown)"),
                    level: LogLevel.Warning);
            }
            return;
        }

        // Create target dictionary if required.
        if (!pods.TryGetValue(pod.targetName, out var updateDict)) {
            pods.Add(pod.targetName,new SortedDictionary<string, Pod>());
        }
        
        pods[pod.targetName][podName] = pod;

        // Only update target SDK if the target is iOS
        if (pod.targetName == DEFAULT_PLATFORM) UpdateTargetSdk(pod);
    }

    /// <summary>
    /// Update the iOS target SDK if it's lower than the minimum SDK
    /// version specified by the pod.
    /// </summary>
    /// <param name="pod">Pod to query for the minimum supported version.
    /// </param>
    /// <param name="notifyUser">Whether to write to the log to notify the
    /// user of a build setting change.</param>
    /// <returns>true if the SDK version was changed, false
    /// otherwise.</returns>
    private static bool UpdateTargetSdk(Pod pod,
                                        bool notifyUser = true) {
        int currentVersion = TargetSdkVersion;
        int minVersion = pod.MinTargetSdkToVersion();
        if (currentVersion >= minVersion) {
            return false;
        }
        if (notifyUser) {
            string oldSdk = TargetSdk;
            TargetSdkVersion = minVersion;
            Log("iOS Target SDK changed from " + oldSdk + " to " +
                TargetSdk + " required by the " + pod.name + " pod");
        }
        return true;
    }

    /// <summary>
    /// Update the target SDK if it's required.
    /// </summary>
    /// <returns>true if the SDK was updated, false otherwise.</returns>
    public static bool UpdateTargetSdk() {
        var minVersionAndPodNames = TargetSdkNeedsUpdate();
        if (minVersionAndPodNames.Value != null) {
            var minVersionString =
                TargetSdkVersionToString(minVersionAndPodNames.Key);
            var update = EditorUtility.DisplayDialog(
                "Unsupported Target SDK",
                "Target SDK selected in the iOS Player Settings (" +
                TargetSdk + ") is not supported by the Cocoapods " +
                "included in this project. " +
                "The build will very likely fail. The minimum supported " +
                "version is \"" + minVersionString + "\" " +
                "required by pods (" +
                String.Join(", ", minVersionAndPodNames.Value.ToArray()) +
                ").\n" +
                "Would you like to update the target SDK version?",
                "Yes", cancel: "No");
            if (update) {
                TargetSdkVersion = minVersionAndPodNames.Key;
                string errorString = (
                    "Target SDK has been updated from " + TargetSdk +
                    " to " + minVersionString + ".  You must restart the " +
                    "build for this change to take effect.");
                EditorUtility.DisplayDialog(
                    "Target SDK updated.", errorString, "OK");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determine whether the target SDK needs to be updated based upon pod
    /// dependencies.
    /// </summary>
    /// <returns>Key value pair of minimum SDK version (key) and
    /// a list of pod names that require it (value) if the currently
    /// selected target SDK version does not satisfy pod requirements, the list
    /// (value) is null otherwise.</returns>
    private static KeyValuePair<int, List<string>> TargetSdkNeedsUpdate() {
        var kvpair = new KeyValuePair<int, List<string>>(0, null);
        
        pods.TryGetValue(DEFAULT_PLATFORM, out var iosPods);
        if (iosPods == null) return kvpair;
        
        var podListsByVersion = Pod.BucketByMinSdkVersion(iosPods.Values);
        if (podListsByVersion.Count == 0) return kvpair;
        
        KeyValuePair<int, List<string>> minVersionAndPodName = kvpair;
        foreach (var versionAndPodList in podListsByVersion) {
            minVersionAndPodName = versionAndPodList;
            break;
        }
        int currentVersion = TargetSdkVersion;
        if (currentVersion >= minVersionAndPodName.Key) {
            return kvpair;
        }
        return minVersionAndPodName;
    }

    // Get the path of an xcode project relative to the specified directory.
    private static string GetProjectPath(string relativeTo,
                                         string projectName) {
        return Path.Combine(relativeTo,
                            Path.Combine(projectName + ".xcodeproj",
                                         "project.pbxproj"));
    }

    /// <summary>
    /// Get the generated xcode project path relative to the specified
    /// directory.
    /// </summary>
    /// <param name="relativeTo">Path the project is relative to.</param>
    public static string GetProjectPath(string relativeTo) {
        return GetProjectPath(relativeTo, PROJECT_NAME);
    }

    /// <summary>
    /// Get or set the Unity iOS target SDK version string (e.g "7.1")
    /// build setting.
    /// </summary>
    static string TargetSdk {
        get {
            string name = null;
            var iosSettingsType = typeof(UnityEditor.PlayerSettings.iOS);
            // Read the version (Unity 5.5 and above).
            var osVersionProperty = iosSettingsType.GetProperty(
                   "targetOSVersionString");
            if (osVersionProperty != null) {
                name = (string)osVersionProperty.GetValue(null, null);
            }
            if (name == null) {
                // Read the version (deprecated in Unity 5.5).
                osVersionProperty = iosSettingsType.GetProperty(
                   "targetOSVersion");
                if (osVersionProperty != null) {
                    var osVersionValue =
                        osVersionProperty.GetValue(null, null);
                    if (osVersionValue != null) {
                        name = Enum.GetName(osVersionValue.GetType(),
                                            osVersionValue);
                    }
                }
            }
            if (String.IsNullOrEmpty(name)) {
                // Versions 8.2 and above do not have enum symbols
                // The values in Unity 5.4.1f1:
                // 8.2 == 32
                // 8.3 == 34
                // 8.4 == 36
                // 9.0 == 38
                // 9.1 == 40
                // Since these are undocumented just report
                // 8.2 as selected for the moment.
                return TargetSdkVersionToString(DEFAULT_TARGET_SDK);
            }
            return name.Trim().Replace("iOS_", "").Replace("_", ".");
        }

        set {
            var iosSettingsType = typeof(UnityEditor.PlayerSettings.iOS);
            // Write the version (Unity 5.5 and above).
            var osVersionProperty =
                iosSettingsType.GetProperty("targetOSVersionString");
            if (osVersionProperty != null) {
                osVersionProperty.SetValue(null, value, null);
            } else {
                osVersionProperty =
                    iosSettingsType.GetProperty("targetOSVersion");
                osVersionProperty.SetValue(
                    null,
                    Enum.Parse(osVersionProperty.PropertyType,
                               "iOS_" + value.Replace(".", "_")),
                    null);
            }
        }
    }

    /// <summary>
    /// Get or set the Unity iOS target SDK using a version number (e.g 71
    /// is equivalent to "7.1").
    /// </summary>
    static int TargetSdkVersion {
        get { return TargetSdkStringToVersion(TargetSdk); }
        set { TargetSdk = TargetSdkVersionToString(value); }
    }

    /// <summary>
    /// Convert a target SDK string into a value of the form
    // (major * 10) + minor.
    /// </summary>
    /// <returns>Integer representation of the SDK.</returns>
    internal static int TargetSdkStringToVersion(string targetSdk) {
        if (TARGET_SDK_REGEX.IsMatch(targetSdk)) {
            try {
                return Convert.ToInt32(targetSdk.Replace(".", ""));
            } catch (FormatException) {
                // Conversion failed, drop through.
            }
        }
        Log(String.Format(
            "Invalid iOS target SDK version configured \"{0}\".\n" +
            "\n" +
            "Please change this to a valid SDK version (e.g {1}) in:\n" +
            "  Player Settings -> Other Settings --> " +
            "Target Minimum iOS Version\n",
            targetSdk, TargetSdkVersionToString(DEFAULT_TARGET_SDK)),
            level: LogLevel.Warning);
        return DEFAULT_TARGET_SDK;

    }

    /// <summary>
    /// Convert an integer target SDK value into a string.
    /// </summary>
    /// <returns>String version number.</returns>
    internal static string TargetSdkVersionToString(int version) {
        int major = version / 10;
        int minor = version % 10;
        return major.ToString() + "." + minor.ToString();
    }

    /// <summary>
    /// Gets the SDK version for the specified target.
    /// </summary>
    /// <param name="targetName">Name of the executable target in the Xcode project.</param>
    /// <returns>SDK version for specified target. For custom targets, returns ios minTargetSdk
    /// when no pods found or no pods specify minTargetSdk.</returns>
    private static string GetSdkVersionForTarget(string targetName) {
        // If default iOS target then return minimum SDK version from
        // Unity iOS build settings
        if (targetName == TARGET_NAME) {
            return TargetSdk;
        }
        // Find the lowest sdk version for this target
        int sdkVersion = 0;
        if (pods.TryGetValue(targetName, out var targetPods)) {
            foreach (var pod in targetPods) {
                int podSdkVersion = pod.Value.MinTargetSdkToVersion();
                if (sdkVersion==0 || (podSdkVersion!=0 && sdkVersion > podSdkVersion)) {
                    sdkVersion = podSdkVersion;
                }
            }
            if (sdkVersion == 0) {
                if (GetPlatformForTarget(targetName) != DEFAULT_PLATFORM) {
                    Log(String.Format(
                            "Custom target \"{0}\" didnt specify a minTargetSdk " +
                            "in any of it's xml dependency files.\n\n" +
                            "Defaulting to ios minTargetSdk: \"{1}\"\n\n" +
                            "This may result in pod errors since a custom " +
                            "targetPlatform is specified.",
                            targetName),
                        level: LogLevel.Warning);
                }
                sdkVersion = TargetSdkVersion;
            }
        }
        return TargetSdkVersionToString(sdkVersion);
    }
    
    /// <summary>
    /// Gets the platform for the specified target.
    /// </summary>
    /// <param name="targetName">Name of the executable target in the Xcode project.</param>
    /// <returns>Platform for specified target. For custom targets, this is determined by the
    /// targetPlatform value in the targets pods which defaults to "ios" if not specified</returns>
    private static string GetPlatformForTarget(string targetName) {
        // If default iOS target then return minimum SDK version from
        // Unity iOS build settings
        if (targetName == TARGET_NAME) {
            return DEFAULT_PLATFORM;
        }
        // Find the lowest sdk version for this target
        string targetPlatform = null;
        bool multiTargetPlatformDetected = false;
        if (pods.TryGetValue(targetName, out var targetPods)) {
            foreach (var pod in targetPods) {
                if (targetPlatform == null) {
                    targetPlatform = pod.Value.targetPlatform;
                }
                if (targetPlatform != pod.Value.targetPlatform) {
                    multiTargetPlatformDetected = true;
                }
            }
        }
        if (multiTargetPlatformDetected) {
            Log(String.Format(
                    "Pods for target \"{0}\" specify multiple targetPlatforms.\n" +
                    "\n" +
                    "Please change this so that all pods for this target specify the " +
                    "same targetPlatform. Using \"{1}\" for now.",
                    targetName,targetPlatform),
                level: LogLevel.Warning);
        }
        return targetPlatform;
    }

    /// <summary>
    /// Determine whether any pods need bitcode disabled.
    /// </summary>
    /// <returns>List of pod names with bitcode disabled.</return>
    private static List<string> FindPodsWithBitcodeDisabled() {
        var disabled = new List<string>();
        foreach (var targetPods in pods.Values) {
            foreach (var pod in targetPods.Values) {
                if (!pod.bitcodeEnabled) {
                    disabled.Add(pod.name);
                }
            }
        }
        return disabled;
    }

    /// <summary>
    /// Menu item that installs CocoaPods if it's not already installed.
    /// </summary>
    [MenuItem("Assets/Play Services Resolver/iOS Resolver/Install Cocoapods")]
    public static void InstallCocoapodsMenu() {
        InstallCocoapodsInteractive();
    }

    /// <summary>
    /// Auto install CocoaPods tools if they're not already installed.
    /// </summary>
    public static void AutoInstallCocoapods() {
        InstallCocoapodsInteractive(displayAlreadyInstalled: false);
    }

    /// <summary>
    /// Interactively installs CocoaPods if it's not already installed.
    /// </summary>
    public static void InstallCocoapodsInteractive(bool displayAlreadyInstalled = true) {
        bool installCocoapods = true;
        lock (commandLineDialogLock) {
            if (commandLineDialog != null) {
                // If the installation is still in progress, display the dialog.
                commandLineDialog.Show();
                installCocoapods = false;
            }
        }
        if (installCocoapods) {
            InstallCocoapods(true, ".", displayAlreadyInstalled: displayAlreadyInstalled);
        }
    }

    /// <summary>
    /// Determine whether a gem (Ruby package) is installed.
    /// </summary>
    /// <param name="gemPackageName">Name of the package to check.</param>
    /// <param name="logMessage">Delegate use to log a failure message if the package manager
    /// returns an error code.</param>
    /// <returns>true if the package is installed, false otherwise.</returns>
    private static bool QueryGemInstalled(string gemPackageName,
                                          LogMessageDelegate logMessage = null) {
        logMessage = logMessage ?? Log;
        logMessage(String.Format("Determine whether Ruby Gem {0} is installed", gemPackageName),
                   verbose: true);
        var query = String.Format("list {0} --no-versions", gemPackageName);
        var result = RunCommand(GEM_EXECUTABLE, query);
        if (result.exitCode == 0) {
            foreach (var line in result.stdout.Split(new string[] { Environment.NewLine },
                                                     StringSplitOptions.None)) {
                if (line == gemPackageName) {
                    logMessage(String.Format("{0} is installed", gemPackageName), verbose: true);
                    return true;
                }
            }
        } else {
            logMessage(
                String.Format("Unable to determine whether the {0} gem is " +
                              "installed, will attempt to install anyway.\n\n" +
                              "'{1} {2}' failed with error code ({3}):\n" +
                              "{4}\n" +
                              "{5}\n",
                              gemPackageName, GEM_EXECUTABLE, query, result.exitCode,
                              result.stdout, result.stderr),
                level: LogLevel.Warning);
        }
        return false;
    }

    /// <summary>
    /// Install CocoaPods if it's not already installed.
    /// </summary>
    /// <param name="interactive">Whether this method should display information in pop-up
    /// dialogs.</param>
    /// <param name="workingDirectory">Where to run the pod tool's setup command.</param>
    /// <param name="displayAlreadyInstalled">Whether to display whether the tools are already
    /// installed.</param>
    public static void InstallCocoapods(bool interactive, string workingDirectory,
                                        bool displayAlreadyInstalled = true) {
        cocoapodsToolsInstallPresent = false;
        // Cocoapod tools are currently only available on OSX, don't attempt to install them
        // otherwise.
        if (UnityEngine.RuntimePlatform.OSXEditor != UnityEngine.Application.platform) {
            return;
        }

        LogMessageDelegate logMessage = null;
        if (interactive) {
            logMessage = LogToDialog;
        } else {
            logMessage = Log;
        }

        var podToolPath = FindPodTool();
        if (!String.IsNullOrEmpty(podToolPath)) {
            var installationFoundMessage = "CocoaPods installation detected " + podToolPath;
            if (displayAlreadyInstalled) logMessage(installationFoundMessage);
            cocoapodsToolsInstallPresent = true;
            return;
        }

        var complete = new AutoResetEvent(false);
        var commonInstallErrorMessage =
            "It will not be possible to install Cocoapods in the generated Xcode " +
            "project which will result in link errors when building your " +
            "application.\n\n" +
            "For more information see:\n" +
            "  https://guides.cocoapods.org/using/getting-started.html\n\n";

        // Log the set of install pods.
        RunCommand(GEM_EXECUTABLE, "list");

        // Gem is being executed in an RVM directory it's already configured to perform a
        // user install.  When RVM is configured "--user-install" ends up installing gems
        // in the wrong directory such that they're not visible to either the package manager
        // or Ruby.
        var gemEnvironment = ReadGemsEnvironment();
        string installArgs = "--user-install";
        if (gemEnvironment != null) {
            List<string> installationDir;
            if (gemEnvironment.TryGetValue("INSTALLATION DIRECTORY", out installationDir)) {
                foreach (var dir in installationDir) {
                    if (dir.IndexOf("/.rvm/") >= 0) {
                        installArgs = "";
                        break;
                    }
                }
            }
        }
        if (VerboseLoggingEnabled || ExecutionEnvironment.InBatchMode) {
            installArgs += " --verbose";
        }

        var commandList = new List<CommandItem>();
        if (!QueryGemInstalled("activesupport", logMessage: logMessage)) {
            // Workaround activesupport (dependency of the CocoaPods gem) requiring
            // Ruby 2.2.2 and above.
            // https://github.com/CocoaPods/CocoaPods/issues/4711
            commandList.Add(
                new CommandItem {
                    Command = GEM_EXECUTABLE,
                    Arguments = "install activesupport -v 4.2.6 " + installArgs
                });
        }
        commandList.Add(new CommandItem {
                Command = GEM_EXECUTABLE,
                Arguments = "install cocoapods " + installArgs
            });
        commandList.Add(new CommandItem { Command = POD_EXECUTABLE, Arguments = "setup" });

        RunCommandsAsync(
            commandList.ToArray(),
            (int commandIndex, CommandItem[] commands, CommandLine.Result result,
                CommandLineDialog dialog) => {
                var lastCommand = commands[commandIndex];
                commandIndex += 1;
                if (result.exitCode != 0) {
                    logMessage(String.Format(
                        "Failed to install CocoaPods for the current user.\n\n" +
                        "{0}\n" +
                        "'{1} {2}' failed with code ({3}):\n" +
                        "{4}\n\n" +
                        "{5}\n",
                        commonInstallErrorMessage, lastCommand.Command,
                        lastCommand.Arguments, result.exitCode, result.stdout,
                        result.stderr), level: LogLevel.Error);
                    complete.Set();
                    return -1;
                }
                // Pod setup process (should be the last command in the list).
                if (commandIndex == commands.Length - 1) {
                    podToolPath = FindPodTool();
                    if (String.IsNullOrEmpty(podToolPath)) {
                        logMessage(String.Format(
                            "'{0} {1}' succeeded but the {2} tool cannot be found.\n\n" +
                            "{3}\n", lastCommand.Command, lastCommand.Arguments,
                            POD_EXECUTABLE, commonInstallErrorMessage), level: LogLevel.Error);
                        complete.Set();
                        return -1;
                    }
                    if (dialog != null) {
                        dialog.bodyText += ("\n\nDownloading CocoaPods Master Repository\n" +
                                            "(this can take a while)\n");
                    }
                    commands[commandIndex].Command = podToolPath;
                } else if (commandIndex == commands.Length) {
                    complete.Set();
                    logMessage("CocoaPods tools successfully installed.");
                    cocoapodsToolsInstallPresent = true;
                }
                return commandIndex;
            }, displayDialog: interactive, summaryText: "Installing CocoaPods...");

        // If this wasn't started interactively, block until execution is complete.
        if (!interactive) complete.WaitOne();
    }

    /// <summary>
    /// Refresh XML dependencies if the plugin is enabled.
    /// </summary>
    /// <param name="buildTarget">Unused</param>
    /// <param name="pathToBuiltProject">Unused</param>
    [PostProcessBuildAttribute(BUILD_ORDER_REFRESH_DEPENDENCIES)]
    public static void OnPostProcessRefreshXmlDependencies(BuildTarget buildTarget,
                                                           string pathToBuiltProject) {
        if (!CocoapodsIntegrationEnabled) return;
        RefreshXmlDependencies();
    }

    /// <summary>
    /// If Cocoapod installation is enabled, prompt the user to install CocoaPods if it's not
    /// present on the machine.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_CHECK_COCOAPODS_INSTALL)]
    public static void OnPostProcessEnsurePodsInstallation(BuildTarget buildTarget,
                                                           string pathToBuiltProject) {
        if (!CocoapodsIntegrationEnabled) return;
        InstallCocoapods(false, pathToBuiltProject);
    }

    /// <summary>
    /// Post-processing build step to patch the generated project files.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_PATCH_PROJECT)]
    public static void OnPostProcessPatchProject(BuildTarget buildTarget,
                                                 string pathToBuiltProject) {
        if (!InjectDependencies() || !PodfileGenerationEnabled ||
            !CocoapodsProjectIntegrationEnabled || !cocoapodsToolsInstallPresent) {
            return;
        }
        PatchProject(buildTarget, pathToBuiltProject);
    }

    // Implementation of OnPostProcessPatchProject().
    // NOTE: This is separate from the post-processing method to prevent the
    // Mono runtime from loading the Xcode API before calling the post
    // processing step.
    internal static void PatchProject(
            BuildTarget buildTarget, string pathToBuiltProject) {
        var podsWithoutBitcode = FindPodsWithBitcodeDisabled();
        bool bitcodeDisabled = podsWithoutBitcode.Count > 0;
        if (bitcodeDisabled) {
            Log("Bitcode is disabled due to the following CocoaPods (" +
                String.Join(", ", podsWithoutBitcode.ToArray()) + ")",
                level: LogLevel.Warning);
        }
        // Configure project settings for CocoaPods.
        string pbxprojPath = GetProjectPath(pathToBuiltProject);
        var project = new UnityEditor.iOS.Xcode.PBXProject();
        project.ReadFromString(File.ReadAllText(pbxprojPath));
        string target = project.TargetGuidByName(TARGET_NAME);
        project.SetBuildProperty(target, "CLANG_ENABLE_MODULES", "YES");
        project.AddBuildProperty(target, "OTHER_LDFLAGS", "-ObjC");
        // GTMSessionFetcher requires Obj-C exceptions.
        project.SetBuildProperty(target, "GCC_ENABLE_OBJC_EXCEPTIONS", "YES");
        if (bitcodeDisabled) {
            project.AddBuildProperty(target, "ENABLE_BITCODE", "NO");
        }
        File.WriteAllText(pbxprojPath, project.WriteToString());
    }

    /// <summary>
    /// Post-processing build step to generate the podfile for ios.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_GEN_PODFILE)]
    public static void OnPostProcessGenPodfile(BuildTarget buildTarget,
                                               string pathToBuiltProject) {
        if (!InjectDependencies() || !PodfileGenerationEnabled) return;
        GenPodfile(buildTarget, pathToBuiltProject);
    }

    /// <summary>
    /// Get the path to the generated Podfile.
    /// </summary>
    private static string GetPodfilePath(string pathToBuiltProject) {
        return Path.Combine(pathToBuiltProject, "Podfile");
    }

    /// <summary>
    /// Checks to see if a podfile, not written by the IOSResolver is present.
    /// </summary>
    /// <param name="suspectedUnityPodfilePath">The path we suspect is written by Unity. This is
    /// either the original file or a backup of the path.</param>
    /// <returns>The path to the Podfile, presumed to be generated by Unity.</returns>
    private static string FindExistingUnityPodfile(string suspectedUnityPodfilePath) {
        if (!File.Exists(suspectedUnityPodfilePath)) return null;

        System.IO.StreamReader podfile = new System.IO.StreamReader(suspectedUnityPodfilePath);
        string firstline = podfile.ReadLine();
        podfile.Close();
        // If the podfile written is one that we created, then we need to look for the backup of the
        // original Unity podfile. This is necessary for cases when the user does an "append build"
        // in Unity. Since we back up the original podfile, we'll re-parse it when regenerating
        // the dependencies this time around.
        if (firstline == null || firstline.StartsWith(PODFILE_GENERATED_COMMENT)) {
            return FindExistingUnityPodfile(suspectedUnityPodfilePath +
                                            UNITY_PODFILE_BACKUP_POSTFIX);
        }

        return suspectedUnityPodfilePath;
    }

    private static void ParseUnityDeps(string unityPodfilePath) {
        Log("Parse Unity deps from: " + unityPodfilePath, verbose: true);

        System.IO.StreamReader unityPodfile = new System.IO.StreamReader(unityPodfilePath);
        string line;

        // We are only interested in capturing the dependencies "Pod depName, depVersion", inside
        // of the specific target. However there can be nested targets such as for testing, so we're
        // counting the depth to determine when to capture the pods. Also we only ever enter the
        // first depth if we're in the exact right target.
        int capturingPodsDepth = 0;
        var sources = new List<string>();
        while ((line = unityPodfile.ReadLine()) != null) {
            line = line.Trim();
            var sourceLineMatch = PODFILE_SOURCE_REGEX.Match(line);
            if (sourceLineMatch.Groups.Count > 1) {
                sources.Add(sourceLineMatch.Groups[1].Value);
                continue;
            }
            if (line.StartsWith("target 'Unity-iPhone' do")) {
                capturingPodsDepth++;
                continue;
            }

            if (capturingPodsDepth == 0) continue;

            // handle other scopes roughly
            if (line.EndsWith(" do")) {
                capturingPodsDepth++;  // Ignore nested targets like tests
            } else if (line == "end") {
                capturingPodsDepth--;
            }

            if (capturingPodsDepth != 1) continue;

            // Parse "pod" lines from the default target in the file.
            var podLineMatch = PODFILE_POD_REGEX.Match(line);
            var podGroups = podLineMatch.Groups;
            if (podGroups.Count > 1) {
                var podName = podGroups["podname"].ToString();
                var podVersion = podGroups["podversion"].ToString();
                var propertyNameCaptures = podGroups["propertyname"].Captures;
                var propertyValueCaptures = podGroups["propertyvalue"].Captures;
                var numberOfProperties = propertyNameCaptures.Count;
                var propertiesByName = new Dictionary<string, string>();
                for (int i = 0; i < numberOfProperties; ++i) {
                    propertiesByName[propertyNameCaptures[i].Value] =
                        propertyValueCaptures[i].Value;
                }
                AddPodInternal(
                    podName,
                    preformattedVersion: String.IsNullOrEmpty(podVersion) ? null : podVersion,
                    sources: sources, createdBy: unityPodfilePath, overwriteExistingPod: false,
                    propertiesByName: propertiesByName);
            }
        }
        unityPodfile.Close();
    }

    /// <summary>
    /// Generate the sources section from the set of "pods" in this class.
    ///
    /// Each source is interleaved across each pod - removing duplicates - as CocoaPods searches
    /// each source in order for each pod.
    ///
    /// See Pod.sources for more information.
    /// </summary>
    /// <returns>String which contains the sources section of a Podfile.  For example, if the
    /// Pod instances referenced by this class contain sources...
    ///
    /// ["http://myrepo.com/Specs.git", "http://anotherrepo.com/Specs.git"]
    ///
    /// this returns the string...
    ///
    /// source 'http://myrepo.com/Specs.git'
    /// source 'http://anotherrepo.com/Specs.git'
    private static string GeneratePodfileSourcesSection() {
        var interleavedSourcesLines = new List<string>();
        var processedSources = new HashSet<string>();
        int sourceIndex = 0;
        bool sourcesAvailable;
        foreach (var kv in Pod.Sources) {
            interleavedSourcesLines.Add(String.Format("source '{0}'", kv.Key));
        }
        do {
            sourcesAvailable = false;
            foreach (var targetPods in pods.Values) {
                foreach (var pod in targetPods.Values) {
                    if (sourceIndex < pod.sources.Count) {
                        sourcesAvailable = true;
                        var source = pod.sources[sourceIndex];
                        if (processedSources.Add(source)) {
                            interleavedSourcesLines.Add(String.Format("source '{0}'", source));
                        }
                    }   
                }
            }
            sourceIndex ++;
        } while (sourcesAvailable);
        return String.Join("\n", interleavedSourcesLines.ToArray()) + "\n";
    }

    // Implementation of OnPostProcessGenPodfile().
    // NOTE: This is separate from the post-processing method to prevent the
    // Mono runtime from loading the Xcode API before calling the post
    // processing step.
    public static void GenPodfile(BuildTarget buildTarget,
                                  string pathToBuiltProject) {
        string podfilePath = GetPodfilePath(pathToBuiltProject);

        string unityPodfile = FindExistingUnityPodfile(podfilePath);
        Log(String.Format("Detected Unity Podfile: {0}", unityPodfile), verbose: true);
        if (unityPodfile != null) {
            ParseUnityDeps(unityPodfile);
            if (podfilePath == unityPodfile) {
                string unityBackupPath = podfilePath + UNITY_PODFILE_BACKUP_POSTFIX;
                if (File.Exists(unityBackupPath)) {
                    File.Delete(unityBackupPath);
                }
                File.Move(podfilePath, unityBackupPath);
            }
        }

        Log(String.Format("Generating Podfile {0} with {1} integration.", podfilePath,
                          (CocoapodsWorkspaceIntegrationEnabled ? "Xcode workspace" :
                          (CocoapodsProjectIntegrationEnabled ? "Xcode project" : "no target"))),
            verbose: true);
        using (StreamWriter file = new StreamWriter(podfilePath)) {
            file.Write(GeneratePodfileSourcesSection());
            foreach(var targetPods in pods) {
                file.Write("\ntarget '" + targetPods.Key + "' do\n");
                file.Write("platform :{0}, '{1}'\n\n", GetPlatformForTarget(targetPods.Key),GetSdkVersionForTarget(targetPods.Key));
                foreach (var pod in targetPods.Value.Values) {
                    file.WriteLine(pod.PodFilePodLine);
                }
                file.WriteLine("end");
            }
        }
    }

    /// <summary>
    /// Read the Gems environment.
    /// </summary>
    /// <returns>Dictionary of environment properties or null if there was a problem reading
    /// the environment.</returns>
    private static Dictionary<string, List<string>> ReadGemsEnvironment() {
        var result = RunCommand(GEM_EXECUTABLE, "environment");
        if (result.exitCode != 0) {
            return null;
        }
        // gem environment outputs YAML for all config variables.  Perform some very rough YAML
        // parsing to get the environment into a usable form.
        var gemEnvironment = new Dictionary<string, List<string>>();
        const string listItemPrefix = "- ";
        int previousIndentSize = 0;
        List<string> currentList = null;
        char[] listToken = new char[] { ':' };
        foreach (var line in result.stdout.Split(new char[] { '\r', '\n' })) {
            var trimmedLine = line.Trim();
            var indentSize = line.Length - trimmedLine.Length;
            if (indentSize < previousIndentSize) currentList = null;

            if (trimmedLine.StartsWith(listItemPrefix)) {
                trimmedLine = trimmedLine.Substring(listItemPrefix.Length).Trim();
                if (currentList == null) {
                    var tokens = trimmedLine.Split(listToken);
                    currentList = new List<string>();
                    gemEnvironment[tokens[0].Trim()] = currentList;
                    var value = tokens.Length == 2 ? tokens[1].Trim() : null;
                    if (!String.IsNullOrEmpty(value)) {
                        currentList.Add(value);
                        currentList = null;
                    }
                } else if (indentSize >= previousIndentSize) {
                    currentList.Add(trimmedLine);
                }
            } else {
                currentList = null;
            }
            previousIndentSize = indentSize;
        }
        return gemEnvironment;
    }

    /// <summary>
    /// Find the "pod" tool.
    /// </summary>
    /// <returns>Path to the pod tool if successful, null otherwise.</returns>
    private static string FindPodTool() {
        foreach (string path in POD_SEARCH_PATHS) {
            string podPath = Path.Combine(path, POD_EXECUTABLE);
            Log("Searching for CocoaPods tool in " + podPath,
                verbose: true);
            if (File.Exists(podPath)) {
                Log("Found CocoaPods tool in " + podPath, verbose: true);
                return podPath;
            }
        }
        Log("Querying gems for CocoaPods install path", verbose: true);
        var environment = ReadGemsEnvironment();
        if (environment != null) {
            const string executableDir = "EXECUTABLE DIRECTORY";
            foreach (string environmentVariable in new [] { executableDir, "GEM PATHS" }) {
                List<string> paths;
                if (environment.TryGetValue(environmentVariable, out paths)) {
                    foreach (var path in paths) {
                        var binPath = environmentVariable == executableDir ? path :
                            Path.Combine(path, "bin");
                        var podPath = Path.Combine(binPath, POD_EXECUTABLE);
                        Log("Checking gems install path for CocoaPods tool " + podPath,
                            verbose: true);
                        if (File.Exists(podPath)) {
                            Log("Found CocoaPods tool in " + podPath, verbose: true);
                            return podPath;
                        }
                    }
                }
            }
        }
        return null;
    }


    /// <summary>
    /// Command line command to execute.
    /// </summary>
    private class CommandItem {
        /// <summary>
        /// Command to execute.
        /// </summary>
        public string Command { get; set; }
        /// <summary>
        /// Arguments for the command.
        /// </summary>
        public string Arguments { get; set; }
        /// <summary>
        /// Directory to execute the command.
        /// </summary>
        public string WorkingDirectory { get; set; }
        /// <summary>
        /// Get a string representation of the command line.
        /// </summary>
        public override string ToString() {
            return String.Format("{0} {1}", Command, Arguments ?? "");
        }
    };

    /// <summary>
    /// Called when one of the commands complete in RunCommandsAsync().
    /// </summary>
    /// <param name="commandIndex">Index of the completed command in commands.</param>
    /// <param name="commands">Array of commands being executed.</param>
    /// <param name="result">Result of the last command.</param>
    /// <param name="dialog">Dialog box, if the command was executed in a dialog.</param>
    /// <returns>Reference to the next command in the list to execute,
    /// -1 or commands.Length to stop execution.</returns>
    private delegate int CommandItemCompletionHandler(
         int commandIndex, CommandItem[] commands,
         CommandLine.Result result, CommandLineDialog dialog);

    /// <summary>
    /// Container for a delegate which enables a lambda to reference itself.
    /// </summary>
    private class DelegateContainer<T> {
        /// <summary>
        /// Delegate method associated with the container.  This enables the
        /// following pattern:
        ///
        /// var container = new DelegateContainer<CommandLine.CompletionHandler>();
        /// container.Handler = (CommandLine.Result result) => { RunNext(container.Handler); };
        /// </summary>
        public T Handler { get; set; }
    }

    /// <summary>
    /// Write the result of a command to the log.
    /// </summary>
    /// <param name="command">Command that was executed.</param>
    /// <param name="result">Result of the command.</param>
    private static void LogCommandLineResult(string command, CommandLine.Result result) {
        Log(String.Format("'{0}' completed with code {1}\n\n" +
                          "{2}\n" +
                          "{3}\n", command, result.exitCode, result.stdout, result.stderr),
            verbose: true);
    }

    /// <summary>
    /// Run a series of commands asynchronously optionally displaying a dialog.
    /// </summary>
    /// <param name="commands">Commands to execute.</param>
    /// <param name="completionDelegate">Called when the command is complete.</param>
    /// <param name="displayDialog">Whether to show a dialog while executing.</param>
    /// <param name="summaryText">Text to display at the top of the dialog.</param>
    private static void RunCommandsAsync(CommandItem[] commands,
                                         CommandItemCompletionHandler completionDelegate,
                                         bool displayDialog = false, string summaryText = null) {
        var envVars = new Dictionary<string,string>() {
            // CocoaPods requires a UTF-8 terminal, otherwise it displays a warning.
            {"LANG", (System.Environment.GetEnvironmentVariable("LANG") ??
                      "en_US.UTF-8").Split('.')[0] + ".UTF-8"},
            {"PATH", ("/usr/local/bin:" +
                      (System.Environment.GetEnvironmentVariable("PATH") ?? ""))},
        };

        if (displayDialog) {
            var dialog = CommandLineDialog.CreateCommandLineDialog("iOS Resolver");
            dialog.modal = false;
            dialog.autoScrollToBottom = true;
            dialog.bodyText = commands[0].ToString() + "\n";
            dialog.summaryText = summaryText ?? dialog.bodyText;
            dialog.logger = logger;

            int index = 0;
            var handlerContainer = new DelegateContainer<CommandLine.CompletionHandler>();
            handlerContainer.Handler = (CommandLine.Result asyncResult) => {
                var command = commands[index];
                LogCommandLineResult(command.ToString(), asyncResult);

                index = completionDelegate(index, commands, asyncResult, dialog);
                bool endOfCommandList = index < 0 || index >= commands.Length;
                if (endOfCommandList) {
                    // If this is the last command and it has completed successfully, close the
                    // dialog.
                    if (asyncResult.exitCode == 0) {
                        dialog.Close();
                    }
                    lock (commandLineDialogLock) {
                        commandLineDialog = null;
                    }
                } else {
                    command = commands[index];
                    var commandLogLine = command.ToString();
                    dialog.bodyText += "\n" + commandLogLine + "\n\n";
                    Log(commandLogLine, verbose: true);
                    dialog.RunAsync(command.Command, command.Arguments, handlerContainer.Handler,
                                    workingDirectory: command.WorkingDirectory,
                                    envVars: envVars);
                }
            };

            Log(commands[0].ToString(), verbose: true);
            dialog.RunAsync(
                commands[index].Command, commands[index].Arguments,
                handlerContainer.Handler, workingDirectory: commands[index].WorkingDirectory,
                envVars: envVars);
            dialog.Show();
            lock (commandLineDialogLock) {
                commandLineDialog = dialog;
            }
        } else {
            if (!String.IsNullOrEmpty(summaryText)) Log(summaryText);

            int index = 0;
            while (index >= 0 && index < commands.Length) {
                var command = commands[index];
                Log(command.ToString(), verbose: true);
                var result = CommandLine.RunViaShell(
                    command.Command, command.Arguments, workingDirectory: command.WorkingDirectory,
                    envVars: envVars, useShellExecution: PodToolExecutionViaShellEnabled);
                LogCommandLineResult(command.ToString(), result);
                index = completionDelegate(index, commands, result, null);
            }
        }
    }


    /// <summary>
    /// Run a command, optionally displaying a dialog.
    /// </summary>
    /// <param name="command">Command to execute.</param>
    /// <param name="commandArgs">Arguments passed to the command.</param>
    /// <param name="completionDelegate">Called when the command is complete.</param>
    /// <param name="workingDirectory">Where to run the command.</param>
    /// <param name="displayDialog">Whether to show a dialog while executing.</param>
    /// <param name="summaryText">Text to display at the top of the dialog.</param>
    private static void RunCommandAsync(string command, string commandArgs,
                                        CommandLine.CompletionHandler completionDelegate,
                                        string workingDirectory = null,
                                        bool displayDialog = false, string summaryText = null) {
        RunCommandsAsync(
            new [] { new CommandItem { Command = command, Arguments = commandArgs,
                                       WorkingDirectory = workingDirectory } },
            (int commandIndex, CommandItem[] commands, CommandLine.Result result,
             CommandLineDialog dialog) => {
                completionDelegate(result);
                return -1;
            }, displayDialog: displayDialog, summaryText: summaryText);
    }

    /// <summary>
    /// Run a command, optionally displaying a dialog.
    /// </summary>
    /// <param name="command">Command to execute.</param>
    /// <param name="commandArgs">Arguments passed to the command.</param>
    /// <param name="workingDirectory">Where to run the command.</param>
    /// <param name="displayDialog">Whether to show a dialog while executing.</param>
    /// <returns>The CommandLine.Result from running the command.</returns>
    private static CommandLine.Result RunCommand(string command, string commandArgs,
                                                 string workingDirectory = null,
                                                 bool displayDialog = false) {
        CommandLine.Result result = null;
        var complete = new AutoResetEvent(false);
        RunCommandAsync(command, commandArgs,
                        (CommandLine.Result asyncResult) => {
                            result = asyncResult;
                            complete.Set();
                        }, workingDirectory: workingDirectory, displayDialog: displayDialog);
        complete.WaitOne();
        return result;

    }

    /// <summary>
    /// Finds and executes the pod command on the command line, using the
    /// correct environment.
    /// </summary>
    /// <param name="podArgs">Arguments passed to the pod command.</param>
    /// <param name="pathToBuiltProject">The path to the unity project, given
    /// from the unity [PostProcessBuildAttribute()] function.</param>
    /// <param name="completionDelegate">Called when the command is complete.</param>
    /// <param name="displayDialog">Whether to execute in a dialog.</param>
    /// <param name="summaryText">Text to display at the top of the dialog.</param>
    private static void RunPodCommandAsync(
            string podArgs, string pathToBuiltProject,
            CommandLine.CompletionHandler completionDelegate,
            bool displayDialog = false, string summaryText = null) {
        string podCommand = FindPodTool();
        if (String.IsNullOrEmpty(podCommand)) {
            var result = new CommandLine.Result();
            result.exitCode = 1;
            result.stderr = String.Format(
                "'{0}' command not found; unable to generate a usable Xcode project.\n{1}",
                POD_EXECUTABLE, COCOAPOD_INSTALL_INSTRUCTIONS);
            Log(result.stderr, level: LogLevel.Error);
            completionDelegate(result);
        }
        RunCommandAsync(podCommand, podArgs, completionDelegate,
                        workingDirectory: pathToBuiltProject, displayDialog: displayDialog,
                        summaryText: summaryText);
    }

    /// <summary>
    /// Finds and executes the pod command on the command line, using the
    /// correct environment.
    /// </summary>
    /// <param name="podArgs">Arguments passed to the pod command.</param>
    /// <param name="pathToBuiltProject">The path to the unity project, given
    /// from the unity [PostProcessBuildAttribute()] function.</param>
    /// <param name="displayDialog">Whether to execute in a dialog.</param>
    /// <returns>The CommandLine.Result from running the command.</returns>
    private static CommandLine.Result RunPodCommand(
            string podArgs, string pathToBuiltProject, bool displayDialog = false) {
        CommandLine.Result result = null;
        var complete = new AutoResetEvent(false);
        RunPodCommandAsync(podArgs, pathToBuiltProject,
                           (CommandLine.Result asyncResult) => {
                               result = asyncResult;
                               complete.Set();
                           }, displayDialog: displayDialog);
        complete.WaitOne();
        return result;
    }

    /// <summary>
    /// Downloads all of the framework dependencies using pods.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_INSTALL_PODS)]
    public static void OnPostProcessInstallPods(BuildTarget buildTarget,
                                                string pathToBuiltProject) {
        if (!InjectDependencies() || !PodfileGenerationEnabled) return;
        if (UpdateTargetSdk()) return;
        if (!CocoapodsIntegrationEnabled || !cocoapodsToolsInstallPresent) {
            Log(String.Format(
                "Cocoapod installation is disabled.\n" +
                "If CocoaPods are not installed in your project it will not link.\n\n" +
                "The command '{0} install' must be executed from the {1} directory to generate " +
                "a Xcode workspace that includes the CocoaPods referenced by {2}.\n" +
                "For more information see:\n" +
                "  https://guides.cocoapods.org/using/using-cocoapods.html\n\n",
                POD_EXECUTABLE, pathToBuiltProject, GetPodfilePath(pathToBuiltProject)),
                level: LogLevel.Warning);
            return;
        }
        // Skip running "pod install" if requested. This is helpful if the user want to run pod tool
        // manually, in case pod tool customizations are necessary (custom flag or repo setup).
        if (UnityCanLoadWorkspace &&
            CocoapodsIntegrationMethodPref == CocoapodsIntegrationMethod.Workspace &&
            SkipPodInstallWhenUsingWorkspaceIntegration) {
            Log("Skipping pod install.", level: LogLevel.Warning);
            return;
        }

        // Require at least version 1.0.0
        CommandLine.Result result;
        result = RunPodCommand("--version", pathToBuiltProject);
        if (result.exitCode == 0) podsVersion = result.stdout.Trim();

        if (result.exitCode != 0 ||
            (!String.IsNullOrEmpty(podsVersion) && podsVersion[0] == '0')) {
            Log("Error running CocoaPods. Please ensure you have at least " +
                "version 1.0.0.  " + COCOAPOD_INSTALL_INSTRUCTIONS + "\n\n" +
                "'" + POD_EXECUTABLE + " --version' returned status: " +
                result.exitCode.ToString() + "\n" +
                "output: " + result.stdout + "\n\n" +
                result.stderr, level: LogLevel.Error);
            return;
        }

        result = RunPodCommand("install", pathToBuiltProject);

        // If pod installation failed it may be due to an out of date pod repo.
        // We'll attempt to resolve the error by updating the pod repo -
        // which is a slow operation - and retrying pod installation.
        if (result.exitCode != 0) {
            CommandLine.Result repoUpdateResult =
                RunPodCommand("repo update", pathToBuiltProject);
            bool repoUpdateSucceeded = repoUpdateResult.exitCode == 0;

            // Second attempt result.
            // This is isolated in case it fails, so we can just report the
            // original failure.
            CommandLine.Result result2;
            result2 = RunPodCommand("install", pathToBuiltProject);

            // If the repo update still didn't fix the problem...
            if (result2.exitCode != 0) {
                Log("iOS framework addition failed due to a " +
                    "CocoaPods installation failure. This will will likely " +
                    "result in an non-functional Xcode project.\n\n" +
                    "After the failure, \"pod repo update\" " +
                    "was executed and " +
                    (repoUpdateSucceeded ? "succeeded. " : "failed. ") +
                    "\"pod install\" was then attempted again, and still " +
                    "failed. This may be due to a broken CocoaPods " +
                    "installation. See: " +
                    "https://guides.cocoapods.org/using/troubleshooting.html " +
                    "for potential solutions.\n\n" +
                    "pod install output:\n\n" + result.stdout +
                    "\n\n" + result.stderr +
                    "\n\n\n" +
                    "pod repo update output:\n\n" + repoUpdateResult.stdout +
                    "\n\n" + repoUpdateResult.stderr, level: LogLevel.Error);
                return;
            }
        }
    }

    /// <summary>
    /// Finds the frameworks downloaded by CocoaPods in the Pods directory
    /// and adds them to the project.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_UPDATE_DEPS)]
    public static void OnPostProcessUpdateProjectDeps(
            BuildTarget buildTarget, string pathToBuiltProject) {
        if (!InjectDependencies() || !PodfileGenerationEnabled ||
            !CocoapodsProjectIntegrationEnabled ||  // Early out for Workspace level integration.
            !cocoapodsToolsInstallPresent) {
            return;
        }

        UpdateProjectDeps(buildTarget, pathToBuiltProject);
    }

    // Handles the Xcode project level integration injection of scanned dependencies.
    // Implementation of OnPostProcessUpdateProjectDeps().
    // NOTE: This is separate from the post-processing method to prevent the
    // Mono runtime from loading the Xcode API before calling the post
    // processing step.
    public static void UpdateProjectDeps(
            BuildTarget buildTarget, string pathToBuiltProject) {
        // If the Pods directory does not exist, the pod download step
        // failed.
        var podsDir = Path.Combine(pathToBuiltProject, PODS_DIR);
        if (!Directory.Exists(podsDir)) return;

        // If Unity can load workspaces, and one has been generated, yet we're still
        // trying to patch the project file, then we have to actually get rid of the workspace
        // and warn the user about it.
        // We'll be taking the dependencies we scraped from the podfile and inserting them in the
        // project with this method anyway, so nothing should be lost.
        string workspacePath = Path.Combine(pathToBuiltProject, "Unity-iPhone.xcworkspace");
        if (UnityCanLoadWorkspace && CocoapodsProjectIntegrationEnabled &&
            Directory.Exists(workspacePath)) {
            Log("Removing the generated workspace to force Unity to directly load the " +
                "xcodeproj.\nSince Unity 5.6, Unity can now load workspace files generated " +
                "from CocoaPods integration, however the IOSResolver Settings are configured " +
                "to use project level integration. It's recommended that you use workspace " +
                "integration instead.\n" +
                "You can manage this setting from: Assets > Play Services Resolver > " +
                "iOS Resolver > Settings, using the CocoaPods Integration drop down menu.",
                level: LogLevel.Warning);
            Directory.Delete(workspacePath, true);
        }

        var pbxprojPath = GetProjectPath(pathToBuiltProject);
        var project = new UnityEditor.iOS.Xcode.PBXProject();
        project.ReadFromString(File.ReadAllText(pbxprojPath));
        var target = project.TargetGuidByName(TARGET_NAME);
        var guid = project.AddFile(
            Path.Combine(podsDir, PODS_PROJECT_NAME + ".xcodeproj"),
            "Pods.xcodeproj",
            UnityEditor.iOS.Xcode.PBXSourceTree.Source);
        project.AddFileToBuild(target, guid);
        project.WriteToFile(pbxprojPath);
    }

    /// <summary>
    /// Read XML dependencies if the plugin is enabled.
    /// </summary>
    private static void RefreshXmlDependencies() {
        // Remove all pods that were added via XML dependencies.
        foreach (var targetPods in pods.ToList()) {
            foreach (var podNameAndPod in targetPods.Value.ToList()) {
                if (podNameAndPod.Value.fromXmlFile) {
                    pods[targetPods.Key].Remove(podNameAndPod.Key);
                }
            }
        }
        // Clear all sources (only can be set via XML config).
        Pod.Sources = new List<KeyValuePair<string, string>>();
        // Read pod specifications from XML dependencies.
        xmlDependencies.ReadAll(logger);
    }
}

}  // namespace Google

#endif  // UNITY_IOS
