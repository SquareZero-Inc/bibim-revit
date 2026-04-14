// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Local JSON storage for saved code snippets and folders.
    /// File: %APPDATA%/BIBIM/history/code_library.json
    /// Independent from session storage so snippets survive session deletion.
    /// </summary>
    public class CodeLibraryService
    {
        private readonly string _filePath;
        private CodeLibraryStorage _cache;
        private readonly object _lock = new object();

        public CodeLibraryService(string customPath = null)
        {
            var folder = customPath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BIBIM", "history");
            _filePath = Path.Combine(folder, "code_library.json");
        }

        // --- Snippet operations ---

        public void Save(CodeSnippet snippet)
        {
            if (snippet == null) return;
            lock (_lock)
            {
                var storage = Load();
                storage.Snippets.Add(snippet);
                Write(storage);
            }
        }

        public List<CodeSnippet> GetAll()
        {
            lock (_lock)
            {
                return Load().Snippets
                    .OrderByDescending(s => s.CreatedAt)
                    .ToList();
            }
        }

        public CodeSnippet GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                return Load().Snippets.FirstOrDefault(s => s.Id == id);
            }
        }

        public void Delete(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var storage = Load();
                storage.Snippets.RemoveAll(s => s.Id == id);
                Write(storage);
            }
        }

        public void RenameSnippet(string id, string newTitle)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var storage = Load();
                var snippet = storage.Snippets.FirstOrDefault(s => s.Id == id);
                if (snippet == null) return;
                snippet.Title = newTitle ?? "";
                Write(storage);
            }
        }

        public void MoveSnippet(string snippetId, string folderId)
        {
            if (string.IsNullOrEmpty(snippetId)) return;
            lock (_lock)
            {
                var storage = Load();
                var snippet = storage.Snippets.FirstOrDefault(s => s.Id == snippetId);
                if (snippet == null) return;
                // Validate folderId exists (null = root is always valid)
                if (folderId != null && !storage.Folders.Any(f => f.Id == folderId))
                    folderId = null;
                snippet.FolderId = folderId;
                Write(storage);
            }
        }

        // --- Folder operations ---

        public List<CodeFolder> GetFolders()
        {
            lock (_lock)
            {
                return Load().Folders.ToList();
            }
        }

        public CodeFolder CreateFolder(string name, string parentId = null)
        {
            lock (_lock)
            {
                var storage = Load();
                var folder = new CodeFolder
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name ?? "New Folder",
                    ParentId = parentId
                };
                storage.Folders.Add(folder);
                Write(storage);
                return folder;
            }
        }

        public void RenameFolder(string id, string newName)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var storage = Load();
                var folder = storage.Folders.FirstOrDefault(f => f.Id == id);
                if (folder == null) return;
                folder.Name = newName ?? "";
                Write(storage);
            }
        }

        public void DeleteFolder(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_lock)
            {
                var storage = Load();
                var folder = storage.Folders.FirstOrDefault(f => f.Id == id);
                if (folder == null) return;

                // Move snippets that were in this folder to the parent folder (or root)
                foreach (var snippet in storage.Snippets.Where(s => s.FolderId == id))
                    snippet.FolderId = folder.ParentId; // null if root-level folder

                // Also remove any child folders (move their snippets up too)
                var childIds = storage.Folders.Where(f => f.ParentId == id).Select(f => f.Id).ToList();
                foreach (var childId in childIds)
                {
                    foreach (var snippet in storage.Snippets.Where(s => s.FolderId == childId))
                        snippet.FolderId = folder.ParentId;
                    storage.Folders.RemoveAll(f => f.Id == childId);
                }

                storage.Folders.RemoveAll(f => f.Id == id);
                Write(storage);
            }
        }

        private CodeLibraryStorage Load()
        {
            if (_cache != null) return _cache;
            if (!File.Exists(_filePath))
            {
                _cache = new CodeLibraryStorage();
                return _cache;
            }
            try
            {
                _cache = JsonHelper.Deserialize<CodeLibraryStorage>(
                    File.ReadAllText(_filePath)) ?? new CodeLibraryStorage();
                // Ensure lists are non-null for older files that predate folders
                if (_cache.Folders == null) _cache.Folders = new List<CodeFolder>();
            }
            catch
            {
                _cache = new CodeLibraryStorage();
            }
            return _cache;
        }

        private void Write(CodeLibraryStorage storage)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, JsonHelper.Serialize(storage, indented: true));
                _cache = storage;
            }
            catch (Exception ex)
            {
                Logger.Log("CodeLibrary", $"Write failed: {ex.Message}");
            }
        }
    }
}
