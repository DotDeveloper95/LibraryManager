﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;

namespace Microsoft.Web.LibraryManager.Providers.Unpkg
{
    internal class UnpkgProvider : IProvider
    {
        public const string IdText = "unpkg";
        public const string DownloadUrlFormat = "http://unpkg.com/{0}@{1}/{2}";

        public UnpkgProvider(IHostInteraction hostInteraction)
        {
            HostInteraction = hostInteraction;
            // TODO: {alexgav} Do we need multiple instances of CacheService?
            _cacheService = new CacheService(WebRequestHandler.Instance);
        }

        public string Id => IdText;

        public string NuGetPackageId { get; } = "Microsoft.Web.LibraryManager.Build";

        public IHostInteraction HostInteraction { get; }

        private CacheService _cacheService;
        private ILibraryCatalog _catalog;

        public ILibraryCatalog GetCatalog()
        {
            return _catalog ?? (_catalog = new UnpkgCatalog(this));
        }

        // TODO: {alexgav} Could got to a command provider base class
        internal string CacheFolder
        {
            get { return Path.Combine(HostInteraction.CacheDirectory, Id); }
        }

        public string LibraryIdHintText => Resources.Text.UnpkgProviderHintText;

        public async Task<ILibraryOperationResult> InstallAsync(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(desiredState);
            }

            if (!desiredState.IsValid(out IEnumerable<IError> errors))
            {
                return new LibraryOperationResult(desiredState, errors.ToArray());
            }

            //Expand the files property if needed
            ILibraryOperationResult updateResult = await UpdateStateAsync(desiredState, cancellationToken);
            if (!updateResult.Success)
            {
                return updateResult;
            }

            desiredState = updateResult.InstallationState;

            // Refresh cache if needed
            ILibraryOperationResult cacheUpdateResult = await RefreshCacheAsync(desiredState, cancellationToken);
            if (!cacheUpdateResult.Success)
            {
                return cacheUpdateResult;
            }

            // Check if Library is already up tp date
            if (IsLibraryUpToDateAsync(desiredState, cancellationToken))
            {
                return LibraryOperationResult.FromUpToDate(desiredState);
            }

            // Write files to destination
            return await WriteToFilesAsync(desiredState, cancellationToken);
        }

        /// <summary>
        /// Copies ILibraryInstallationState files to cache
        /// </summary>
        /// <param name="state"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<LibraryOperationResult> RefreshCacheAsync(ILibraryInstallationState state, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(state);
            }

            var tasks = new List<Task>();
            string[] args = state.LibraryId.Split('@');
            string name = args[0];
            string version = args[1];

            string libraryDir = Path.Combine(CacheFolder, name);

            try
            {
                List<CacheServiceMetadata> librariesMetadata = new List<CacheServiceMetadata>();
                foreach (string sourceFile in state.Files)
                {
                    string cacheFile = Path.Combine(libraryDir, version, sourceFile);
                    string url = string.Format(DownloadUrlFormat, name, version, sourceFile);

                    CacheServiceMetadata newEntry = new CacheServiceMetadata(url, cacheFile);
                    if (!librariesMetadata.Contains(newEntry))
                    {
                        librariesMetadata.Add(new CacheServiceMetadata(url, cacheFile));
                    }
                }
                await _cacheService.RefreshCacheAsync(librariesMetadata, cancellationToken);
            }
            catch (ResourceDownloadException ex)
            {
                HostInteraction.Logger.Log(ex.ToString(), LogLevel.Error);
                return new LibraryOperationResult(state, PredefinedErrors.FailedToDownloadResource(ex.Url));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                HostInteraction.Logger.Log(ex.InnerException.ToString(), LogLevel.Error);
                return new LibraryOperationResult(state, PredefinedErrors.UnknownException());
            }

            return LibraryOperationResult.FromSuccess(state);
        }

        private bool IsLibraryUpToDateAsync(ILibraryInstallationState state, CancellationToken cancellationToken)
        {
            string[] args = state.LibraryId.Split('@');
            string name = args[0];
            string version = args[1];

            string cacheDir = Path.Combine(CacheFolder, name, version);
            string destinationDir = Path.Combine(HostInteraction.WorkingDirectory, state.DestinationPath);

            try
            {
                foreach (string sourceFile in state.Files)
                {
                    var destinationFile = new FileInfo(Path.Combine(destinationDir, sourceFile).Replace('\\', '/'));
                    var cacheFile = new FileInfo(Path.Combine(cacheDir, sourceFile).Replace('\\', '/'));

                    if (!destinationFile.Exists || !cacheFile.Exists || !FileHelpers.AreFilesUpToDate(destinationFile, cacheFile))
                    {
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                // Log failure here 
                return false;
            }

            return true;
        }

        private async Task<ILibraryOperationResult> WriteToFilesAsync(ILibraryInstallationState state, CancellationToken cancellationToken)
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
                            return new LibraryOperationResult(state, PredefinedErrors.CouldNotWriteFile(file));
                        }

                        string destinationPath = Path.Combine(state.DestinationPath, file);
                        var sourceStream = new Func<Stream>(() => GetStreamAsync(state, file, cancellationToken).Result);
                        bool writeOk = await HostInteraction.WriteFileAsync(destinationPath, sourceStream, state, cancellationToken).ConfigureAwait(false);

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

        private async Task<Stream> GetStreamAsync(ILibraryInstallationState state, string sourceFile, CancellationToken cancellationToken)
        {
            string name = GetLibraryName(state);
            string version = GetLibraryVersion(state);

            string absolute = Path.Combine(CacheFolder, name, version, sourceFile);

            if (File.Exists(absolute))
            {
                return await HostInteraction.ReadFileAsync(absolute, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        private string GetLibraryName(ILibraryInstallationState state)
        {
            string[] args = state.LibraryId.Split('@');
            string name = args[0];

            return name;
        }

        private string GetLibraryVersion(ILibraryInstallationState state)
        {
            string[] args = state.LibraryId.Split('@');
            string version = args[1];

            return version;
        }

        public async Task<ILibraryOperationResult> UpdateStateAsync(ILibraryInstallationState desiredState, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return LibraryOperationResult.FromCancelled(desiredState);
            }

            try
            {
                ILibraryCatalog catalog = GetCatalog();
                ILibrary library = await catalog.GetLibraryAsync(desiredState.LibraryId, cancellationToken).ConfigureAwait(false);

                if (library == null)
                {
                    throw new InvalidLibraryException(desiredState.LibraryId, Id);
                }

                if (desiredState.Files != null && desiredState.Files.Count > 0)
                {
                    IReadOnlyList<string> invalidFiles = library.GetInvalidFiles(desiredState.Files);
                    if (invalidFiles.Any())
                    {
                        var invalidFilesError = PredefinedErrors.InvalidFilesInLibrary(desiredState.LibraryId, invalidFiles, library.Files.Keys);
                        return new LibraryOperationResult(desiredState, invalidFilesError);
                    }
                    else
                    {
                        return LibraryOperationResult.FromSuccess(desiredState);
                    }
                }

                desiredState = new LibraryInstallationState
                {
                    ProviderId = Id,
                    LibraryId = desiredState.LibraryId,
                    DestinationPath = desiredState.DestinationPath,
                    Files = library.Files.Keys.ToList(),
                };
            }
            catch (Exception ex) when (ex is InvalidLibraryException || ex.InnerException is InvalidLibraryException)
            {
                return new LibraryOperationResult(desiredState, PredefinedErrors.UnableToResolveSource(desiredState.LibraryId, desiredState.ProviderId));
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
    }
}
