using System;
using System.Text;
using System.Text.Json.Nodes;

namespace SFSearch
{
    public static class xEditFormatter
    {
        public static string FormatRecord(JsonNode record)
        {
            if (record == null) return "Invalid Record";

            var sb = new StringBuilder();
            
            string signature = record["Signature"]?.ToString() ?? "Unknown";
            string edid = record["EDID - Editor ID"]?.ToString() ?? "Unknown";
            string header = record["Record Header"]?.ToString() ?? $"[{signature}] {edid}";

            sb.AppendLine($"# {header}\n");
            
            // Core Properties Table
            sb.AppendLine("## Core Properties");
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|---|---|");

            foreach (var kvp in record.AsObject())
            {
                if (kvp.Value is JsonArray || kvp.Value is JsonObject) continue;
                if (kvp.Key == "Record Header" || kvp.Key == "Signature" || kvp.Key == "EDID - Editor ID") continue;

                string val = kvp.Value?.ToString() ?? "Null";
                if (val.Length > 100) val = val.Substring(0, 97) + "..."; // safety truncate
                sb.AppendLine($"| **{kvp.Key}** | {val} |");
            }
            
            sb.AppendLine();

            // Arrays and Objects
            foreach (var kvp in record.AsObject())
            {
                if (kvp.Value is JsonArray arr)
                {
                    sb.AppendLine($"## {kvp.Key}");
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj)
                        {
                            sb.AppendLine($"- {FormatInlineObject(obj)}");
                        }
                        else
                        {
                            sb.AppendLine($"- {item?.ToString()}");
                        }
                    }
                    sb.AppendLine();
                }
                else if (kvp.Value is JsonObject obj && kvp.Key != "ObjectBounds")
                {
                    sb.AppendLine($"## {kvp.Key}");
                    sb.AppendLine(FormatInlineObject(obj));
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private static string FormatInlineObject(JsonObject obj)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kvp in obj)
            {
                if (kvp.Value is JsonArray || kvp.Value is JsonObject) continue; // Keep it shallow for xEdit lists
                parts.Add($"**{kvp.Key}:** {kvp.Value?.ToString()}");
            }
            return string.Join(" | ", parts);
        }
    }
}
