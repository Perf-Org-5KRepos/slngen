﻿// Copyright (c) Microsoft Corporation.
//
// Licensed under the MIT license.

using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SlnGen.Common
{
    /// <summary>
    /// A class for loading MSBuild projects and their project references.
    /// </summary>
    public sealed class MSBuildProjectLoader : IMSBuildProjectLoader
    {
        /// <summary>
        /// The name of the <ProjectReference /> item in MSBuild projects.
        /// </summary>
        private const string ProjectReferenceItemName = "ProjectReference";

        private static readonly ProjectLoadSettings DefaultProjectLoadSettings = ProjectLoadSettings.IgnoreEmptyImports
                                                                                         | ProjectLoadSettings.IgnoreInvalidImports
                                                                                 | ProjectLoadSettings.IgnoreMissingImports
                                                                                 | ProjectLoadSettings.DoNotEvaluateElementsWithFalseCondition;

        /// <summary>
        /// Stores the list of paths to the projects that are loaded.
        /// </summary>
        private readonly HashSet<string> _loadedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ISlnGenLogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="MSBuildProjectLoader"/> class.
        /// </summary>
        /// <param name="logger">An <see cref="ISlnGenLogger" /> to use for logging.</param>
        public MSBuildProjectLoader(ISlnGenLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets or sets a value indicating whether statistics should be collected.
        /// </summary>
        public bool CollectStats { get; set; } = false;

        /// <summary>
        /// Gets or sets a <see cref="Func{Project, Boolean}"/> that determines if a project is a traversal project.
        /// </summary>
        public Func<Project, bool> IsTraversalProject { get; set; } = project => string.Equals("true", project.GetPropertyValue("IsTraversal"));

        /// <summary>
        /// Gets a <see cref="MSBuildProjectLoaderStatistics" /> object containing project load times.
        /// </summary>
        public MSBuildProjectLoaderStatistics Statistics { get; } = new MSBuildProjectLoaderStatistics();

        /// <summary>
        /// Gets or sets the item names that specify project references in a traversal project.  The default value is "ProjectFile".
        /// </summary>
        public string TraversalProjectFileItemName { get; set; } = "ProjectFile";

        /// <inheritdoc cref="IMSBuildProjectLoader.LoadProjects" />
        public void LoadProjects(ProjectCollection projectCollection, IDictionary<string, string> globalProperties, IEnumerable<string> projectPaths)
        {
            Parallel.ForEach(projectPaths, projectPath => { LoadProject(projectPath, projectCollection, globalProperties); });
        }

        /// <summary>
        /// Loads a single project if it hasn't already been loaded.
        /// </summary>
        /// <param name="projectPath">The path to the project.</param>
        /// <param name="projectCollection">A <see cref="ProjectCollection"/> to load the project into.</param>
        /// <param name="globalProperties">The <see cref="IDictionary{String,String}" /> to use when evaluating the project.</param>
        private void LoadProject(string projectPath, ProjectCollection projectCollection, IDictionary<string, string> globalProperties)
        {
            if (TryLoadProject(projectPath, projectCollection.DefaultToolsVersion, projectCollection, globalProperties, out Project project))
            {
                LoadProjectReferences(project, globalProperties);
            }
        }

        /// <summary>
        /// Loads the project references of the specified project.
        /// </summary>
        /// <param name="project">The <see cref="Project"/> to load the references of.</param>
        /// <param name="globalProperties">The <see cref="IDictionary{String,String}" /> to use when evaluating the project.</param>
        private void LoadProjectReferences(Project project, IDictionary<string, string> globalProperties)
        {
            IEnumerable<ProjectItem> projects = project.GetItems(ProjectReferenceItemName);

            if (IsTraversalProject(project))
            {
                projects = projects.Concat(project.GetItems(TraversalProjectFileItemName));
            }

            Parallel.ForEach(projects, projectReferenceItem =>
            {
                string projectReferencePath = Path.IsPathRooted(projectReferenceItem.EvaluatedInclude) ? projectReferenceItem.EvaluatedInclude : Path.GetFullPath(Path.Combine(projectReferenceItem.Project.DirectoryPath, projectReferenceItem.EvaluatedInclude));

                LoadProject(projectReferencePath, projectReferenceItem.Project.ProjectCollection, globalProperties);
            });
        }

        /// <summary>
        /// Attempts to load the specified project if it hasn't already been loaded.
        /// </summary>
        /// <param name="path">The path to the project to load.</param>
        /// <param name="toolsVersion">The ToolsVersion to use when loading the project.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection"/> to load the project into.</param>
        /// <param name="globalProperties">The <see cref="IDictionary{String,String}" /> to use when evaluating the project.</param>
        /// <param name="project">Contains the loaded <see cref="Project"/> if one was loaded.</param>
        /// <returns><code>true</code> if the project was loaded, otherwise <code>false</code>.</returns>
        private bool TryLoadProject(string path, string toolsVersion, ProjectCollection projectCollection, IDictionary<string, string> globalProperties, out Project project)
        {
            project = null;

            bool shouldLoadProject;

            string fullPath = path.ToFullPathInCorrectCase();

            lock (_loadedProjects)
            {
                shouldLoadProject = _loadedProjects.Add(fullPath);
            }

            if (!shouldLoadProject)
            {
                return false;
            }

            long now = DateTime.Now.Ticks;

            try
            {
                project = new Project(fullPath, globalProperties, toolsVersion, projectCollection, DefaultProjectLoadSettings);
            }
            catch (InvalidProjectFileException e)
            {
                _logger.LogError(e.Message, e.ErrorCode, e.ProjectFile, e.LineNumber, e.ColumnNumber);

                return false;
            }
            catch (Exception e)
            {
                _logger.LogError(message: e.ToString());

                return false;
            }

            if (CollectStats)
            {
                Statistics.TryAddProjectLoadTime(path, TimeSpan.FromTicks(DateTime.Now.Ticks - now));
            }

            return true;
        }
    }
}