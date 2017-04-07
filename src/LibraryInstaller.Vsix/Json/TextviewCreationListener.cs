﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Threading;
using LibraryInstaller.Contracts;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LibraryInstaller.Vsix.Json
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("JSON")]
    [TextViewRole(PredefinedTextViewRoles.Debuggable)]
    public class TextviewCreationListener : IWpfTextViewCreationListener
    {
        private Manifest _manifest;
        private Dependencies _dependencies;

        [Import]
        private ITextDocumentFactoryService DocumentService { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            if (!DocumentService.TryGetTextDocument(textView.TextBuffer, out var doc))
                return;

            string fileName = Path.GetFileName(doc.FilePath);

            if (!fileName.Equals(Constants.ConfigFileName, StringComparison.OrdinalIgnoreCase))
                return;

            _dependencies = Dependencies.FromConfigFile(doc.FilePath);
            _manifest = Manifest.FromFileAsync(doc.FilePath, _dependencies, CancellationToken.None).Result;

            doc.FileActionOccurred += OnFileSavedAsync;
            textView.Closed += OnViewClosed;
        }

        private async Task RemoveFilesAsync(Manifest newManifest)
        {
            var hostInteraction = _dependencies.GetHostInteractions() as HostInteraction;

            foreach (ILibraryInstallationState prevState in _manifest.Libraries)
            {
                ILibraryInstallationState newState = newManifest.Libraries.FirstOrDefault(l => l.LibraryId == prevState.LibraryId && l.ProviderId == prevState.ProviderId);
                IEnumerable<string> prevFiles = await GetFilesAsync(prevState).ConfigureAwait(false);

                if (newState == null || newState.DestinationPath != prevState.DestinationPath)
                {
                    hostInteraction.DeleteFiles(prevFiles?.Select(f => Path.Combine(prevState.DestinationPath, f)).ToArray());
                }
                else
                {
                    IEnumerable<string> newFiles = await GetFilesAsync(newState).ConfigureAwait(false);
                    IEnumerable<string> diffFiles = prevFiles.Where(f => !newFiles.Contains(f));
                    hostInteraction.DeleteFiles(diffFiles.Select(f => Path.Combine(prevState.DestinationPath, f)).ToArray());
                }
            }
        }

        private async Task<IEnumerable<string>> GetFilesAsync(ILibraryInstallationState state)
        {
            if (state.Files == null)
            {
                ILibraryCatalog catalog = _dependencies.GetProvider(state.ProviderId)?.GetCatalog();

                if (catalog != null)
                {
                    ILibrary library = await catalog.GetLibraryAsync(state.LibraryId, CancellationToken.None);
                    return library?.Files.Keys.ToList();
                }
            }

            return state.Files;
        }

        private void OnFileSavedAsync(object sender, TextDocumentFileActionEventArgs e)
        {
            if (e.FileActionType == FileActionTypes.ContentSavedToDisk)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Manifest newManifest = await Manifest.FromFileAsync(e.FilePath, _dependencies, CancellationToken.None);
                        await RemoveFilesAsync(newManifest);

                        _manifest = newManifest;

                        await LibraryHelpers.RestoreAsync(e.FilePath, CancellationToken.None);
                        Telemetry.TrackOperation("restoresave");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogEvent(ex.ToString(), Contracts.LogLevel.Error);
                        Telemetry.TrackException("configsaved", ex);
                    }
                });
            }
        }

        private void OnViewClosed(object sender, EventArgs e)
        {
            var view = (IWpfTextView)sender;

            if (DocumentService.TryGetTextDocument(view.TextBuffer, out var doc))
            {
                doc.FileActionOccurred -= OnFileSavedAsync;
            }
        }
    }
}
