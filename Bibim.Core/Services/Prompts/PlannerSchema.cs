// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// JSON schema for the Task Planner response (<see cref="TaskPlanResponse"/>), sent as
    /// structured output (Anthropic output_config.format / OpenAI strict json_schema). The
    /// provider then guarantees parseable JSON with every field present, so the planner's
    /// parse-failure retry only remains for self-hosted servers.
    ///
    /// Shape rules for strict mode on both providers: every object lists all properties in
    /// "required" and sets additionalProperties=false; no numeric/string-length keywords.
    /// Keep in sync with the JSON description inside the planner system prompt.
    /// </summary>
    public static class PlannerSchema
    {
        public const string Name = "task_plan";

        public static JObject Build()
        {
            JObject Str() => new JObject { ["type"] = "string" };
            JObject Enum(params string[] values) => new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values)
            };

            var question = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JArray("text", "selectionType", "options"),
                ["properties"] = new JObject
                {
                    ["text"] = Str(),
                    ["selectionType"] = Enum("single", "multi"),
                    ["options"] = new JObject { ["type"] = "array", ["items"] = Str() }
                }
            };

            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JArray(
                    "mode", "taskKind", "taskCategory", "taskRelation", "title", "summary",
                    "steps", "questions", "assistantMessage", "shouldAutoRun"),
                ["properties"] = new JObject
                {
                    ["mode"] = Enum("chat", "task"),
                    ["taskKind"] = Enum(TaskKinds.Read, TaskKinds.Write),
                    ["taskCategory"] = Enum(TaskCategories.All),
                    ["taskRelation"] = Enum("new", "update", "ask"),
                    ["title"] = Str(),
                    ["summary"] = Str(),
                    ["steps"] = new JObject { ["type"] = "array", ["items"] = Str() },
                    ["questions"] = new JObject { ["type"] = "array", ["items"] = question },
                    ["assistantMessage"] = Str(),
                    ["shouldAutoRun"] = new JObject { ["type"] = "boolean" }
                }
            };
        }
    }
}
