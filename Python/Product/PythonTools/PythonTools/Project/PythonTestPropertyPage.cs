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
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;

namespace Microsoft.PythonTools.Project {
    [SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
        Justification = "object is owned by VS")]
    [Guid(PythonConstants.TestPropertyPageGuid)]
    public sealed class PythonTestPropertyPage : CommonPropertyPage {
        private readonly PythonTestPropertyPageControl _control;

        public PythonTestPropertyPage() {
            _control = new PythonTestPropertyPageControl(this);
        }

        public override Control Control {
            get { return _control; }
        }

        public override void Apply() {
            _control.SaveSettings();
            IsDirty = false;
        }

        public override void LoadSettings() {
            Loading = true;
            try {
                _control.LoadSettings();
            } finally {
                Loading = false;
            }
        }

        public override string Name {
            get { return Strings.PythonTestPropertyPageLabel; }
        }
    }
}
