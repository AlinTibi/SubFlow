using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ALMARFELD.SubFlow.Core.Glossary;
using ALMARFELD.SubFlow.Core.Translation;

namespace ALMARFELD.SubFlow.Translation.Llm;

/// <summary>Builds the prompt shared by all chat-model providers (Ollama, OpenAI-compatible).</summary>
public static class LlmPrompt
{
    /// <summary>Bump when the prompt changes materially; it is part of the cache key.</summary>
    public const string Version = "p4";

    public static string System(TranslationBatchRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a professional subtitle translator for film, television and educational video.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Translate every subtitle segment from {r.Source.Name} into {r.Target.Name}.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Reply with JSON only, in this exact shape: {\"translations\":[{\"id\":1,\"text\":\"...\"}]}");
        sb.AppendLine("2. Return exactly one entry for every input id. Never skip, merge, split, reorder or add segments.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"3. Convey meaning, tone and intent naturally, the way a native {r.Target.Name} subtitler would. Avoid word-for-word translation.");
        sb.AppendLine("4. Keep character names, place names and invented terms as they are unless the glossary says otherwise.");
        sb.AppendLine("5. A segment may contain several lines separated by \\n. Translate the content of EVERY line and keep the same number of lines.");
        sb.AppendLine("6. Never add explanations, notes, alternatives, quotation marks or dialogue that is not in the source.");
        sb.AppendLine("7. Names, sound effects or interjections that need no translation may be returned unchanged.");
        sb.AppendLine("8. The \"context\" lines are earlier dialogue for understanding only. Do not translate or return them.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"9. Write correct {r.Target.Name} with its standard script, spelling and diacritics.");
        if (r.Target.IsRightToLeft)
            sb.AppendLine(CultureInfo.InvariantCulture, $"10. {r.Target.Name} is written right-to-left; write natural {r.Target.Name} text without adding direction marks.");

        if (r.Glossary.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Mandatory glossary (always apply):");
            foreach (var e in r.Glossary)
                sb.AppendLine("- " + GlossaryService.Describe(e));
        }
        if (!string.IsNullOrWhiteSpace(r.CorrectionNote))
        {
            sb.AppendLine();
            sb.AppendLine("IMPORTANT correction from the previous attempt: " + r.CorrectionNote);
        }
        return sb.ToString();
    }

    public static string User(TranslationBatchRequest r)
    {
        var root = new JsonObject
        {
            ["source_language"] = r.Source.Name,
            ["target_language"] = r.Target.Name,
        };
        if (r.PrecedingContext.Count > 0)
        {
            var ctx = new JsonArray();
            foreach (var c in r.PrecedingContext)
            {
                var o = new JsonObject { ["source"] = c.Source };
                if (c.Translation is not null) o["translation"] = c.Translation;
                ctx.Add(o);
            }
            root["context"] = ctx;
        }
        var segs = new JsonArray();
        foreach (var s in r.Segments) segs.Add(new JsonObject { ["id"] = s.Id, ["text"] = s.Text });
        root["segments"] = segs;
        return root.ToJsonString(new JsonSerializerOptions
        {
            Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>JSON schema used for structured output where the engine supports it.</summary>
    public static JsonObject ResponseSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["translations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["type"] = "integer" },
                        ["text"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("id", "text"),
                },
            },
        },
        ["required"] = new JsonArray("translations"),
    };
}
