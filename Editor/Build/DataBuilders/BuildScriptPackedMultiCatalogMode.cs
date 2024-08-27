using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AddressableAssets.Build.BuildPipelineTasks;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace UnityEditor.AddressableAssets.Build.DataBuilders
{
    /// <summary>
    /// Build script used for player builds and running with bundles in the editor, allowing building of multiple catalogs.
    /// </summary>
    [CreateAssetMenu(fileName = "BuildScriptPackedMultiCatalog.asset", menuName = "Addressables/Content Builders/Multi-Catalog Build Script")]
    public class BuildScriptPackedMultiCatalogMode : BuildScriptPackedMode, IMultipleCatalogsBuilder
    {
        /// <summary>
        /// Move a file, deleting it first if it exists.
        /// </summary>
        private static void FileCopyOverwrite(string src, string dst)
        {
            if (src == dst)
            {
                return;
            }

            if (File.Exists(dst))
            {
                File.Delete(dst);
            }

            File.Copy(src, dst);
        }

        [SerializeField]
        private List<ExternalCatalogSetup> externalCatalogs = new List<ExternalCatalogSetup>();

        private readonly List<CatalogSetup> catalogSetups = new List<CatalogSetup>();

        public override string Name
        {
            get => base.Name + " - Multi-Catalog";
        }

        public List<ExternalCatalogSetup> ExternalCatalogs
        {
            get => externalCatalogs;
            set => externalCatalogs = value;
        }

        protected override List<ContentCatalogBuildInfo> GetContentCatalogs(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            // cleanup
            catalogSetups.Clear();

            // Prepare catalogs
            var defaultCatalog = base.GetContentCatalogs(builderInput, aaContext).First();
            defaultCatalog.Locations.Clear(); // This will get filled up again below, but filtered by external catalog setups.
            foreach (ExternalCatalogSetup catalogContentGroup in externalCatalogs)
            {
                if (catalogContentGroup != null)
                {
                    catalogSetups.Add(new CatalogSetup(catalogContentGroup, builderInput, aaContext));
                }
            }

            // Assign assets to new catalogs based on included groups, allowing overlap
            foreach (var loc in aaContext.locations)
            {
                bool addedToAnyCatalog = false;

                foreach (var catalogSetup in catalogSetups)
                {
                    if (catalogSetup.CatalogContentGroup.IsPartOfCatalog(loc, aaContext))
                    {
                        AddLocationToCatalog(loc, catalogSetup, builderInput);
                        addedToAnyCatalog = true;
                    }
                }

                // If the asset wasn't added to any catalog, add it to the default catalog
                if (!addedToAnyCatalog)
                {
                    defaultCatalog.Locations.Add(loc);
                }
            }

            // Handle dependencies and collect final catalogs
            foreach (var catalogSetup in catalogSetups)
            {
                ProcessCatalogDependencies(catalogSetup, defaultCatalog);
            }

            var catalogs = new List<ContentCatalogBuildInfo>(catalogSetups.Count + 1);
            catalogs.Add(defaultCatalog);
            foreach (var setup in catalogSetups)
            {
                if (!setup.IsEmpty)
                {
                    catalogs.Add(setup.BuildInfo);
                }
            }

            return catalogs;
        }

        private void AddLocationToCatalog(ContentCatalogDataEntry loc, CatalogSetup catalogSetup, AddressablesDataBuilderInput builderInput)
        {
            if (loc.ResourceType == typeof(IAssetBundleResource))
            {
                var bundleId = (loc.Data as AssetBundleRequestOptions).BundleName + ".bundle";
                var group = catalogSetup.CatalogContentGroup.AssetGroups.FirstOrDefault(g => g.entries.Any(e => e.BundleFileId == loc.InternalId));

                // Prioritize the paths from ExternalCatalogSetup over the group's schema
                var externalCatalog = catalogSetup.CatalogContentGroup;
                var buildPath = externalCatalog.BuildPath.GetValue(builderInput.AddressableSettings.profileSettings, builderInput.AddressableSettings.activeProfileId);
                var runtimeLoadPath = externalCatalog.RuntimeLoadPath.GetValue(builderInput.AddressableSettings.profileSettings, builderInput.AddressableSettings.activeProfileId);

                // Generate a new load path based on the settings of the external catalog
                var filename = GenerateLocationListsTask.GetFileName(loc.InternalId, builderInput.Target);
                var runtimeLoadPathEvaluated = GenerateLocationListsTask.GetLoadPath(group, externalCatalog.RuntimeLoadPath, filename, builderInput.Target);

                // Add the location to the catalog with the correct paths
                catalogSetup.BuildInfo.Locations.Add(new ContentCatalogDataEntry(typeof(IAssetBundleResource), runtimeLoadPathEvaluated, loc.Provider, loc.Keys, loc.Dependencies, loc.Data));
                catalogSetup.catalogBundles.Add(loc);
            }
            else
            {
                catalogSetup.BuildInfo.Locations.Add(loc);
            }
        }

        private void ProcessCatalogDependencies(CatalogSetup catalogSetup, ContentCatalogBuildInfo defaultCatalog)
        {
            var locationQueue = new Queue<ContentCatalogDataEntry>(catalogSetup.BuildInfo.Locations);
            var processedLocations = new HashSet<ContentCatalogDataEntry>();

            while (locationQueue.Count > 0)
            {
                ContentCatalogDataEntry location = locationQueue.Dequeue();

                if (!processedLocations.Add(location) || location.Dependencies == null || location.Dependencies.Count == 0)
                {
                    continue;
                }

                foreach (var entryDependency in location.Dependencies)
                {
                    var depLocation = defaultCatalog.Locations.Find(loc => loc.Keys[0] == entryDependency);
                    if (depLocation != null)
                    {
                        locationQueue.Enqueue(depLocation);

                        if (!catalogSetup.BuildInfo.Locations.Contains(depLocation))
                        {
                            catalogSetup.BuildInfo.Locations.Add(depLocation);
                        }
                    }
                    else if (!catalogSetup.BuildInfo.Locations.Exists(loc => loc.Keys[0] == entryDependency))
                    {
                        Debug.LogError($"Could not find location for dependency ID {entryDependency} in the default catalog.");
                    }
                }
            }
        }

        protected override TResult DoBuild<TResult>(AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
        {
            var result = base.DoBuild<TResult>(builderInput, aaContext);
            var copiedFiles = new HashSet<string>();

            foreach (var setup in catalogSetups)
            {
                if (setup.IsEmpty)
                {
                    continue;
                }

                var profileSettings = aaContext.Settings.profileSettings;
                var activeProfileId = aaContext.Settings.activeProfileId;
                var defaultBuildPathData = profileSettings.GetProfileDataByName(AddressableAssetSettings.kLocalBuildPath);
                var globalBuildPath = profileSettings.EvaluateString(activeProfileId, profileSettings.GetValueById(activeProfileId, defaultBuildPathData.Id));

                foreach (var loc in setup.catalogBundles)
                {
                    var bundleId = (loc.Data as AssetBundleRequestOptions).BundleName + ".bundle";
                    var group = aaContext.Settings.FindGroup(g => g.Guid == aaContext.bundleToAssetGroup[bundleId]);
                    var schema = group?.GetSchema<BundledAssetGroupSchema>();

                    if (schema?.BuildPath.Id != defaultBuildPathData.Id)
                    {
                        continue;
                    }

                    var bundleName = Path.GetFileName(loc.InternalId);
                    var bundleSrcPath = Path.Combine(globalBuildPath, bundleName);
                    var bundleDstPath = Path.Combine(setup.BuildInfo.BuildPath, bundleName);

                    // Copy the file instead of moving
                    FileCopyOverwrite(bundleSrcPath, bundleDstPath);
                    copiedFiles.Add(bundleSrcPath); // Track the copied files
                }
            }

            // After all copies are done, delete the source files if they are no longer needed
            foreach (var file in copiedFiles)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            return result;
        }

        public override void ClearCachedData()
        {
            base.ClearCachedData();

            if ((externalCatalogs == null) || (externalCatalogs.Count == 0))
            {
                return;
            }

            // Cleanup the additional catalogs
            var profileSettings = AddressableAssetSettingsDefaultObject.Settings.profileSettings;
            var profileId = AddressableAssetSettingsDefaultObject.Settings.activeProfileId;

            var libraryDirectory = new DirectoryInfo("Library");
            var assetsDirectory = new DirectoryInfo("Assets");

            foreach (ExternalCatalogSetup externalCatalog in externalCatalogs)
            {
                string buildPath = externalCatalog.BuildPath.GetValue(profileSettings, profileId);
                if (string.IsNullOrEmpty(buildPath))
                {
                    buildPath = externalCatalog.BuildPath.Id;
                }

                if (!Directory.Exists(buildPath))
                {
                    continue;
                }

                // Stop if we're about to delete the whole library or assets directory.
                var buildDirectory = new DirectoryInfo(buildPath);
                if ((Path.GetRelativePath(buildDirectory.FullName, libraryDirectory.FullName) == ".") ||
                    (Path.GetRelativePath(buildDirectory.FullName, assetsDirectory.FullName) == "."))
                {
                    continue;
                }

                // Delete each file in the build directory.
                foreach (string catalogFile in Directory.GetFiles(buildPath))
                {
                    File.Delete(catalogFile);
                }

                Directory.Delete(buildPath, true);
            }
        }

        private class CatalogSetup
        {
            public readonly ExternalCatalogSetup CatalogContentGroup = null;

            /// <summary>
            /// The catalog build info.
            /// </summary>
            public readonly ContentCatalogBuildInfo BuildInfo = null;

            /// <summary>
            /// Tells whether the catalog is empty.
            /// </summary>
            public bool IsEmpty => BuildInfo.Locations.Count == 0;

            /// <summary>
            /// The list of bundles that are associated with this catalog setup.
            /// </summary>
            public readonly List<ContentCatalogDataEntry> catalogBundles = new List<ContentCatalogDataEntry>();

            public CatalogSetup(ExternalCatalogSetup buildCatalog, AddressablesDataBuilderInput builderInput, AddressableAssetsBuildContext aaContext)
            {
                this.CatalogContentGroup = buildCatalog;

                var profileSettings = aaContext.Settings.profileSettings;
                var profileId = aaContext.Settings.activeProfileId;
                var catalogFileName = $"{buildCatalog.CatalogName}{Path.GetExtension(builderInput.RuntimeCatalogFilename)}";

                BuildInfo = new ContentCatalogBuildInfo(buildCatalog.CatalogName, catalogFileName);

                // Set the build path.
                BuildInfo.BuildPath = buildCatalog.BuildPath.GetValue(profileSettings, profileId);
                if (string.IsNullOrEmpty(BuildInfo.BuildPath))
                {
                    BuildInfo.BuildPath = profileSettings.EvaluateString(profileId, buildCatalog.BuildPath.Id);

                    if (string.IsNullOrWhiteSpace(BuildInfo.BuildPath))
                    {
                        throw new Exception($"The catalog build path for external catalog '{buildCatalog.name}' is empty.");
                    }
                }

                // Set the load path.
                BuildInfo.LoadPath = buildCatalog.RuntimeLoadPath.GetValue(profileSettings, profileId);
                if (string.IsNullOrEmpty(BuildInfo.LoadPath))
                {
                    BuildInfo.LoadPath = profileSettings.EvaluateString(profileId, buildCatalog.RuntimeLoadPath.Id);
                }

                BuildInfo.Register = false;
            }
        }
    }
}