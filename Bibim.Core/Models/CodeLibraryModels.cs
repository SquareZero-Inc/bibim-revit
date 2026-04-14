// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
#if NET48
using Newtonsoft.Json;
#else
using System.Text.Json.Serialization;
#endif

namespace Bibim.Core
{
    /// <summary>
    /// A folder in the code library. Supports flat or nested (via ParentId) organization.
    /// </summary>
    public class CodeFolder
    {
#if NET48
        [JsonProperty("id")]
#else
        [JsonPropertyName("id")]
#endif
        public string Id { get; set; } = Guid.NewGuid().ToString();

#if NET48
        [JsonProperty("name")]
#else
        [JsonPropertyName("name")]
#endif
        public string Name { get; set; }

#if NET48
        [JsonProperty("parentId")]
#else
        [JsonPropertyName("parentId")]
#endif
        public string ParentId { get; set; } // null = root-level folder
    }

    /// <summary>
    /// A saved code snippet in the local code library.
    /// Stored independently from chat sessions so deletion of a session
    /// does not remove the code.
    /// </summary>
    public class CodeSnippet
    {
#if NET48
        [JsonProperty("id")]
#else
        [JsonPropertyName("id")]
#endif
        public string Id { get; set; } = Guid.NewGuid().ToString();

#if NET48
        [JsonProperty("title")]
#else
        [JsonPropertyName("title")]
#endif
        public string Title { get; set; }

#if NET48
        [JsonProperty("summary")]
#else
        [JsonPropertyName("summary")]
#endif
        public string Summary { get; set; }

#if NET48
        [JsonProperty("code")]
#else
        [JsonPropertyName("code")]
#endif
        public string Code { get; set; }

#if NET48
        [JsonProperty("revitVersion")]
#else
        [JsonPropertyName("revitVersion")]
#endif
        public string RevitVersion { get; set; }

#if NET48
        [JsonProperty("taskKind")]
#else
        [JsonPropertyName("taskKind")]
#endif
        public string TaskKind { get; set; }

#if NET48
        [JsonProperty("sourceSessionId")]
#else
        [JsonPropertyName("sourceSessionId")]
#endif
        public string SourceSessionId { get; set; }

#if NET48
        [JsonProperty("folderId")]
#else
        [JsonPropertyName("folderId")]
#endif
        public string FolderId { get; set; } // null = uncategorized (root)

#if NET48
        [JsonProperty("createdAt")]
#else
        [JsonPropertyName("createdAt")]
#endif
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Root container for local code library JSON storage.
    /// </summary>
    public class CodeLibraryStorage
    {
#if NET48
        [JsonProperty("folders")]
#else
        [JsonPropertyName("folders")]
#endif
        public List<CodeFolder> Folders { get; set; } = new List<CodeFolder>();

#if NET48
        [JsonProperty("snippets")]
#else
        [JsonPropertyName("snippets")]
#endif
        public List<CodeSnippet> Snippets { get; set; } = new List<CodeSnippet>();
    }
}
