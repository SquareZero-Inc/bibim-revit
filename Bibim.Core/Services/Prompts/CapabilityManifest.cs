// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
namespace Bibim.Core
{
    /// <summary>
    /// Single source of truth for what BIBIM can NOT do in-session. Injected into BOTH
    /// the planner prompt and the codegen prompt so impossible requests are refused
    /// up-front (with a reason + the closest achievable alternative) instead of burning
    /// a full codegen loop on code that can never work.
    ///
    /// Background: generated code runs inside an already-running Revit session via
    /// IExternalEventHandler + Roslyn. Anything that requires IExternalApplication
    /// startup registration or another add-in's internals is structurally impossible.
    /// (Real-world failure this prevents: a "make me a ribbon button" request consumed
    /// two full codegen rounds / ~220k tokens before the user was told it can't work.)
    /// </summary>
    public static class CapabilityManifest
    {
        /// <summary>Shared list of impossible in-session operations (prompt-ready).</summary>
        private const string ImpossibleList = @"
1. Creating or modifying ribbon tabs / panels / buttons (CreateRibbonTab, RibbonPanel,
   PushButton) — ribbon registration only works in IExternalApplication.OnStartup,
   which requires installing a separate add-in, not runtime code.
2. Registering or loading new add-ins, IExternalApplication / IExternalCommand
   implementations, or external DLLs at runtime.
3. Controlling ANOTHER add-in's UI or commands (e.g. Environment, Dynamo, pyRevit,
   Enscape buttons/tools) — their internals are not exposed through the Revit API.
4. Creating modeless or dockable panels / persistent custom UI.
5. Driving Revit's built-in dialogs or simulating mouse clicks / keyboard input.
6. Anything that must keep running in the background after this execution returns
   (watchers, timers, event subscriptions that outlive the call).";

        /// <summary>
        /// Elements the Revit API cannot create at all (checked against RevitAPI.dll
        /// 2024/2026: no Ramp class or creation method). Not "impossible in-session" —
        /// impossible via the API anywhere — but with a real, achievable alternative, so
        /// the answer is "can't + here is what I can build", not a flat refusal.
        /// (field report 2026-09-17: ramp requests produced uneditable DirectShapes and a
        /// failed StairsEditScope attempt instead of an honest answer.)
        /// </summary>
        private const string RampRule = @"
RAMPS (the Revit API has NO way to create a Ramp-category element — no Ramp class, no
creation method; StairsEditScope builds stairs, not ramps; a DirectShape in the Ramps
category is NOT an editable ramp):
- Never plan or write code that fakes a ramp (DirectShape, mass, in-place family).
- Offer instead: (a) a SLOPED FLOOR built with a slope arrow (editable, native Floor);
  (b) the user draws the ramp with Revit's Ramp tool (BIBIM can open the tool via
  PostableCommand.Ramp, but cannot fill in its sketch).";

        /// <summary>
        /// Planner-side block: route impossible requests to an honest chat refusal,
        /// never to a task.
        /// </summary>
        public const string PlannerBlock = @"
CAPABILITY BOUNDARIES (CRITICAL — check BEFORE anything else):
BIBIM executes generated C# inside the RUNNING Revit session (IExternalEventHandler).
The following are IMPOSSIBLE in-session and MUST NOT become a task:
" + ImpossibleList + @"
If the request requires any of the above:
- Set mode = ""chat"" (NEVER ""task"" — do not plan, do not ask clarifying questions).
- In assistantMessage (in the user's language): state clearly that this specific thing
  cannot be done from a running session, give the reason in ONE short sentence, and
  offer the closest achievable alternative. Useful alternatives: save the generated
  code to the Code Library and re-run it with one click for repeatable actions; or a
  standalone add-in as a feature request to the BIBIM team.
Honesty rule: a fast, clear ""this can't work, here's what can"" is ALWAYS better than
attempting a workaround that appears to run but has no effect.
" + RampRule + @"
Planner handling of ramp requests:
- Ramp only: mode = ""chat"". assistantMessage (user's language) says FIRST that the
  Revit API cannot create ramps, then offers a sloped floor with the SAME dimensions and
  levels the user gave (restate them), and asks whether to build it. No task.
- Ramp + floor/slab in the same request, or the user accepted the sloped-floor offer:
  mode = ""task"" for the sloped floor only; start `summary` with one sentence that the
  Ramp part cannot be created through the API.";

        /// <summary>
        /// Codegen-side block: last line of defence when an impossible task slips
        /// through planning — reply without code instead of writing doomed code.
        /// </summary>
        public const string CodeGenBlock = @"

CAPABILITY BOUNDARIES (CRITICAL):
This code runs inside the CURRENT Revit session via IExternalEventHandler. The
following are structurally impossible — do NOT attempt them or emit code for them:
" + ImpossibleList + @"
If the task requires any of the above, DO NOT return a code block. Reply in plain text
(in the user's language): say it cannot work from a running session, why in one short
sentence, and the closest achievable alternative (e.g. re-running saved code from the
Code Library for one-click repeatable actions). Never write code that compiles and
runs but silently cannot achieve the requested effect.
" + RampRule + @"
SLOPED FLOOR recipe (Revit 2022+):
  Floor.Create(doc, profileLoops, floorTypeId, levelId, false, slopeArrow, slopeAngle)
  - slopeArrow: a HORIZONTAL Line inside the profile at the level's elevation, drawn
    from the LOW end to the HIGH end (tail = low end, sits at the level + base offset).
  - slopeAngle: an ANGLE IN RADIANS, not rise/run: Math.Atan(rise / run).
    Example: 400 mm rise over 5000 mm run → Math.Atan(400.0 / 5000.0).
  - Base offset: set BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM after creation.
  - All lengths in internal feet: UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters).
  - Log the created floor id, rise, run and angle in degrees with ctx.Log.
STAIRS: StairsEditScope.Start must be called OUTSIDE any Transaction (open the
Transaction inside the scope, commit it, then scope.Commit(failuresPreprocessor)).

GRID / LEVEL DISPLAY IN VIEWS — applies to ANY request to copy, apply, match or sync how
grids or levels look in one view to other views (2D/3D extents, length, end or head
position, ""그리드 설정"", ""2D 표시 설정""), whatever words the task summary uses:
  - The ONLY correct call is
    DatumPlane.PropagateToViews(sourceView, ISet<ElementId> targetViewIds)
    (pass a HashSet<ElementId>, not a List). It copies the extent type of each end AND
    the 2D end positions in one call.
  - Valid targets come from datum.GetPropagationViews(sourceView) (parallel views where
    the datum is visible). Intersect the requested views with that set per datum; report
    datums/views that are not in it instead of failing silently.
  - Do NOT copy with SetCurveInView(…, targetView, curveFromSourceView): the source
    view's curve lies at another elevation and throws ""The curve is unbound or not
    coincident with the original one of the datum plane"". (If a curve must be set, take
    the TARGET view's own curve from GetCurvesInView and move only its end points along
    it; check IsCurveValidInView first.)
  - Bubbles are separate: ShowBubbleInView / HideBubbleInView per end.
  - Count a datum as succeeded ONLY if its extents were propagated. Bubble-only success
    is a partial result and must be reported as such.

DIMENSION AUDIT (""난치수"" / odd, non-round distances — dimension only the offending gaps):
  - Distance in mm: UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters).
  - Never test the fraction with `mm - Math.Floor(mm)` or `% 1` — 2345.000 arrives as
    2344.9999999 and every gap gets flagged. Test against the allowed precision p
    (decimals; default 2 → 24.05 is planned, 24.016 is odd, unless the user gave another):
        double r = Math.Round(mm, p);  bool odd = Math.Abs(mm - r) > 0.0005;
  - Pairs: ADJACENT parallel neighbours only. Per direction, sort the references by
    coordinate, cluster references that overlap in the other direction, and compare each
    one with its next neighbour — never all N×N combinations, and one reference per
    element side (not every face of every solid). Only widen the pairing if the user
    explicitly asks for non-adjacent pairs, and then cap it and say so.
  - Works for element–element and grid–element (e.g. column centre to nearest parallel
    grid): Grid → new Reference(grid); walls/columns → face or location-line references.
  - Before creating, ctx.Log: pairs examined, odd pairs found, and the precision used.
    If more than 200 dimensions would be created, create none: return the count and the
    10 worst offenders and ask the user to narrow the selection or confirm.
  - Return: examined / odd / created, the precision, and the smallest and largest odd
    remainder.";
    }
}
