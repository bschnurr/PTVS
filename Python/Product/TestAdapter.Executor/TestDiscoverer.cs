// Python Tools for Visual Studio
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
using System.Linq;
using Microsoft.PythonTools.TestAdapter.Services;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.PythonTools.TestAdapter.Config;
using Microsoft.PythonTools.TestAdapter.Pytest;
using System.Windows.Forms;
using System.Diagnostics;

namespace Microsoft.PythonTools.TestAdapter {
    [FileExtension(".py")]
    [DefaultExecutorUri(PythonConstants.TestExecutorUriString)]
    class TestDiscoverer : ITestDiscoverer {
        
        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, IMessageLogger logger, ITestCaseDiscoverySink discoverySink) {
            if (sources == null) {
                throw new ArgumentNullException(nameof(sources));
            }

            if (discoverySink == null) {
                throw new ArgumentNullException(nameof(discoverySink));
            }

            var settings = discoveryContext.RunSettings;
            
            DiscoverTests(sources, logger, discoverySink, settings);
        }

        static void DiscoverTests(IEnumerable<string> sources, IMessageLogger logger, ITestCaseDiscoverySink discoverySink, IRunSettings settings) {
            MessageBox.Show("Discover: " + Process.GetCurrentProcess().Id);

            var sourceToProjSettings = RunSettingsUtil.GetSourceToProjSettings(settings);

            foreach (var testGroup in sources.GroupBy(x => sourceToProjSettings[x])) {
                DiscoverTestGroup(testGroup, logger, discoverySink);
            }
        }

        static private void DiscoverTestGroup(IGrouping<PythonProjectSettings, string> testGroup, IMessageLogger logger, ITestCaseDiscoverySink discoverySink ) {
            PythonProjectSettings settings = testGroup.Key;
            if(!settings.PytestEnabled) {
                return;
            }

            var discovery = new DiscoveryService();
            var results = discovery.RunDiscovery(settings, testGroup);

            if (results.Count == 0) {
                return;
            }

            PyTestReader.ParseDiscovery(results[0], discoverySink);
        }
    }
}
