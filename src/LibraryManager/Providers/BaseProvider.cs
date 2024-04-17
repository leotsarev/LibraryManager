﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Cache;
using Microsoft.Web.LibraryManager.Contracts;
using Microsoft.Web.LibraryManager.Helpers;
using Microsoft.Web.LibraryManager.LibraryNaming;
using Microsoft.Web.LibraryManager.Utilities;

namespace Microsoft.Web.LibraryManager.Providers
{
    /// <summary>
    /// Default implementation for a provider, since most provider implementations are very similar.
    /// </summary>
    internal abstract class BaseProvider : IProvider
    {
        protected readonly CacheService _cacheService;
        private string _cacheFolder;

        public BaseProvider(IHostInteraction hostInteraction, CacheService cacheService)
        {
            HostInteraction = hostInteraction;
            _cacheService = cacheService;
        }

        #region IProvider implementation

        /// <inheritdoc />
        public abstract string Id { get; }

        /// <inheritdoc />
        public virtual string NuGetPackageId => "Microsoft.Web.LibraryManager.Build";

        /// <inheritdoc />
        public abstract string LibraryIdHintText { get; }

        /// <inheritdoc />
        public IHostInteraction HostInteraction { get; }

        /// <inheritdoc />
        public virtual bool SupportsLibraryVersions => true;

        /// <inheritdoc />
        public abstract ILibraryCatalog GetCatalog();

        /// <inheritdoc />
        public abstract string GetSuggestedDestination(ILibrary library);

        /// <inheritdoc />
        public virtual async Task<ILibraryOperationResult> InstallAsync(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(desiredState);
            }

            OperationResult<ILibrary> getLibrary = await GetLibraryForInstallationState(desiredState, cancellationToken).ConfigureAwait(false);
            if (!getLibrary.Success)
            {
                return new LibraryOperationResult(desiredState, [.. getLibrary.Errors])
                {
                    Cancelled = getLibrary.Cancelled,
                };
            }

            OperationResult<LibraryInstallationGoalState> getGoalState = GenerateGoalState(desiredState, getLibrary.Result);
            if (!getGoalState.Success)
            {
                return new LibraryOperationResult(desiredState, [.. getGoalState.Errors])
                {
                    Cancelled = getGoalState.Cancelled,
                };
            }

            LibraryInstallationGoalState goalState = getGoalState.Result;

            if (!IsSourceCacheReady(goalState))
            {
                ILibraryOperationResult updateCacheResult = await RefreshCacheAsync(desiredState, getLibrary.Result, cancellationToken);
                if (!updateCacheResult.Success)
                {
                    return updateCacheResult;
                }
            }

            if (goalState.IsAchieved())
            {
                return LibraryOperationResult.FromUpToDate(desiredState);
            }

            return await InstallFiles(goalState, cancellationToken);

        }

        private async Task<OperationResult<ILibrary>> GetLibraryForInstallationState(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            ILibrary library;
            try
            {
                ILibraryCatalog catalog = GetCatalog();
                library = await catalog.GetLibraryAsync(desiredState.Name, desiredState.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidLibraryException)
            {
                string libraryId = LibraryNamingScheme.GetLibraryId(desiredState.Name, desiredState.Version);
                return OperationResult<ILibrary>.FromError(PredefinedErrors.UnableToResolveSource(libraryId, desiredState.ProviderId));
            }
            catch (Exception ex)
            {
                HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                return OperationResult<ILibrary>.FromError(PredefinedErrors.UnknownException());
            }

            return OperationResult<ILibrary>.FromSuccess(library);
        }

        private async Task<LibraryOperationResult> InstallFiles(LibraryInstallationGoalState goalState, CancellationToken cancellationToken)
        {
            try
            {
                foreach (KeyValuePair<string, string> kvp in goalState.InstalledFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return LibraryOperationResult.FromCancelled(goalState.InstallationState);
                    }

                    string sourcePath = kvp.Value;
                    string destinationPath = kvp.Key;
                    bool writeOk = await HostInteraction.CopyFileAsync(sourcePath, destinationPath, cancellationToken);

                    if (!writeOk)
                    {
                        return new LibraryOperationResult(goalState.InstallationState, PredefinedErrors.CouldNotWriteFile(destinationPath));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return new LibraryOperationResult(goalState.InstallationState, PredefinedErrors.PathOutsideWorkingDirectory());
            }
            catch (Exception ex)
            {
                HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                return new LibraryOperationResult(goalState.InstallationState, PredefinedErrors.UnknownException());
            }

            return LibraryOperationResult.FromSuccess(goalState.InstallationState);
        }

        /// <inheritdoc />
        public virtual async Task<ILibraryOperationResult> UpdateStateAsync(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(desiredState);
            }

            string libraryId = LibraryNamingScheme.GetLibraryId(desiredState.Name, desiredState.Version);
            try
            {
                ILibraryCatalog catalog = GetCatalog();

                if (string.Equals(desiredState.Version, ManifestConstants.LatestVersion, StringComparison.Ordinal))
                {
                    // replace the @latest version with the latest version from the catalog.  This redirect
                    // ensures that as new versions are released, we will not reuse stale "latest" assets
                    // from the cache.
                    string latestVersion = await catalog.GetLatestVersion(libraryId, includePreReleases: false, cancellationToken).ConfigureAwait(false);
                    LibraryInstallationState newState = LibraryInstallationState.FromInterface(desiredState);
                    newState.Version = latestVersion;
                    desiredState = newState;
                }

                ILibrary library = await catalog.GetLibraryAsync(desiredState.Name, desiredState.Version, cancellationToken).ConfigureAwait(false);

                if (library == null)
                {
                    return new LibraryOperationResult(desiredState, PredefinedErrors.UnableToResolveSource(desiredState.Name, desiredState.ProviderId));
                }

                if (desiredState.Files != null && desiredState.Files.Count > 0)
                {
                    // expand any potential file patterns
                    IEnumerable<string> updatedFiles = FileGlobbingUtility.ExpandFileGlobs(desiredState.Files, library.Files.Keys);
                    var processedState = new LibraryInstallationState
                    {
                        Name = desiredState.Name,
                        Version = desiredState.Version,
                        ProviderId = desiredState.ProviderId,
                        DestinationPath = desiredState.DestinationPath,
                        IsUsingDefaultDestination = desiredState.IsUsingDefaultDestination,
                        IsUsingDefaultProvider = desiredState.IsUsingDefaultProvider,
                        Files = updatedFiles.ToList(),
                    };

                    return CheckForInvalidFiles(processedState, libraryId, library);
                }

                desiredState = new LibraryInstallationState
                {
                    ProviderId = Id,
                    Name = desiredState.Name,
                    Version = desiredState.Version,
                    DestinationPath = desiredState.DestinationPath,
                    Files = library.Files.Keys.ToList(),
                    IsUsingDefaultDestination = desiredState.IsUsingDefaultDestination,
                    IsUsingDefaultProvider = desiredState.IsUsingDefaultProvider
                };
            }
            catch (InvalidLibraryException)
            {
                return new LibraryOperationResult(desiredState, PredefinedErrors.UnableToResolveSource(libraryId, desiredState.ProviderId));
            }
            catch (UnauthorizedAccessException)
            {
                return new LibraryOperationResult(desiredState, PredefinedErrors.PathOutsideWorkingDirectory());
            }
            catch (Exception ex)
            {
                HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                return new LibraryOperationResult(desiredState, PredefinedErrors.UnknownException());
            }

            return LibraryOperationResult.FromSuccess(desiredState);
        }

        public async Task<OperationResult<LibraryInstallationGoalState>> GetInstallationGoalStateAsync(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            // get the library from the catalog
            OperationResult<ILibrary> getLibrary = await GetLibraryForInstallationState(desiredState, cancellationToken).ConfigureAwait(false);
            if (!getLibrary.Success)
            {
                return OperationResult<LibraryInstallationGoalState>.FromErrors([.. getLibrary.Errors]);
            }

            return GenerateGoalState(desiredState, getLibrary.Result);
        }

        #endregion

        private OperationResult<LibraryInstallationGoalState> GenerateGoalState(ILibraryInstallationState desiredState, ILibrary library)
        {
            var goalState = new LibraryInstallationGoalState(desiredState);
            List<IError> errors = null;

            if (string.IsNullOrEmpty(desiredState.DestinationPath))
            {
                return OperationResult<LibraryInstallationGoalState>.FromError(PredefinedErrors.DestinationNotSpecified(desiredState.Name));
            }

            IEnumerable<string> outFiles;
            if (desiredState.Files == null || desiredState.Files.Count == 0)
            {
                outFiles = library.Files.Keys;
            }
            else
            {
                outFiles = FileGlobbingUtility.ExpandFileGlobs(desiredState.Files, library.Files.Keys);
            }

            if (library.GetInvalidFiles(outFiles.ToList()) is IReadOnlyList<string> invalidFiles
                && invalidFiles.Count > 0)
            {
                errors ??= [];
                errors.Add(PredefinedErrors.InvalidFilesInLibrary(desiredState.Name, invalidFiles, library.Files.Keys));
            }

            foreach (string outFile in outFiles)
            {
                // strip the source prefix
                string destinationFile = Path.Combine(HostInteraction.WorkingDirectory, desiredState.DestinationPath, outFile);
                if (!FileHelpers.IsUnderRootDirectory(destinationFile, HostInteraction.WorkingDirectory))
                {
                    errors ??= [];
                    errors.Add(PredefinedErrors.PathOutsideWorkingDirectory());
                }
                destinationFile = FileHelpers.NormalizePath(destinationFile);

                // don't forget to include the cache folder in the path
                string sourceFile = GetCachedFileLocalPath(desiredState, outFile);
                sourceFile = FileHelpers.NormalizePath(sourceFile);

                // TODO: make goalState immutable
                // map destination back to the library-relative file it originated from
                goalState.InstalledFiles.Add(destinationFile, sourceFile);
            }

            if (errors is not null)
            {
                return OperationResult<LibraryInstallationGoalState>.FromErrors([.. errors]);
            }

            return OperationResult<LibraryInstallationGoalState>.FromSuccess(goalState);
        }

        public bool IsSourceCacheReady(LibraryInstallationGoalState goalState)
        {
            foreach (KeyValuePair<string, string> item in goalState.InstalledFiles)
            {
                string cachePath = GetCachedFileLocalPath(goalState.InstallationState, item.Value);
                // TODO: use abstraction for filesystem ops
                if (!File.Exists(cachePath))
                {
                    return false;
                }
            }

            return true;
        }

        protected virtual ILibraryOperationResult CheckForInvalidFiles(ILibraryInstallationState desiredState, string libraryId, ILibrary library)
        {
            IReadOnlyList<string> invalidFiles = library.GetInvalidFiles(desiredState.Files);
            if (invalidFiles.Count > 0)
            {
                IError invalidFilesError = PredefinedErrors.InvalidFilesInLibrary(libraryId, invalidFiles, library.Files.Keys);
                return new LibraryOperationResult(desiredState, invalidFilesError);
            }
            else
            {
                return LibraryOperationResult.FromSuccess(desiredState);
            }
        }

        protected virtual ILibraryNamingScheme LibraryNamingScheme { get; } = new VersionedLibraryNamingScheme();

        public string CacheFolder
        {
            get { return _cacheFolder ?? (_cacheFolder = Path.Combine(HostInteraction.CacheDirectory, Id)); }
        }

        /// <summary>
        /// Copy files from the download cache to the desired installation state
        /// </summary>
        /// <remarks>Precondition: all files must already exist in the cache</remarks>
        protected async Task<ILibraryOperationResult> WriteToFilesAsync(ILibraryInstallationState state, CancellationToken cancellationToken)
        {
            if (state.Files != null)
            {
                try
                {
                    foreach (string file in state.Files)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return LibraryOperationResult.FromCancelled(state);
                        }

                        if (string.IsNullOrEmpty(file))
                        {
                            string id = LibraryNamingScheme.GetLibraryId(state.Name, state.Version);
                            return new LibraryOperationResult(state, PredefinedErrors.FileNameMustNotBeEmpty(id));
                        }

                        string sourcePath = GetCachedFileLocalPath(state, file);
                        string destinationPath = Path.Combine(state.DestinationPath, file);
                        bool writeOk = await HostInteraction.CopyFileAsync(sourcePath, destinationPath, cancellationToken);

                        if (!writeOk)
                        {
                            return new LibraryOperationResult(state, PredefinedErrors.CouldNotWriteFile(file));
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return new LibraryOperationResult(state, PredefinedErrors.PathOutsideWorkingDirectory());
                }
                catch (Exception ex)
                {
                    HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                    return new LibraryOperationResult(state, PredefinedErrors.UnknownException());
                }
            }

            return LibraryOperationResult.FromSuccess(state);
        }

        /// <summary>
        /// Gets the expected local path for a file from the file cache
        /// </summary>
        /// <returns></returns>
        private string GetCachedFileLocalPath(ILibraryInstallationState state, string sourceFile)
        {
            return Path.Combine(CacheFolder, state.Name, state.Version, sourceFile.Trim('/'));
        }

        /// <summary>
        /// Copies ILibraryInstallationState files to cache
        /// </summary>
        /// <param name="state"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<ILibraryOperationResult> RefreshCacheAsync(ILibraryInstallationState state, ILibrary library, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(state);
            }

            string libraryDir = Path.Combine(CacheFolder, state.Name, state.Version);

            try
            {
                IEnumerable<string> filesToCache;
                // expand "files" to concrete files in the library
                if (state.Files == null || state.Files.Count == 0)
                {
                    filesToCache = library.Files.Keys;
                }
                else
                {
                    filesToCache = FileGlobbingUtility.ExpandFileGlobs(state.Files, library.Files.Keys);
                }

                var librariesMetadata = new HashSet<CacheFileMetadata>();
                foreach (string sourceFile in filesToCache)
                {
                    string cacheFile = Path.Combine(libraryDir, sourceFile);
                    string url = GetDownloadUrl(state, sourceFile);

                    var newEntry = new CacheFileMetadata(url, cacheFile);
                    librariesMetadata.Add(newEntry);
                }
                await _cacheService.RefreshCacheAsync(librariesMetadata, HostInteraction.Logger, cancellationToken);
            }
            catch (ResourceDownloadException ex)
            {
                HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                return new LibraryOperationResult(state, PredefinedErrors.FailedToDownloadResource(ex.Url));
            }
            catch (OperationCanceledException)
            {
                return LibraryOperationResult.FromCancelled(state);
            }
            catch (Exception ex)
            {
                HostInteraction.Logger.Log(ex.InnerException.ToString(), LogLevel.Error);
                return new LibraryOperationResult(state, PredefinedErrors.UnknownException());
            }

            return LibraryOperationResult.FromSuccess(state);
        }

        protected abstract string GetDownloadUrl(ILibraryInstallationState state, string sourceFile);
    }
}
