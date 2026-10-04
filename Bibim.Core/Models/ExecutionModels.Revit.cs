// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Bibim.Core
{
    // Revit-typed members are split out of ExecutionModels.cs so Bibim.Core.Tests
    // can link that file without referencing RevitAPI.dll.

    public partial class ExecutionRequest
    {
        /// <summary>
        /// Selection captured at the start of the preview (dry-run) for this task.
        /// On commit, if the live selection is empty (dry-run rollback commonly clears
        /// it when generated code selected now-rolled-back elements), the handler
        /// restores these ids before invoking the code — selection-based code then
        /// sees the same element set the preview validated.
        /// </summary>
        public IList<ElementId> SelectionSnapshot { get; set; }
    }

    public partial class ExecutionResult
    {
        /// <summary>
        /// Selection as it was immediately BEFORE the generated code ran (dry-run only).
        /// The provider stores this per task and feeds it back as
        /// <see cref="ExecutionRequest.SelectionSnapshot"/> on commit.
        /// </summary>
        public IList<ElementId> SelectionBeforeRun { get; set; }
    }
}
