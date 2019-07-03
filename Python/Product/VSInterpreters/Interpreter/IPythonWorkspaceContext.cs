﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.PythonTools.Interpreter {
    public interface IPythonWorkspaceContext : IDisposable {
        /// <summary>
        /// Interpreter setting string has changed.
        /// </summary>
        event EventHandler InterpreterSettingChanged;

        /// <summary>
        /// Search paths setting has changed.
        /// </summary>
        event EventHandler SearchPathsSettingChanged;

        /// <summary>
        /// Effective interpreter factory for the workspace has changed.
        /// </summary>
        event EventHandler ActiveInterpreterChanged;

        /// <summary>
        /// Test settings for the workspace has changed.
        /// </summary>
        event EventHandler TestSettingChanged;

        /// <summary>
        /// Display name for the workspace.
        /// </summary>
        string WorkspaceName { get; }

        /// <summary>
        /// Full path to the workspace folder.
        /// </summary>
        string Location { get; }

        /// <summary>
        /// Effective interpreter factory for the workspace.
        /// If there's no interpreter setting specified in
        /// workspace, then this will be the global default.
        /// May be <c>null</c> if no interpreter factories are
        /// installed on the system.
        /// </summary>
        IPythonInterpreterFactory CurrentFactory { get; }

        /// <summary>
        /// Whether the <see cref="CurrentFactory"/> is set to the global default.
        /// </summary>
        bool IsCurrentFactoryDefault { get; }

        /// <summary>
        /// Get an absolute path from a workspace relative path.
        /// </summary>
        /// <param name="path">Relative path.</param>
        /// <returns>Absolute path.</returns>
        string MakeRooted(string path);

        /// <summary>
        /// Return the property setting from PythonSettings.json.
        /// </summary>
        /// <param name="propertyName">Property to retrieve.</param>
        /// <returns>Property value.</returns>
        string GetStringProperty(string propertyName);

        /// <summary>
        /// Return the property setting from PythonSettings.json.
        /// </summary>
        /// <param name="propertyName">Property to retrieve.</param>
        /// <returns>Property value.</returns>
        bool? GetBoolProperty(string propertyName);

        /// <summary>
        /// Read the interpreter setting string from PythonSettings.json.
        /// </summary>
        /// <remarks>
        /// Most callers should use <see cref="CurrentFactory"/> but
        /// some factory providers need to read the setting directly.
        /// </remarks>
        /// <returns>
        /// Current value of the interpreter setting. May be <c>null</c>.
        /// </returns>
        string ReadInterpreterSetting();

        /// <summary>
        /// Get the effective search paths for the workspace, calculated
        /// from the SearchPaths array setting in PythonSettings.json.
        /// </summary>
        /// <remarks>
        /// The workspace folder is always included as the first item.
        /// </remarks>
        /// <returns>Absolute search paths.</returns>
        IEnumerable<string> GetAbsoluteSearchPaths();

        /// <summary>
        /// Get the absolute path to the workspace's requirements.txt file.
        /// </summary>
        /// <returns>
        /// Absolute path to file or <c>null</c> if there is none.
        /// </returns>
        string GetRequirementsTxtPath();

        /// <summary>
        /// Get the absolute path to the workspace's environment.yml file.
        /// </summary>
        /// <returns>
        /// Absolute path to file or <c>null</c> if there is none.
        /// </returns>
        string GetEnvironmentYmlPath();

        /// <summary>
        /// Set a property to the specified value. If the value is <c>null</c>,
        /// the property is removed if it already exists.
        /// </summary>
        /// <param name="propertyName">Name of property.</param>
        /// <param name="propertyVal">Value of property.</param>
        Task SetPropertyAsync(string propertyName, string propertyVal);

        /// <summary>
        /// Set a property to the specified value. If the value is <c>null</c>,
        /// the property is removed if it already exists.
        /// </summary>
        /// <param name="propertyName">Name of property.</param>
        /// <param name="propertyVal">Value of property.</param>
        Task SetPropertyAsync(string propertyName, bool? propertyVal);

        /// <summary>
        /// Update the interpreter setting in PythonSettings.json to
        /// the specified factory's id or interpreter path, as appropriate.
        /// </summary>
        /// <remarks>
        /// This will raise a <see cref="ActiveInterpreterChanged"/> if there
        /// is an effective interpreter change.
        /// </remarks>
        /// <param name="factory">New factory.</param>
        Task SetInterpreterFactoryAsync(IPythonInterpreterFactory factory);

        /// <summary>
        /// Update the interpreter setting to the specified value.
        /// </summary>
        /// <param name="interpreter">New interpreter setting.</param>
        Task SetInterpreterAsync(string interpreter);

        /// <summary>
        /// Add an action to execute when the workspace is diposed.
        /// </summary>
        void AddActionOnClose(object key, Action<object> action);
    }
}
