﻿
using Microsoft.PythonTools.Infrastructure;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace Microsoft.PythonTools.TestAdapter.Pytest {
    struct PytestParent {

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("parentid")]
        public string Parentid { get; set; }
    }

    struct PytestTest {

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("source")]
        public string Source { get; set; }

        [JsonProperty("markers")]
        public List<object> Markers { get; set; }

        [JsonProperty("parentid")]
        public string Parentid { get; set; }
    }

    struct PytestDiscoveryResults {

        [JsonProperty("rootid")]
        public string Rootid { get; set; }

        [JsonProperty("root")]
        public string Root { get; set; }

        [JsonProperty("parents")]
        public List<PytestParent> Parents { get; set; }

        [JsonProperty("tests")]
        public List<PytestTest> Tests { get; set; }
    }
}