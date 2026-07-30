using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Collections;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace compute.geometry
{
    // POST /branch-extract — reads DLT panel + subpanel data directly out of an
    // unbaked Branch (StructureCraft) .3dm by walking each object's geometry-level
    // ArchivableDictionary (obj.Geometry.UserDictionary). This is a standard
    // RhinoCommon dictionary, not a Branch-proprietary structure, so it requires
    // no Branch SDK and no prior bake step. See
    // docs/branch-extract/RhinoCompute_Branch_Extractor_Handoff.md for the
    // validated data structure this endpoint translates into C#.
    public static class BranchExtractModule
    {
        public static void MapEndpoints(IEndpointRouteBuilder app)
        {
            app.MapPost("branch-extract", Extract);
        }

        // Identity/logistics fields, read as raw top-level entries on the
        // geometry dictionary itself (obj.Geometry.UserDictionary), NOT as
        // attribute user-text (attrs.GetUserString). Verified against a live
        // model: these keys sit alongside "Parameters" and "Subpanels" at the
        // top level, and attrs.GetUserString returns null for all of them.
        static readonly string[] TopLevelFields =
        {
            "FabricationPackage", "InstallSequence", "Truck", "Bundle",
            "BundleLevel", "InstanceMark", "Material", "Coating"
        };

        static async Task Extract(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/json";

            JToken body;
            try
            {
                string bodyText = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
                body = JToken.Parse(bodyText);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("{\"error\":\"Request body must be a JSON array of file paths, or {\\\"paths\\\": [<file paths>]}.\"}");
                return;
            }

            string[] paths = body is JArray arr
                ? arr.ToObject<string[]>()
                : body["paths"]?.ToObject<string[]>();

            if (paths == null || paths.Length == 0)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("{\"error\":\"Request body must be a JSON array of file paths, or {\\\"paths\\\": [<file paths>]}.\"}");
                return;
            }

            var panels = new JArray();
            var errors = new JArray();

            foreach (var path in paths)
            {
                try
                {
                    ExtractFile(path, panels);
                }
                catch (Exception ex)
                {
                    errors.Add(new JObject { ["path"] = path, ["message"] = ex.Message });
                }
            }

            var result = new JObject { ["panels"] = panels, ["errors"] = errors };
            await ctx.Response.WriteAsync(result.ToString(Newtonsoft.Json.Formatting.None));
        }

        static void ExtractFile(string path, JArray panels)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("File not found", path);

            RhinoDoc doc = RhinoDoc.OpenHeadless(path);
            if (doc == null)
                throw new InvalidOperationException("Failed to open document headlessly.");

            try
            {
                // A DLT panel object is identified by a "Parameters" key on its
                // geometry dictionary. Multiple objects per panel can share the same
                // identity (lifting points, lam-stack lines, etc.); only the main
                // object carries the rich dictionary, so dedupe on the panel's own
                // "UniqueId" (a Branch-assigned GUID living on the geometry
                // dictionary) rather than the object's own Rhino id, and keep the
                // first match.
                var seenIds = new HashSet<string>();
                foreach (RhinoObject obj in doc.Objects)
                {
                    var gdict = GetGeometryDict(obj.Geometry);
                    if (gdict == null || !gdict.ContainsKey("Parameters"))
                        continue;

                    string dedupeId = gdict.TryGetValue("UniqueId", out object uid) ? uid?.ToString() : null;
                    if (string.IsNullOrEmpty(dedupeId))
                        dedupeId = obj.Id.ToString();
                    if (!seenIds.Add(dedupeId))
                        continue;

                    panels.Add(ExtractPanel(gdict, path, doc));
                }
            }
            finally
            {
                doc.Dispose();
            }
        }

        static ArchivableDictionary GetGeometryDict(GeometryBase geom)
        {
            if (geom == null)
                return null;

            var direct = geom.UserDictionary;
            if (direct != null && direct.Count > 0)
                return direct;

            // Fallback: on some objects the data sits on a raw UserDictionary
            // UserData entry rather than being reachable through the UserDictionary
            // convenience property.
            ArchivableDictionary best = null;
            foreach (Rhino.DocObjects.Custom.UserData ud in geom.UserData)
            {
                if (ud is Rhino.DocObjects.Custom.UserDictionary shared &&
                    shared.Dictionary != null &&
                    (best == null || shared.Dictionary.Count > best.Count))
                {
                    best = shared.Dictionary;
                }
            }
            return best;
        }

        static JObject ExtractPanel(ArchivableDictionary gdict, string sourceFile, RhinoDoc doc)
        {
            var row = new JObject { ["sourceFile"] = sourceFile };

            // Mark/type identity: there is no "Mark" key. The panel's real mark is
            // the concatenation of TypePrefix + TypeSuffix (e.g. "P" + "201" ->
            // "P201"), confirmed against the live model. BranchID has no direct
            // equivalent; UniqueId (a Branch-assigned GUID on the geometry
            // dictionary) is the closest stable per-panel identifier. The old
            // baked pipeline always appended "-{InstanceMark}" (e.g. "P4000-01"),
            // and downstream scripts (half_label() in build_dlt_subpanels.py, plus
            // the Audit List/Comparison Matrix reference join) depend on that
            // suffix being present, so it must be reproduced here.
            string typePrefix = RawString(gdict, "TypePrefix");
            string typeSuffix = RawString(gdict, "TypeSuffix");
            string instanceMark = RawString(gdict, "InstanceMark");
            row["Mark"] = (typePrefix != null || typeSuffix != null)
                ? $"{typePrefix}{typeSuffix}" + (!string.IsNullOrEmpty(instanceMark) ? $"-{instanceMark}" : "")
                : null;
            row["BranchID"] = RawString(gdict, "UniqueId");

            foreach (var field in TopLevelFields)
                row[field] = RawValue(gdict, field);

            // "Material" on the geometry dictionary is a Guid (e.g.
            // "8face9a0-..."), NOT a readable name. Empirically it does NOT resolve
            // against this document's material tables: on a live unbaked model the
            // target GUID is absent from both doc.RenderMaterials and doc.Materials
            // (which only hold "Custom"/"transparent_red"). It's a Branch-internal
            // material-catalog id; the old baked pipeline got a readable name
            // ("DLTLumber - SPF") only because Branch's bake handler resolved it
            // against Branch's own catalog and wrote the string into user text.
            //
            // We still attempt document-table resolution (harmless, and it would
            // work for a normal Rhino material), but if the value is still an
            // unresolved GUID we emit null rather than the raw GUID: downstream
            // (build_dlt_masterlist.split_material) splits Material on "-", which
            // would shred a GUID into garbage columns. A blank "unknown" is
            // correct; garbage is not. The real fix for readable species/grade is
            // a Branch material-catalog GUID->name map supplied out-of-band.
            if (row["Material"] != null && Guid.TryParse(row["Material"].ToString(), out Guid materialId))
            {
                // Preserve the raw catalog GUID as MaterialId BEFORE we try (and
                // usually fail) to resolve a human-readable name. This GUID is the
                // stable join key into Branch's
                // AppResources/Materials/Materials.xml (keyed by <UniqueId>), which
                // carries Species / Grade / Density. Downstream
                // (build_production_forecast.py) uses it to resolve species+grade
                // and a real per-species density for the volume x density weight
                // reconstruction. Nulling the unresolved GUID out of "Material"
                // (below) protects the CSV split logic, but the GUID itself is the
                // most valuable field on this object, so we keep it here.
                row["MaterialId"] = materialId.ToString();

                string materialName = doc?.RenderMaterials?.Find(materialId)?.Name;
                if (string.IsNullOrEmpty(materialName))
                {
                    int materialIndex = doc?.Materials?.Find(materialId, true) ?? -1;
                    if (materialIndex >= 0)
                        materialName = doc.Materials[materialIndex]?.Name;
                }
                row["Material"] = string.IsNullOrEmpty(materialName) ? null : materialName;
            }

            var (width, length) = PanelWidthLength(gdict);
            row["Width"] = width;
            row["Length"] = length;

            if (gdict.TryGetDictionary("Parameters", out var parameters))
            {
                row["LamWidth"] = TryDouble(parameters, "LamWidth");
                row["LamProfileType"] = EnumString(parameters, "LamProfileType");
                row["LamArrangementType"] = EnumString(parameters, "LamArrangementType");
                row["SubpanelDepth"] = TryDouble(parameters, "SubpanelDepth");
                string notation = NotationString(parameters);
                row["LamNotation"] = notation;
                row["Subpanels"] = ExtractSubpanels(gdict, notation);
            }

            return row;
        }

        // Panel footprint isn't stored as flat Width/Length fields anywhere in the
        // dictionary tree; it's derived from LocalBoundingBox, a plane-relative box
        // stored as a nested dictionary with IntervalX/IntervalY/IntervalZ. IntervalZ
        // matches SubpanelDepth (panel thickness), confirming IntervalX is the long
        // dimension (Length) and IntervalY the short dimension (Width).
        static (double? width, double? length) PanelWidthLength(ArchivableDictionary gdict)
        {
            if (!gdict.TryGetDictionary("LocalBoundingBox", out var bbox))
                return (null, null);

            double? length = bbox.TryGetValue("IntervalX", out object ix) && ix is Interval xi
                ? Math.Abs(xi.Length) : (double?)null;
            double? width = bbox.TryGetValue("IntervalY", out object iy) && iy is Interval yi
                ? Math.Abs(yi.Length) : (double?)null;
            return (width, length);
        }

        static string RawString(ArchivableDictionary dict, string key)
        {
            return dict.TryGetValue(key, out object v) ? v?.ToString() : null;
        }

        static JToken RawValue(ArchivableDictionary dict, string key)
        {
            if (!dict.TryGetValue(key, out object v) || v == null)
                return null;
            if (v is int || v is long || v is double || v is float || v is bool)
                return JToken.FromObject(v);
            return v.ToString();
        }

        static JArray ExtractSubpanels(ArchivableDictionary gdict, string notation)
        {
            var subpanels = new JArray();
            if (!gdict.TryGetDictionary("Subpanels", out var subs))
                return subpanels;

            // Each "+"-separated group in the parent's reconstructed notation
            // belongs to one half, in order (validated: "18/14+18/15" -> half A
            // "18/14", half B "18/15"). Sort keys numerically ("0", "1", ...) so
            // that ordering lines up with the notation groups.
            string[] notationGroups = string.IsNullOrEmpty(notation) ? Array.Empty<string>() : notation.Split('+');
            var orderedKeys = subs.Keys.OrderBy(k => int.TryParse(k, out int n) ? n : int.MaxValue).ToList();

            for (int i = 0; i < orderedKeys.Count; i++)
            {
                if (!(subs[orderedKeys[i]] is ArchivableDictionary half))
                    continue;

                subpanels.Add(new JObject
                {
                    ["key"] = orderedKeys[i],
                    ["typeLetter"] = EnumString(half, "TypeLetter"),
                    ["netArea"] = TryDouble(half, "NetArea"),
                    ["volume"] = TryDouble(half, "Volume"),
                    ["lamNotation"] = i < notationGroups.Length ? notationGroups[i] : null
                });
            }

            return subpanels;
        }

        static double? TryDouble(ArchivableDictionary dict, string key)
        {
            return dict.TryGetDouble(key, out double v) ? v : (double?)null;
        }

        static string EnumString(ArchivableDictionary dict, string key)
        {
            return dict.ContainsKey(key) ? dict[key]?.ToString() : null;
        }

        static string NotationString(ArchivableDictionary parameters)
        {
            if (!parameters.ContainsKey("LamStackNotation"))
                return null;

            // Validated shape: a nested ArchivableDictionary with parallel int
            // arrays "Lengths" (group sizes) and "Values" (flattened values), e.g.
            // Lengths=[2,2], Values=[18,10,18,10] -> "18/10+18/10".
            if (parameters.TryGetDictionary("LamStackNotation", out var notation))
            {
                if (notation.TryGetValue("Lengths", out object lengthsObj) &&
                    notation.TryGetValue("Values", out object valuesObj))
                {
                    int[] lengths = ToIntArray(lengthsObj);
                    int[] values = ToIntArray(valuesObj);
                    var groups = new List<string>();
                    int idx = 0;
                    foreach (var len in lengths)
                    {
                        groups.Add(string.Join("/", values.Skip(idx).Take(len)));
                        idx += len;
                    }
                    return string.Join("+", groups);
                }
                return null;
            }

            // Defensive fallback in case a Branch version stores this as a raw
            // jagged int[][] instead of the Lengths/Values sub-dictionary.
            if (parameters["LamStackNotation"] is int[][] jagged)
                return string.Join("+", jagged.Select(g => string.Join("/", g)));

            return null;
        }

        static int[] ToIntArray(object value)
        {
            if (value is IEnumerable<int> ints)
                return ints.ToArray();
            if (value is System.Collections.IEnumerable en)
            {
                var list = new List<int>();
                foreach (var item in en)
                    list.Add(Convert.ToInt32(item));
                return list.ToArray();
            }
            return Array.Empty<int>();
        }
    }
}
