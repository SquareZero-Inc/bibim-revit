// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Bibim.Core
{
    /// <summary>
    /// A folder in the code library. Supports flat or nested (via ParentId) organization.
    /// </summary>
    public class CodeFolder
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("parentId")]
        public string ParentId { get; set; } // null = root-level folder
    }

    /// <summary>
    /// A saved code snippet in the local code library.
    /// Stored independently from chat sessions so deletion of a session
    /// does not remove the code.
    /// </summary>
    public class CodeSnippet
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; }

        [JsonProperty("taskKind")]
        public string TaskKind { get; set; }

        [JsonProperty("sourceSessionId")]
        public string SourceSessionId { get; set; }

        [JsonProperty("folderId")]
        public string FolderId { get; set; } // null = uncategorized (root)

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Root container for local code library JSON storage.
    /// </summary>
    public class CodeLibraryStorage
    {
        [JsonProperty("folders")]
        public List<CodeFolder> Folders { get; set; } = new List<CodeFolder>();

        [JsonProperty("snippets")]
        public List<CodeSnippet> Snippets { get; set; } = new List<CodeSnippet>();
    }
}
