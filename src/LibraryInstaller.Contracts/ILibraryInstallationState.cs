﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;

namespace LibraryInstaller.Contracts
{
    /// <summary>
    /// An interface that describes the library to install.
    /// </summary>
    public interface ILibraryInstallationState
    {
        /// <summary>
        /// The identifyer to uniquely identify the library
        /// </summary>
        string LibraryId { get; }

        /// <summary>
        /// The unique identifier of the provider.
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// The list of file names to install
        /// </summary>
        IReadOnlyList<string> Files { get; }

        /// <summary>
        /// The path relative to the working directory to copy the files to.
        /// </summary>
        string DestinationPath { get; }
    }
}
