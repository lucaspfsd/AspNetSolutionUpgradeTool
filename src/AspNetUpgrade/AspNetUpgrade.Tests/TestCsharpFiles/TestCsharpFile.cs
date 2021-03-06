﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using Autofac;
using Autofac.Integration.Mef;
using MefContrib.Hosting;
using Microsoft.AspNet.FileProviders;
using Microsoft.AspNet.FileProviders.Composite;
using Microsoft.AspNet.Hosting;
using Microsoft.AspNet.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.OptionsModel;
using Microsoft.Extensions.PlatformAbstractions;
using Microsoft.AspNet.Builder;
using Microsoft.Extensions.WebEncoders;
using Microsoft.AspNet.Razor;
using Microsoft.AspNet.Http;
using Microsoft.Data.Entity;

using Newtonsoft.Json;

namespace Gluon.Core
{
    /// <summary>
    /// Can find types in assemblies that implement the given contract. 
    /// </summary>
    /// <remarks>Serializable allows this class to be marshalled accross app domain boundaries, should
    /// you wish to load plugin assemblies within a seperate app domain then this is important.</remarks>   
    public class ComponentManager : IComponentManager, IEqualityComparer<AssemblyName>
    {
        private readonly IOptions<ComponentManagerConfig> _config;
        private readonly string _shadowedPluginsDirectory;
        private readonly IHostingEnvironment _env;
        //   private readonly DefaultAssemblyProvider _defaultAssemblyProvider;
        private readonly IAssemblyLoadContextAccessor _assemblyLoadContextAccessor;
        private readonly ILibraryManager _libraryManager;
        private IEnumerable<Library> _librariesReferencingThisLibrary;

        private const string ShadowedDirPath = "..\\Shadowed\\";
        private List<Assembly> _candidateAssemblies = null;

        private readonly ILogger<ComponentManager> _logger;

        public ComponentManager(ILibraryManager libManager, IAssemblyLoadContextAccessor assemblyLoadContextAccessor,
            IOptions<ComponentManagerConfig> config, IHostingEnvironment env, ILogger<ComponentManager> logger)
        {
            // _defaultAssemblyProvider = defaultAssemblyProvider;
            _libraryManager = libManager;
            _assemblyLoadContextAccessor = assemblyLoadContextAccessor;
            _config = config;
            _env = env;
            _shadowedPluginsDirectory = Path.Combine(GetRootedPath(_config.Value.ModulesFolder), ShadowedDirPath);
            _logger = logger;
        }

        public IEnumerable<Library> GetLibrariesReferencingThisLibrary()
        {
            if (_librariesReferencingThisLibrary == null)
            {
                // find all referenced libraries that have this assembly a dependency, and treat them as candidate assemblies for plugins.
                var thisAssemblyName = this.GetType().Assembly.GetName().Name;
                _librariesReferencingThisLibrary = _libraryManager.GetReferencingLibraries(thisAssemblyName);

            }
            return _librariesReferencingThisLibrary.ToArray();
        }

        /// <summary>
        /// Returns assemblies from the LibraryManager that have a dependency to this assembly.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Assembly> GetAssembliesThatReferenceThisAssembly()
        {
            if (_candidateAssemblies == null)
            {
                // find all referenced libraries that have this assembly a dependency, and treat them as candidate assemblies for plugins.
                _candidateAssemblies = new List<Assembly>();
                var librariesThatReferenceThislIbrary = GetLibrariesReferencingThisLibrary();
                foreach (var item in librariesThatReferenceThislIbrary)
                {
                    _candidateAssemblies.AddRange(item.Assemblies.Select(assy =>
                    _assemblyLoadContextAccessor.Default.Load(assy))
                    );
                }
            }
            return _candidateAssemblies.ToArray();
        }

        /// <summary>
        /// Loads all mef components from all candiate assemblies, populates the IComponentRegistry with them, and also registers them with the autofac builder.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public IComponentRegistry LoadComponents(ContainerBuilder builder)
        {

            // We want to create MEF catalogues for all assemblies that might have Plugins in.
            // The rules for this are:
            // 1. If the application has a library that references this assebly, then it is considered a candidate for a plugin.
            // 2. If there is an assembly in the plugins directory, that isn't allready loaded in ILibraryManager, then it should be considered a possible 
            //    candidate for containing a plugin.. But it should always be loaded from a shadowed directory so we can safely uninstall the original at runtime.

            // 1. Create MEF catalogues for all referenced assemblies, that have a reference to Core. 
            var candidateAssemblies = GetAssembliesThatReferenceThisAssembly();
            var catalogs = candidateAssemblies.Select(assembly => new AssemblyCatalog(assembly)).Cast<ComposablePartCatalog>().ToList();

            // 2. 
            var copiedFiles = ShadowCopy(_config.Value.ModulesFolder, _shadowedPluginsDirectory);
            var copiedAssemblyFiles = copiedFiles.Where(f => f.Extension.ToLowerInvariant().EndsWith(".dll")).ToList();

            var allReferencedAssemblyNames = _libraryManager.GetLibraries().SelectMany(l => l.Assemblies).Distinct().ToList();
            //var duplicateAssemblies = allReferencedAssemblyNames.Join(copiedAssemblyFiles, assyName => assyName.Name + ".dll", assyFile => assyFile.Name)

            var allCopiedAssemblyNames = copiedAssemblyFiles.Select(a => new AssemblyName(Path.GetFileNameWithoutExtension(a.Name)));
            var nonReferencedAssemblies = allCopiedAssemblyNames.Except(allReferencedAssemblyNames, this).ToList();

            // Now we need to look at all the non referenced assemblies, and find those that have a reference to our Core assembly, those are treated as 
            // candidates for parts.
            IList<FileInfo> candidatePluginAssemblyFiles = GetAssembliesThatReferenceThisAssembly(nonReferencedAssemblies, copiedAssemblyFiles);


            var pluginAssemblyCatalogs = candidatePluginAssemblyFiles.Select((f) => (ComposablePartCatalog)new AssemblyCatalog(f.FullName));


            //  pluginAssemblies.Select(a => copiedFiles.First(b => Path.GetFileNameWithoutExtension(b.Name) ==  ))

            //.Join(copiedFiles, a => a.Name, b => Path.GetFileNameWithoutExtension(b.Name), (a, b) => b.FullName)
            //    .Select((f) => (ComposablePartCatalog)new AssemblyCatalog(f))
            //    .ToArray();

            // also include assemblies in our shadow directory where plugins are loaded.
            // catalogs.Add(new RecursiveDirectoryCatalog(_shadowedPluginsDirectory));
            catalogs.AddRange(pluginAssemblyCatalogs);

            // Compose MEF catalog for all of the candidate assemblies.
            var aggregateCatalog = new AggregateCatalog(catalogs);

            builder.RegisterComposablePartCatalog(aggregateCatalog);
            var composition = new CompositionContainer(aggregateCatalog);
            var componentRegistry = new ComponentRegistry(composition);
            builder.RegisterInstance<IComponentRegistry>(componentRegistry);

            return componentRegistry;
        }

        private IList<FileInfo> GetAssembliesThatReferenceThisAssembly(IList<AssemblyName> nonReferencedAssemblies, List<FileInfo> copiedAssemblyFiles)
        {

            var thisAssemblyName = this.GetType().Assembly.GetName();
            var candidatePlugins = new List<FileInfo>();
            foreach (var assemblyname in nonReferencedAssemblies)
            {
                var assemblyFile = copiedAssemblyFiles.FirstOrDefault(a => assemblyname.Name == Path.GetFileNameWithoutExtension(a.Name));
                if (assemblyFile != null)
                {
                    var assembly = Assembly.ReflectionOnlyLoadFrom(assemblyFile.FullName);
                    var assemblyReferences = assembly.GetReferencedAssemblies();
                    if (assemblyReferences.Contains(thisAssemblyName, this))
                    {
                        // candidate found.
                        candidatePlugins.Add(assemblyFile);
                        continue;
                    }
                }


                //foreach (var matchingAssemblyFile in assemblyFiles)
                //{


                //    // actually we will only check the first assembly with this name
                //    continue;

                //}
            }

            return candidatePlugins;
        }

        /// <summary>
        /// Copies all files from the <paramref name="pluginPath"/> into the <paramref name="shadowedDir"/> and returns a list of all the files that were copied in their new location. 
        /// </summary>
        /// <param name="pluginPath"></param>
        /// <param name="shadowedDir"></param>
        /// <returns></returns>
        private IList<FileInfo> ShadowCopy(string pluginPath, string shadowedDir)
        {
            var shadowedPlugins = new DirectoryInfo(shadowedDir);

            // remove old shadowed copies
            if (shadowedPlugins.Exists)
            {
                shadowedPlugins.Delete(true);
            }

            shadowedPlugins.Create();

            // Shadow copy plugins to avoid CLR locking.
            var copiedFiles = new List<FileInfo>();
            var plugins = new DirectoryInfo(pluginPath);
            if (plugins.Exists)
            {
                CopyAll(plugins, shadowedPlugins, copiedFiles);
            }

            return copiedFiles;

        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target, IList<FileInfo> copiedFiles)
        {
            Directory.CreateDirectory(target.FullName); // does nothing if directory already exists.

            // Copy each file into the new directory.
            foreach (FileInfo fi in source.GetFiles())
            {
                // exclude assemblies that are already loaded.
                //   Console.WriteLine(@"Copying {0}\{1}", target.FullName, fi.Name);
                var targetFilePath = Path.Combine(target.FullName, fi.Name);
                var targetFile = fi.CopyTo(targetFilePath, true);
                copiedFiles.Add(targetFile);
            }

            // Copy each subdirectory using recursion.
            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
            {
                DirectoryInfo nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir, copiedFiles);
            }
        }

        protected string GetRootedPath(string path)
        {
            if (System.IO.Path.IsPathRooted(path))
            {
                return path;
            }
            else
            {
                if (_env == null)
                {
                    throw new Exception("env was null!");
                }

                if (string.IsNullOrWhiteSpace(_env.WebRootPath))
                {
                    _env.WebRootPath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new Exception("path was null!");
                }



                return System.IO.Path.Combine(_env.WebRootPath, path);
            }
        }

        public bool Equals(AssemblyName x, AssemblyName y)
        {
            // Without using strong named assemblies this is a littel dangerous as two assemblies could have the same name,
            // but be completely different, however we would assume them to be the same name with this logic.
            // Not a problem for now, but maybe revisit in the future.
            return x.Name == y.Name;
        }

        public int GetHashCode(AssemblyName obj)
        {
            return obj.Name.GetHashCode();
        }

        /// <summary>
        /// Returns a file provider that can be used for obtaining static files that should be served up from all components.
        /// </summary>
        /// <returns></returns>
        public IFileProvider GetStaticFileProvider(IHostingEnvironment env)
        {
            List<IFileProvider> componentFileProviders = new List<IFileProvider>();

            foreach (var library in _librariesReferencingThisLibrary)
            {

                var libraryPath = library.Path;
                _logger.LogInformation("Inspecting Module Path for project.json: {libraryPath}", libraryPath);
                string rootPath = libraryPath;


                if (!rootPath.ToLowerInvariant().EndsWith("project.json"))
                {
                    // When running after a proper deployment,
                    // rootPath doesn't see to point to the project.json, 
                    // so we need to fix up the path a bit. perhaps this is t do with the fact that
                    // the library type is changed form a project library, to a nuget package library during the dnu publish procedure.
                    var moduleProjectJson = Path.Combine(rootPath, "root\\project.json");
                    if (File.Exists(moduleProjectJson))
                    {
                        rootPath = moduleProjectJson;
                    }
                }

                // read webroot dir from project.json file if we have one.
                if (rootPath.ToLowerInvariant().EndsWith("project.json"))
                {
                    // Read the wwwroot attribute from the project.json file in order to get the modules public folder for serving static content files.
                    using (StreamReader r = new StreamReader(rootPath))
                    {
                        string json = r.ReadToEnd();
                        var projectJson = JsonConvert.DeserializeObject<ProjectJsonFile>(json);
                        rootPath = Path.GetDirectoryName(rootPath);

                        if (!string.IsNullOrWhiteSpace(projectJson.webroot))
                        {
                            rootPath = Path.Combine(rootPath, projectJson.webroot);
                        }

                        _logger.LogInformation(
                            "Module {Name} is configured with a webroot path of {rootPath} in it's project.json.",
                            library.Name, rootPath);

                    }
                }
                else
                {
                    // This is most likely a module that is just referenced as a NuGet package.
                    // Therefore use it's "Content" folder, to serve module content files from.
                    rootPath = Path.Combine(rootPath, "Content");
                }


                if (Directory.Exists(rootPath))
                {
                    var phsyicalFileProvider = new PhysicalFileProvider(rootPath);
                    componentFileProviders.Add(phsyicalFileProvider);
                    _logger.LogInformation("Physical file provider created for module webroot {rootPath}", rootPath);
                }
                else
                {
                    _logger.LogWarning("Skipped creating a physical file provider for a non existent module webroot path {rootPath}", rootPath);
                }

            }

            // add the file provider that serves files from the hosting environment wwwroot directory.
            componentFileProviders.Add(env.WebRootFileProvider);

            var fileProvider = new CompositeFileProvider(componentFileProviders);
            return fileProvider;

        }

        public class ProjectJsonFile
        {
            public string webroot { get; set; }
        }
    }
}
