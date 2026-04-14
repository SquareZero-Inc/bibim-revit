// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bibim.Core
{
    /// <summary>
    /// Command to show/toggle the BIBIM dockable panel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class BibimShowPanelCommand : IExternalCommand
    {
        private static readonly DockablePaneId PanelId =
            new DockablePaneId(new Guid("B1B1B1B1-B1B1-B1B1-B1B1-B1B1D0C0AB01"));

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var pane = commandData.Application.GetDockablePane(PanelId);
                if (pane == null)
                {
                    message = "BIBIM panel not found.";
                    return Result.Failed;
                }

                if (pane.IsShown())
                    pane.Hide();
                else
                    pane.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.LogError("BibimShowPanelCommand", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
