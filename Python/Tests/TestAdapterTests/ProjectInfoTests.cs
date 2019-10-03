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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.PythonTools.Projects;
using Microsoft.PythonTools.TestAdapter;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using TestAdapterTests.Mocks;

namespace TestAdapterTests {
    [TestClass]
    public class ProjectInfoTests {
        /// <summary>
        /// Recreate collection was modified while iterating exception
        /// Test shouldn't throw
        /// </summary>
        /// <returns></returns>
        [TestMethod, Priority(0)]
        public async Task TestBuildingAndClearingProjectMapConcurrently() {
            ITestContainerDiscoverer dummyDiscoverer = null;
            var projectMap = new ConcurrentDictionary<string, ProjectInfo>();
            var projectName = "dummyName";
            PythonProject dumpyProject = new MockPythonProject("dummyHome", projectName);

            projectMap[projectName] = new ProjectInfo(dumpyProject);

            var rebuildTasks = Enumerable.Range(1, 100)
                .Select(i => Task.Run(async
                    () => {
                        projectMap.Clear();
                        var project = new ProjectInfo(dumpyProject);
                        foreach (int j in Enumerable.Range(1, 10000)) {
                            project.AddTestContainer(dummyDiscoverer, j.ToString() + ".py");
                        }
                        projectMap[projectName] = project;
                        await Task.Delay(100);
                    }
                )
            );

            var iterateTasks = Enumerable.Range(1, 1000)
                .Select(i => Task.Run(async
                    () => {
                        var items = projectMap.Values.SelectMany(p => p.GetAllContainers()).ToList();
                        await Task.Delay(10);
                    }
                )
            );

            await Task.WhenAll(rebuildTasks.Concat(iterateTasks));
        }
    }
}
