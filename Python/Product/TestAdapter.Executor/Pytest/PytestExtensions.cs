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
using System.IO;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace Microsoft.PythonTools.TestAdapter.Pytest {
    static class PyTestExtensions {

        /// <summary>
        /// Parses the relative source and line number from a PytestTest discovery result
        /// Example Test "source": ".\\test_user_marks.py:17",
        ///     returns  (test_user_marks.py, 17)
        /// </summary>
        /// <param name="test"></param>
        /// <returns></returns>
        public static (string, int) ParseSourceAndLine(this PytestTest test) {
            int line = 0;
            var sourceAndLineNum = test.Source.Replace(".\\", "");
            var sourceParts = sourceAndLineNum.Split(':');
            if (sourceParts.Length != 2 ||
                !Int32.TryParse(sourceParts[1], out line)) {
                throw new FormatException(String.Format("Invalid source: {0}", test.ToString()));
            }

            return (sourceParts[0], line);
        }

        public static TestCase ToVsTestCase(
            this PytestTest test,
            string source,
            int line,
            Dictionary<string, PytestParent> parentMap,
            string projectHome
        ) {
            if (parentMap == null) {
                throw new ArgumentException(nameof(parentMap));
            }
            if (String.IsNullOrWhiteSpace(source)) {
                throw new ArgumentException(nameof(source) + " " + test.ToString());
            }
            if (String.IsNullOrWhiteSpace(projectHome)) {
                throw new ArgumentException(nameof(projectHome));
            }
            if (String.IsNullOrWhiteSpace(test.Name) ||
                String.IsNullOrWhiteSpace(test.Id)) {
                throw new FormatException(test.ToString());
            }

            var pytestId = CreateProperCasedPytestId(source, projectHome, test.Id);
            var fullyQualifiedName = CreateFullyQualifiedTestNameFromId(source, pytestId);
            var tc = new TestCase(fullyQualifiedName, PythonConstants.PytestExecutorUri, source) {
                DisplayName = test.Name,
                LineNumber = line,
                CodeFilePath = source
            };

            tc.SetPropertyValue(Constants.PytestIdProperty, pytestId);
          
            foreach (var marker in test.Markers.MaybeEnumerate()) {
                tc.Traits.Add(new Trait(marker.ToString(), String.Empty));
            }

            return tc;
        }

        /// <summary>
        /// Currently the pytest discovery adapter is lowercasing the file portion the id.
        /// Fix function replaces the file portion with an unmodified verison.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="projectHome"></param>
        /// <param name="pytestId"></param>
        /// <returns></returns>
        internal static string CreateProperCasedPytestId(string source, string projectHome, string pytestId) {
            String[] idParts = pytestId.Replace(".\\", "").Split(new string[] { "::" }, StringSplitOptions.None);
            idParts[0] = PathUtils.CreateFriendlyFilePath(projectHome, source);
            return String.Join("::", idParts);
        }


        /// <summary>
        /// Creates a classname that matches the junit testresult generated one so that we can match testresults with testcases
        /// Note if a function doesn't have a class, its classname appears to be the filename without an extension
        /// </summary>
        /// <param name="t"></param>
        /// <param name="parentMap"></param>
        /// <returns></returns>
        internal static string CreateXmlClassName(PytestTest t, Dictionary<string, PytestParent> parentMap) {
            var parentList = new List<string>();
            var currId = t.Parentid;
            while (parentMap.TryGetValue(currId, out PytestParent parent)) {
                // class names for functions dont append the direct parent 
                if (String.Compare(parent.Kind, "function", StringComparison.OrdinalIgnoreCase) != 0) {
                    parentList.Add(Path.GetFileNameWithoutExtension(parent.Name));
                }
                currId = parent.Parentid;
            }
            parentList.Reverse();

            var xmlClassName = String.Join(".", parentList);
            return xmlClassName;
        }

        internal static string CreateFullyQualifiedTestNameFromId(string source, string pytestId) {
            var fullyQualifiedName = pytestId.Replace(".\\", "");
            String[] parts = fullyQualifiedName.Split(new string[] { "::" }, StringSplitOptions.None);

            // set classname as filename, without extension for test functions outside of classes,
            // so test explorer doesn't use .py as the classname
            if (parts.Length == 2) {
                var className = Path.GetFileNameWithoutExtension(parts[0]);
                return $"{parts[0]}::{className}::{parts[1]}";
            }
            return fullyQualifiedName;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="absoluteFilePath"></param>
        /// <param name="pytestId"></param>
        /// <returns></returns>
        internal static string GetAbsoluteTestExecutionPath(string absoluteFilePath, string pytestId) {
            var filename = Path.GetFileName(absoluteFilePath);
            var executionTestPath = "";
            var index = pytestId.LastIndexOf(filename);
            if (index != -1) {
                //join full codefilepath and pytestId but remove overlapping directories or filename
                var functionName = pytestId.Substring(index + filename.Length);
                executionTestPath = absoluteFilePath + functionName;
            } else {
                executionTestPath = Path.Combine(Path.GetDirectoryName(absoluteFilePath), pytestId.TrimStart('.'));
            }
            return executionTestPath;
        }
    }
}
