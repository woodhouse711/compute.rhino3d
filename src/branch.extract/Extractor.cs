using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using br.om.Materials;
using br.om.Objects;
using Rhino;
using Rhino.Geometry;
using sc.om.Document;

namespace BranchExtract;

using ComplexityRecord = ComplexityDescriptors;
using ElementRecord = Element;
using ExtractionDocument = ExtractionRecord;
using LodRecord = LodInfo;
using PanelRecord = Panel;
using SummaryRecord = Summary;

internal static class Extractor
{
    private static readonly HashSet<string> ReferenceTypes = new(
        new[]
        {
            "Dap1DReference",
            "Dap2DReference",
            "LineSupportReference"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> SpeciesDisplay = new(
        new[]
        {
            new KeyValuePair<string, string>("DF", "Douglas Fir"),
            new KeyValuePair<string, string>("Dfir", "Douglas Fir"),
            new KeyValuePair<string, string>("DouglasFir", "Douglas Fir"),
            new KeyValuePair<string, string>("SPF", "SPF"),
            new KeyValuePair<string, string>("HemFir", "Hem-Fir"),
            new KeyValuePair<string, string>("SYP", "Southern Yellow Pine"),
            new KeyValuePair<string, string>("AYC", "AYC"),
            new KeyValuePair<string, string>("Glulam", "Glulam"),
            new KeyValuePair<string, string>("WW", "Western White Pine")
        },
        StringComparer.OrdinalIgnoreCase);

    internal static ExtractionDocument Extract(
        RhinoDoc rhinoDocument,
        CommandLineOptions options,
        RunCoordinator coordinator,
        string lastDocumentWarning)
    {
        ExtractionDocument output = ExtractionDocumentFactory.CreateBase(
            options,
            BranchRuntimeInfo.BranchDirectory);
        output.Source.RhinoVersion = RhinoApp.Version.ToString();

        IssueTracker issues = new();
        if (!string.IsNullOrWhiteSpace(lastDocumentWarning))
            issues.Invalid("bootstrap", lastDocumentWarning);

        // RhinoDoc.OpenHeadless establishes this process-global singleton.
        // Capture it before Branch's event sequence, matching the proven
        // reference harness and preventing a later read from observing a
        // different document.
        BranchDoc branchDocument = BranchDoc.ActiveDocument;
        if (branchDocument == null)
        {
            output.Extraction.Status = "needs_upgrade";
            output.Extraction.StatusDetail =
                "BranchDoc.ActiveDocument remained null after materialization; the model may require an interactive Branch upgrade.";
            output.Summary = SummaryBuilder.Build(output.Panels);
            return output;
        }

        coordinator.MarkBranchDocumentLoaded();
        Materialize(rhinoDocument, issues);

        output.Source.BranchProjectGuid = branchDocument.UniqueId == Guid.Empty
            ? null
            : branchDocument.UniqueId.ToString();
        ApplyBranchProjectMetadata(output, branchDocument);

        ObjectCensus census = WalkObjects(branchDocument, issues);
        output.Extraction.Blocks = new BlockRecord
        {
            DefinitionCount = census.BlockDefinitionCount,
            ObjectsInBlocks = SortCounts(census.ObjectsInBlocks),
            OverlapWithTopLevel = SortCounts(census.OverlapWithTopLevel)
        };
        output.Extraction.Excluded.ReferenceTypes = census.ReferenceTypeCount;

        output.Extraction.TypesRecognised = SortCounts(
            census.Objects
                .Where(item => CanModelType(item.Instance.GetType()))
                .GroupBy(item => item.TypeName)
                .ToDictionary(group => group.Key, group => group.Count()));
        output.Extraction.TypesUnrecognised = SortCounts(
            census.Objects
                .Where(item => !CanModelType(item.Instance.GetType()))
                .GroupBy(item => item.TypeName)
                .ToDictionary(group => group.Key, group => group.Count()));

        UnitConversions units;
        try
        {
            units = new UnitConversions(rhinoDocument.ModelUnitSystem);
        }
        catch (Exception ex)
        {
            issues.Invalid(
                "units",
                $"Could not establish model-unit conversion factors: {ex.Message}");
            units = UnitConversions.Invalid;
        }

        Dictionary<Guid, int> planarCutsByHost = CountPlanarCutsByHost(
            census.Objects,
            issues);
        HashSet<Guid> fastenerDapIds = new();
        List<PanelExtraction> panelExtractions = new();

        foreach (LocatedObject located in census.Objects.Where(
                     item => item.Instance is DLT))
        {
            if (located.Instance is not DLT dlt)
            {
                issues.Truncated(
                    "panels",
                    1,
                    $"Runtime object named DLT was not assignable to {typeof(DLT).FullName}.");
                continue;
            }

            try
            {
                PanelExtraction panelExtraction = ExtractPanel(
                    dlt,
                    located,
                    units,
                    rhinoDocument.ModelAbsoluteTolerance,
                    planarCutsByHost,
                    issues);
                panelExtractions.Add(panelExtraction);
                output.Panels.Add(panelExtraction.Panel);
                fastenerDapIds.UnionWith(panelExtraction.FastenerDapIds);
            }
            catch (Exception ex)
            {
                issues.Truncated(
                    $"panel:{located.Id}",
                    1,
                    Unwrap(ex).ToString());
            }
        }

        NormalizeInstallSequences(panelExtractions);

        foreach (LocatedObject located in census.Objects.Where(
                     item => item.Instance is not DLT))
        {
            try
            {
                output.Elements.Add(ExtractElement(
                    located,
                    fastenerDapIds,
                    issues));
            }
            catch (Exception ex)
            {
                issues.Truncated(
                    $"element:{located.TypeName}:{located.Id}",
                    1,
                    Unwrap(ex).ToString());
            }
        }

        output.Lod = output.Panels.Count == 0
            ? null
            : LodBuilder.Build(
                output.Panels,
                new LodSignals(
                    census.Objects.Any(item =>
                        ImplementsInterface(item.Instance.GetType(), "IDap")),
                    census.Objects.Any(item =>
                        ImplementsInterface(item.Instance.GetType(), "IFastener")),
                    census.Objects.Any(item =>
                        IsConnectionObject(item.Instance.GetType()))));
        output.Summary = SummaryBuilder.Build(output.Panels);
        SetOutcome(output, census, issues);
        return output;
    }

    private static void Materialize(RhinoDoc document, IssueTracker issues)
    {
        try
        {
            new br.common.viewHandlers.Events.LoadExternalResources().Execute();
        }
        catch (Exception ex)
        {
            issues.Truncated(
                "LoadExternalResources",
                1,
                Unwrap(ex).ToString());
        }

        Exception blockException = ExecutePluginEvent(
            "br.desktop.Events.Custom.DeserializeBlockDefinitions");
        if (blockException != null)
        {
            issues.Truncated(
                "DeserializeBlockDefinitions",
                1,
                blockException.ToString());
        }

        try
        {
            new br.common.viewHandlers.Events.DeserializeBranchObjects(document)
                .Execute();
        }
        catch (Exception ex)
        {
            issues.Truncated(
                "DeserializeBranchObjects",
                1,
                Unwrap(ex).ToString());
        }

        try
        {
            var queue = br.common.viewHandlers.Events.EventQueue.Queue;
            int guard = 0;
            while (!queue.AtEndOfQueue && guard++ < 200)
                queue.CycleQueue(out _);

            if (!queue.AtEndOfQueue)
            {
                issues.Truncated(
                    "DrainEventQueue",
                    1,
                    "Event queue did not drain within 200 cycles.");
            }
        }
        catch (Exception ex)
        {
            issues.Truncated(
                "DrainEventQueue",
                1,
                Unwrap(ex).ToString());
        }
    }

    private static Exception ExecutePluginEvent(string fullTypeName)
    {
        try
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate =>
                    candidate.GetName().Name == "Branch");
            Type type = assembly?.GetType(fullTypeName);
            if (type == null)
                return new TypeLoadException($"{fullTypeName} was not found.");

            object instance = Activator.CreateInstance(type);
            MethodInfo execute = type.GetMethod(
                "Execute",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (instance == null || execute == null)
                return new MissingMethodException(fullTypeName, "Execute");

            execute.Invoke(instance, null);
            return null;
        }
        catch (Exception ex)
        {
            return Unwrap(ex);
        }
    }

    private static ObjectCensus WalkObjects(
        BranchDoc document,
        IssueTracker issues)
    {
        ObjectCensus census = new();
        List<ComponentBase> topLevel = null;
        try
        {
            topLevel = document.Objects.GetAll();
            if (topLevel == null)
                throw new InvalidOperationException("Objects.GetAll() returned null.");
        }
        catch (Exception ex)
        {
            issues.Truncated(
                "Objects.GetAll",
                EstimateCount(document.Objects),
                Unwrap(ex).ToString());
        }

        if (topLevel != null)
        {
            foreach (ComponentBase item in topLevel)
            {
                try
                {
                    census.Register(item, false, Guid.Empty, issues);
                }
                catch (Exception ex)
                {
                    issues.Truncated(
                        "top_level",
                        1,
                        Unwrap(ex).ToString());
                }
            }
        }

        List<BlockDefinition> blocks = null;
        try
        {
            blocks = document.BlockDefinitions.GetAll();
            if (blocks == null)
                throw new InvalidOperationException(
                    "BlockDefinitions.GetAll() returned null.");
            census.BlockDefinitionCount = blocks.Count;
        }
        catch (Exception ex)
        {
            census.BlockDefinitionCount = EstimateCount(document.BlockDefinitions);
            issues.Truncated(
                "BlockDefinitions.GetAll",
                Math.Max(1, census.BlockDefinitionCount),
                Unwrap(ex).ToString());
        }

        if (blocks != null)
        {
            foreach (BlockDefinition block in blocks)
            {
                List<ComponentBase> members;
                try
                {
                    members = block.GetMembers()?.ToList();
                    if (members == null)
                        throw new InvalidOperationException(
                            $"Block {block.UniqueId} GetMembers() returned null.");
                }
                catch (Exception ex)
                {
                    issues.Truncated(
                        $"block:{SafeBlockId(block)}",
                        EstimateBlockMemberCount(block),
                        Unwrap(ex).ToString());
                    continue;
                }

                foreach (ComponentBase member in members)
                {
                    try
                    {
                        census.Register(
                            member,
                            true,
                            block.UniqueId,
                            issues);
                    }
                    catch (Exception ex)
                    {
                        issues.Truncated(
                            $"block:{SafeBlockId(block)}:members",
                            1,
                            Unwrap(ex).ToString());
                    }
                }
            }
        }

        census.FinalizeCounts();
        return census;
    }

    private static int EstimateCount(object database)
    {
        try
        {
            object value = database?.GetType()
                .GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(database);
            return value is int count ? Math.Max(1, count) : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static int EstimateBlockMemberCount(BlockDefinition block)
    {
        try
        {
            return Math.Max(1, block?.Members?.Count ?? 0);
        }
        catch
        {
            return 1;
        }
    }

    private static string SafeBlockId(BlockDefinition block)
    {
        try
        {
            return block?.UniqueId.ToString() ?? "<null>";
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static Dictionary<Guid, int> CountPlanarCutsByHost(
        IEnumerable<LocatedObject> objects,
        IssueTracker issues)
    {
        Dictionary<Guid, int> counts = new();
        foreach (LocatedObject item in objects.Where(
                     candidate => candidate.Instance is PlanarCut))
        {
            if (!TryReadGuidProperty(
                    item.Instance,
                    "HostId",
                    out Guid hostId,
                    out string error))
            {
                issues.Invalid(
                    $"PlanarCut:{item.Id}:HostId",
                    error);
                continue;
            }

            if (hostId == Guid.Empty)
                continue;
            counts[hostId] = counts.TryGetValue(hostId, out int count)
                ? count + 1
                : 1;
        }
        return counts;
    }

    private static PanelExtraction ExtractPanel(
        DLT dlt,
        LocatedObject located,
        UnitConversions units,
        double modelTolerance,
        IReadOnlyDictionary<Guid, int> planarCutsByHost,
        IssueTracker issues)
    {
        PanelRecord panel = new()
        {
            BranchId = located.Id.ToString(),
            InBlock = located.InBlock,
            BlockId = located.InBlock ? located.BlockId.ToString() : null,
            Lam = new LamRecord(),
            Logistics = new LogisticsRecord(),
            Counts = new CountRecord(),
            Complexity = new ComplexityRecord()
        };

        if (!located.HasValidId)
        {
            AddInvalid(
                panel,
                issues,
                "branch_id",
                "UniqueId was Guid.Empty.");
        }

        panel.Mark = ReadRequiredString(
            panel,
            issues,
            "mark",
            () => dlt.Mark,
            "DLT.Mark is null or empty; numbering was not applied.");
        panel.InstanceMark = ReadRequiredString(
            panel,
            issues,
            "instance_mark",
            () => dlt.InstanceMark,
            "DLT.InstanceMark is null or empty; numbering was not applied.");

        double? weightRaw = ReadFinite(
            panel,
            issues,
            "weight_kg",
            () => dlt.GetWeight(),
            1.0,
            allowZero: false);
        panel.RawWeightKg = weightRaw;
        panel.WeightKg = Round(weightRaw, 2);

        // Decompose the composite. Per the plugin author, DLT.GetWeight() is the sum of
        // subpanel weights plus sheathing, and cutouts are already deducted. Reporting only
        // the total made the number unexplainable: a reference model's 214,607.79 kg looked
        // 15% adrift from a 186,045 kg volume-x-density figure until the 28,563 kg of
        // sheathing was named. Splitting it here means nobody has to rediscover that.
        double subpanelWeightRaw = 0.0;
        bool subpanelWeightComplete = true;
        try
        {
            DLTSubpanel[] subpanels = dlt.GetSubpanels();
            if (subpanels == null || subpanels.Length == 0)
            {
                subpanelWeightComplete = false;
            }
            else
            {
                foreach (DLTSubpanel subpanel in subpanels)
                {
                    double w = subpanel.GetWeight();
                    if (double.IsFinite(w))
                        subpanelWeightRaw += w;
                    else
                        subpanelWeightComplete = false;
                }
            }
        }
        catch (Exception ex)
        {
            subpanelWeightComplete = false;
            AddPartial(
                panel,
                issues,
                "weight_subpanels_kg",
                $"DLTSubpanel.GetWeight() {Describe(ex)}.");
        }

        if (subpanelWeightComplete)
        {
            panel.WeightSubpanelsKg = Round(subpanelWeightRaw, 2);
            if (weightRaw.HasValue)
                panel.WeightSheathingKg = Round(weightRaw.Value - subpanelWeightRaw, 2);
        }

        // Read the sheathing DIRECTLY rather than deriving its weight as (total - subpanels).
        // DLTSheathing is IVolumetric and IWeighable, so it has its own Volume, GetWeight()
        // and Material. Reading both halves independently turns the composition into
        // something verifiable: subpanels + sheathing should RECONCILE with GetWeight()
        // instead of being assumed to.
        double sheathingWeightRaw = 0.0, sheathingVolumeRaw = 0.0;
        bool sheathingComplete = true, anySheathing = false;
        try
        {
            foreach (DLTSheathing sheathing in new[] { dlt.SheathingTop, dlt.SheathingBottom })
            {
                if (sheathing == null)
                    continue;
                anySheathing = true;

                double w = sheathing.GetWeight();
                if (double.IsFinite(w)) sheathingWeightRaw += w; else sheathingComplete = false;

                double v = sheathing.Volume;
                if (double.IsFinite(v)) sheathingVolumeRaw += v; else sheathingComplete = false;

                if (panel.SheathingMaterialName == null)
                {
                    string name = sheathing.Material?.Name;
                    panel.SheathingMaterialName =
                        string.IsNullOrWhiteSpace(name)
                        || name == BranchMaterial.UNASSIGNEDMATERIALNAME
                            ? null
                            : name;
                }
            }
            panel.SheathingPresent = anySheathing;
        }
        catch (Exception ex)
        {
            sheathingComplete = false;
            AddPartial(
                panel,
                issues,
                "sheathing_present",
                $"DLT.SheathingTop/SheathingBottom {Describe(ex)}.");
        }

        if (anySheathing && sheathingComplete)
        {
            panel.WeightSheathingKg = Round(sheathingWeightRaw, 2);
            panel.VolumeSheathingM3 = Round(sheathingVolumeRaw * units.VolumeToM3, 3);
            panel.DensitySheathingKgPerM3 =
                sheathingVolumeRaw > 0.0
                    ? Round(sheathingWeightRaw / (sheathingVolumeRaw * units.VolumeToM3), 1)
                    : null;
        }
        else if (!anySheathing)
        {
            panel.WeightSheathingKg = 0.0;
            panel.VolumeSheathingM3 = 0.0;
        }

        // Reconciliation: the parts should add up to the whole. This is the only independent
        // check we have on GetWeight(), which otherwise has to be taken on trust.
        if (weightRaw.HasValue
            && panel.WeightSubpanelsKg.HasValue
            && panel.WeightSheathingKg.HasValue)
        {
            double parts = panel.WeightSubpanelsKg.Value + panel.WeightSheathingKg.Value;
            if (Math.Abs(parts - weightRaw.Value) > 0.5)
            {
                AddPartial(
                    panel,
                    issues,
                    "weight_kg",
                    $"Parts do not reconcile with the whole: subpanels "
                    + $"{panel.WeightSubpanelsKg.Value.ToString("0.00", CultureInfo.InvariantCulture)} + sheathing "
                    + $"{panel.WeightSheathingKg.Value.ToString("0.00", CultureInfo.InvariantCulture)} = "
                    + $"{parts.ToString("0.00", CultureInfo.InvariantCulture)} against GetWeight() "
                    + $"{weightRaw.Value.ToString("0.00", CultureInfo.InvariantCulture)} kg.");
            }
        }

        double? volumeRaw = ReadFinite(
            panel,
            issues,
            "volume_m3",
            () => dlt.Volume,
            units.VolumeToM3,
            allowZero: false);
        panel.RawVolumeM3 = volumeRaw;
        panel.VolumeM3 = Round(volumeRaw, 3);

        // Total material actually present. A total VOLUME is a real physical quantity, so it
        // is reported. A total DENSITY deliberately is NOT: averaging timber against plywood
        // describes no material anyone would act on.
        if (volumeRaw.HasValue && panel.VolumeSheathingM3.HasValue)
        {
            // volumeRaw is ALREADY in m3 - ReadFinite applied units.VolumeToM3 when it read
            // DLT.Volume. Scaling again here silently reduced the subpanel term to ~0 and made
            // the total equal the sheathing alone (43.94 where 457.37 was correct). Only the
            // sheathing figure, read straight off DLTSheathing.Volume, needs converting.
            panel.VolumeTotalM3 = Round(
                volumeRaw.Value + panel.VolumeSheathingM3.Value,
                3);
        }


        double? grossAreaRaw = ReadFinite(
            panel,
            issues,
            "gross_area_sqft",
            () => dlt.GrossArea,
            units.AreaToSquareFeet,
            allowZero: true);
        panel.GrossAreaSqft = Round(grossAreaRaw, 2);

        double? nativeNetAreaRaw = ReadFinite(
            panel,
            issues,
            "net_area_sqft_native",
            () => dlt.NetArea,
            units.AreaToSquareFeet,
            allowZero: true);
        panel.NetAreaSqftNative = Round(nativeNetAreaRaw, 2);

        double? roughAreaRaw = ReadFinite(
            panel,
            issues,
            "rough_area_sqft",
            () => dlt.RoughArea,
            units.AreaToSquareFeet,
            allowZero: true);
        panel.RoughAreaSqft = Round(roughAreaRaw, 2);

        double? subpanelNetAreaRaw = ExtractSubpanels(
            dlt,
            panel,
            units,
            issues);
        panel.RawNetAreaSqft = subpanelNetAreaRaw;
        // 1 decimal place, deliberately: this is the bit-identical regression invariant
        // against the schema-0.3 baseline (plan §6.7). Rounding to 2dp shifted 128 of 138
        // reference-model panels by 0.01-0.04 sqft and broke the only oracle that distinguishes a
        // changed geometry read from a formatting change. Do not "improve" the precision.
        panel.NetAreaSqft = Round(subpanelNetAreaRaw, 1);

        // Compare the RAW values, not the rounded ones: the two fields are deliberately
        // rounded to different precisions, so a rounded comparison would report a
        // disagreement on essentially every panel.
        if (subpanelNetAreaRaw.HasValue
            && nativeNetAreaRaw.HasValue
            && Math.Abs(subpanelNetAreaRaw.Value - nativeNetAreaRaw.Value) > 0.05)
        {
            AddPartial(
                panel,
                issues,
                "net_area_sqft_native",
                $"DLT.NetArea ({panel.NetAreaSqftNative.Value.ToString(CultureInfo.InvariantCulture)} sqft) disagrees after rounding with the raw sum of subpanel.NetArea ({panel.NetAreaSqft.Value.ToString(CultureInfo.InvariantCulture)} sqft).");
        }

        panel.LengthMm = Round(ReadFinite(
            panel,
            issues,
            "length_mm",
            () => dlt.Length,
            units.LengthToMillimetres,
            allowZero: false), 2);
        panel.WidthMm = Round(ReadFinite(
            panel,
            issues,
            "width_mm",
            () => dlt.Width,
            units.LengthToMillimetres,
            allowZero: false), 2);
        panel.DepthMm = Round(ReadFinite(
            panel,
            issues,
            "depth_mm",
            () => dlt.DepthOfSubpanel,
            units.LengthToMillimetres,
            allowZero: false), 2);

        ExtractMaterial(
            dlt,
            panel,
            weightRaw,
            volumeRaw,
            issues);
        ExtractLamination(dlt, panel, units, issues);
        decimal? rawInstallSequence = ExtractLogistics(
            dlt,
            panel,
            issues);

        HashSet<Guid> fastenerDapIds = ExtractCounts(
            dlt,
            panel,
            located.Id,
            planarCutsByHost,
            issues);
        ExtractComplexity(
            dlt,
            panel,
            units,
            modelTolerance,
            grossAreaRaw,
            subpanelNetAreaRaw,
            issues);

        return new PanelExtraction(
            panel,
            fastenerDapIds,
            rawInstallSequence);
    }

    private static double? ExtractSubpanels(
        DLT dlt,
        PanelRecord panel,
        UnitConversions units,
        IssueTracker issues)
    {
        DLTSubpanel[] subpanels;
        try
        {
            subpanels = dlt.GetSubpanels();
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "net_area_sqft",
                $"DLT.GetSubpanels() threw {Describe(ex)}.");
            return null;
        }

        if (subpanels == null || subpanels.Length == 0)
        {
            AddInvalid(
                panel,
                issues,
                "net_area_sqft",
                "DLT.GetSubpanels() returned no subpanels; raw subpanel NetArea cannot be summed.");
            return null;
        }

        bool everyAreaValid = true;
        double rawAreaSum = 0.0;
        for (int index = 0; index < subpanels.Length; index++)
        {
            DLTSubpanel subpanel = subpanels[index];
            SubpanelRecord record = new();
            panel.Subpanels.Add(record);
            if (subpanel == null)
            {
                AddInvalid(
                    panel,
                    issues,
                    $"subpanels[{index}]",
                    "DLT.GetSubpanels() contained a null entry.");
                everyAreaValid = false;
                continue;
            }

            try
            {
                record.TypeLetter = Clean(subpanel.TypeLetter);
                if (record.TypeLetter == null)
                {
                    AddPartial(
                        panel,
                        issues,
                        $"subpanels[{index}].type_letter",
                        "DLTSubpanel.TypeLetter is null or empty.");
                }
            }
            catch (Exception ex)
            {
                AddInvalid(
                    panel,
                    issues,
                    $"subpanels[{index}].type_letter",
                    $"DLTSubpanel.TypeLetter threw {Describe(ex)}.");
            }

            double? area = ReadFinite(
                panel,
                issues,
                $"subpanels[{index}].net_area_sqft",
                () => subpanel.NetArea,
                units.AreaToSquareFeet,
                allowZero: true);
            // 1dp to match the schema-0.3 baseline (plan §6.7). The panel total is summed
            // from `area` (raw) below, so this rounding is presentation only.
            record.NetAreaSqft = Round(area, 1);
            if (area.HasValue)
                rawAreaSum += area.Value;
            else
                everyAreaValid = false;

            double? volume = ReadFinite(
                panel,
                issues,
                $"subpanels[{index}].volume_m3",
                () => subpanel.Volume,
                units.VolumeToM3,
                allowZero: true);
            record.VolumeM3 = Round(volume, 3);
        }

        if (!everyAreaValid)
        {
            if (!panel.FieldErrors.ContainsKey("net_area_sqft"))
            {
                AddInvalid(
                    panel,
                    issues,
                    "net_area_sqft",
                    "At least one DLTSubpanel.NetArea was invalid, so the raw sum is unavailable.");
            }
            return null;
        }

        return rawAreaSum;
    }

    private static void ExtractMaterial(
        DLT dlt,
        PanelRecord panel,
        double? weightRaw,
        double? volumeRaw,
        IssueTracker issues)
    {
        BranchMaterial material;
        try
        {
            material = dlt.Material;
        }
        catch (Exception ex)
        {
            SetUnavailableMaterial(
                panel,
                issues,
                $"DLT.Material threw {Describe(ex)}.",
                invalid: true);
            SetSubpanelDensity(panel, issues, volumeRaw);
            return;
        }

        if (material == null)
        {
            SetUnavailableMaterial(
                panel,
                issues,
                "DLT.Material returned null.",
                invalid: false);
            SetSubpanelDensity(panel, issues, volumeRaw);
            return;
        }

        string materialName;
        try
        {
            materialName = Clean(material.Name);
        }
        catch (Exception ex)
        {
            SetUnavailableMaterial(
                panel,
                issues,
                $"BranchMaterial.Name threw {Describe(ex)}.",
                invalid: true);
            SetSubpanelDensity(panel, issues, volumeRaw);
            return;
        }

        bool unassigned = ReferenceEquals(material, BranchMaterial.Unset)
            || string.Equals(
                materialName,
                BranchMaterial.UNASSIGNEDMATERIALNAME,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                materialName,
                "Unassigned",
                StringComparison.OrdinalIgnoreCase);

        if (unassigned)
        {
            issues.UnassignedMaterialPanels++;
            panel.Species = null;
            panel.Grade = null;
            panel.MaterialName = null;
            panel.MaterialId = null;
            panel.DensityAssignedKgPerM3 = null;

            AddPartial(
                panel,
                issues,
                "species",
                "BranchMaterial.Name == 'Unassigned' (Species=Unset).");
            AddPartial(
                panel,
                issues,
                "grade",
                "BranchMaterial.Name == 'Unassigned'.");
            AddPartial(
                panel,
                issues,
                "material_name",
                "BranchMaterial.Name == 'Unassigned' — sentinel, not a material.");
            AddPartial(
                panel,
                issues,
                "material_id",
                "BranchMaterial.Name == 'Unassigned' — no assigned material identifier.");
            AddPartial(
                panel,
                issues,
                "density_assigned_kg_per_m3",
                "BranchMaterial.Density == 0 on an unassigned material.");

            SetSubpanelDensity(panel, issues, volumeRaw);
            if (panel.WeightKg.HasValue)
            {
                // With no material on the subpanels their weight is zero, so GetWeight()
                // returns sheathing weight alone. Measured on a reference model: every gram
                // of a 4,963 kg total was sheathing. The number is real but it is NOT the
                // panel's structural weight, and saying so precisely matters more than
                // flagging a vague discrepancy.
                string sheathing = panel.WeightSheathingKg?.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture)
                    ?? "unavailable";
                AddPartial(
                    panel,
                    issues,
                    "weight_kg",
                    "GetWeight() returned a value but the subpanels carry no material, so their "
                    + $"weight is zero and this total is sheathing only ({sheathing} kg). It is not "
                    + "the panel's structural weight.");
            }
            return;
        }

        panel.MaterialName = materialName;
        if (panel.MaterialName == null)
        {
            AddPartial(
                panel,
                issues,
                "material_name",
                "BranchMaterial.Name is null or empty.");
        }

        try
        {
            panel.Grade = Clean(material.Grade);
            if (panel.Grade == null)
            {
                AddPartial(
                    panel,
                    issues,
                    "grade",
                    "BranchMaterial.Grade is null or empty.");
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "grade",
                $"BranchMaterial.Grade threw {Describe(ex)}.");
        }

        try
        {
            Guid materialId = material.UniqueId;
            panel.MaterialId = materialId == Guid.Empty
                ? null
                : materialId.ToString();
            if (panel.MaterialId == null)
            {
                AddPartial(
                    panel,
                    issues,
                    "material_id",
                    "BranchMaterial.UniqueId is Guid.Empty.");
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "material_id",
                $"BranchMaterial.UniqueId threw {Describe(ex)}.");
        }

        try
        {
            string speciesCode = material.Species.ToString();
            if (speciesCode.Equals("Unset", StringComparison.OrdinalIgnoreCase))
            {
                panel.Species = null;
                AddPartial(
                    panel,
                    issues,
                    "species",
                    "BranchMaterial.Species == Unset.");
            }
            else if (SpeciesDisplay.TryGetValue(speciesCode, out string display))
            {
                panel.Species = display;
            }
            else
            {
                panel.Species = speciesCode;
                AddPartial(
                    panel,
                    issues,
                    "species",
                    $"SpeciesTypes.{speciesCode} has no SPECIES_DISPLAY v1 entry; the raw enum name was emitted.");
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "species",
                $"BranchMaterial.Species threw {Describe(ex)}.");
        }

        double? assignedDensity = ReadFinite(
            panel,
            issues,
            "density_assigned_kg_per_m3",
            () => material.Density,
            1.0,
            allowZero: false);
        panel.DensityAssignedKgPerM3 = Round(assignedDensity, 1);

        // Density is checked against the SUBPANEL scope, because that is the only scope where
        // volume and weight describe the same material. See SetSubpanelDensity.
        SetSubpanelDensity(panel, issues, volumeRaw);
    }

    private static void SetUnavailableMaterial(
        PanelRecord panel,
        IssueTracker issues,
        string reason,
        bool invalid)
    {
        panel.Species = null;
        panel.Grade = null;
        panel.MaterialName = null;
        panel.MaterialId = null;
        panel.DensityAssignedKgPerM3 = null;
        panel.DensitySubpanelsKgPerM3 = null;

        foreach (string field in new[]
                 {
                     "species",
                     "grade",
                     "material_name",
                     "material_id",
                     "density_assigned_kg_per_m3"
                 })
        {
            if (invalid)
                AddInvalid(panel, issues, field, reason);
            else
                AddPartial(panel, issues, field, reason);
        }

        if (panel.WeightKg.HasValue)
        {
            if (invalid)
                AddInvalid(panel, issues, "weight_kg", $"GetWeight() is not traceable because {reason}");
            else
                AddPartial(panel, issues, "weight_kg", $"GetWeight() is not traceable because {reason}");
        }
    }

    /// <summary>
    /// Density on the SUBPANEL scope: subpanel weight over subpanel volume. This is the only
    /// scope where numerator and denominator describe the same material, so it is the only
    /// density we can honestly report. The panel-scope alternative - GetWeight() over
    /// DLT.Volume - mixes a sheathing-inclusive weight with a subpanel-only volume and
    /// produced a figure that looked like a Branch defect but was our own arithmetic.
    ///
    /// Note the plugin author's caveat: a non-uniform panel (an acoustic build-up, say)
    /// legitimately has no single density, so a null here is not necessarily an error.
    /// </summary>
    private static void SetSubpanelDensity(
        PanelRecord panel,
        IssueTracker issues,
        double? subpanelVolumeRaw)
    {
        // A zero subpanel weight means the subpanels carry no material, so the quotient is
        // 0 - and 0 is not a density, it is an absence. Emitting it as a number would claim
        // massless timber. Null with a recorded reason instead: this is the same failure
        // class as a silently substituted default, just arrived at by division.
        if (panel.WeightSubpanelsKg.HasValue
            && panel.WeightSubpanelsKg.Value <= 0.0)
        {
            panel.DensitySubpanelsKgPerM3 = null;
            AddPartial(
                panel,
                issues,
                "density_subpanels_kg_per_m3",
                "Subpanel weight is zero because the subpanels carry no material, so no "
                + "density can be derived. This is not a density of zero.");
            return;
        }

        panel.DensitySubpanelsKgPerM3 =
            panel.WeightSubpanelsKg.HasValue
            && subpanelVolumeRaw.HasValue
            && subpanelVolumeRaw.Value > 0.0
                ? Round(panel.WeightSubpanelsKg.Value / subpanelVolumeRaw.Value, 1)
                : null;

        if (!panel.DensitySubpanelsKgPerM3.HasValue
            && !panel.FieldErrors.ContainsKey("density_subpanels_kg_per_m3"))
        {
            AddPartial(
                panel,
                issues,
                "density_subpanels_kg_per_m3",
                "weight_subpanels_kg or volume_m3 is unavailable.");
        }
    }

    private static void ExtractLamination(
        DLT dlt,
        PanelRecord panel,
        UnitConversions units,
        IssueTracker issues)
    {
        DLTParameters parameters;
        try
        {
            parameters = dlt.Parameters;
        }
        catch (Exception ex)
        {
            string reason = $"DLT.Parameters threw {Describe(ex)}.";
            foreach (string field in new[]
                     {
                         "lam.profile",
                         "lam.arrangement",
                         "lam.thickness_mm",
                         "lam.height_mm"
                     })
            {
                AddInvalid(panel, issues, field, reason);
            }
            parameters = null;
        }

        if (parameters == null)
        {
            foreach (string field in new[]
                     {
                         "lam.profile",
                         "lam.arrangement",
                         "lam.thickness_mm",
                         "lam.height_mm"
                     })
            {
                if (!panel.FieldErrors.ContainsKey(field))
                {
                    AddPartial(
                        panel,
                        issues,
                        field,
                        "DLT.Parameters is null.");
                }
            }
        }
        else
        {
            try
            {
                panel.Lam.Profile = CleanEnum(parameters.LamProfileType.ToString());
                if (panel.Lam.Profile == null)
                {
                    AddPartial(
                        panel,
                        issues,
                        "lam.profile",
                        "DLT.Parameters.LamProfileType is Unset or Unknown.");
                }
            }
            catch (Exception ex)
            {
                AddInvalid(
                    panel,
                    issues,
                    "lam.profile",
                    $"DLT.Parameters.LamProfileType threw {Describe(ex)}.");
            }

            try
            {
                panel.Lam.Arrangement = CleanEnum(
                    parameters.LamArrangementType.ToString());
                if (panel.Lam.Arrangement == null)
                {
                    AddPartial(
                        panel,
                        issues,
                        "lam.arrangement",
                        "DLT.Parameters.LamArrangementType is Unset or Unknown.");
                }
            }
            catch (Exception ex)
            {
                AddInvalid(
                    panel,
                    issues,
                    "lam.arrangement",
                    $"DLT.Parameters.LamArrangementType threw {Describe(ex)}.");
            }

            panel.Lam.ThicknessMm = Round(ReadFinite(
                panel,
                issues,
                "lam.thickness_mm",
                () => parameters.LamWidth,
                units.LengthToMillimetres,
                allowZero: false), 2);
            panel.Lam.HeightMm = Round(ReadFinite(
                panel,
                issues,
                "lam.height_mm",
                () => parameters.SubpanelDepth,
                units.LengthToMillimetres,
                allowZero: false), 2);
        }

        try
        {
            panel.Lam.Notation = Clean(dlt.GetNotation());
            if (panel.Lam.Notation == null)
            {
                AddPartial(
                    panel,
                    issues,
                    "lam.notation",
                    "DLT.GetNotation() returned null or empty.");
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "lam.notation",
                $"DLT.GetNotation() threw {Describe(ex)}.");
        }

        try
        {
            int count = dlt.GetLamsCount();
            if (count < 0)
            {
                AddInvalid(
                    panel,
                    issues,
                    "lam.count",
                    $"DLT.GetLamsCount() returned negative value {count}.");
            }
            else
            {
                panel.Lam.Count = count;
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "lam.count",
                $"DLT.GetLamsCount() threw {Describe(ex)}.");
        }
    }

    private static decimal? ExtractLogistics(
        DLT dlt,
        PanelRecord panel,
        IssueTracker issues)
    {
        panel.Logistics.FabricationPackage = ReadOptionalString(
            panel,
            issues,
            "logistics.fabrication_package",
            () => dlt.FabricationPackage);
        panel.Logistics.Truck = ReadOptionalString(
            panel,
            issues,
            "logistics.truck",
            () => dlt.Truck);
        panel.Logistics.Bundle = ReadOptionalString(
            panel,
            issues,
            "logistics.bundle",
            () => dlt.Bundle);

        string bundleLevel = ReadOptionalString(
            panel,
            issues,
            "logistics.bundle_level",
            () => dlt.BundleLevel);
        panel.Logistics.BundleLevel = ParseOptionalInteger(
            panel,
            issues,
            "logistics.bundle_level",
            bundleLevel);

        string installSequence = ReadOptionalString(
            panel,
            issues,
            "logistics.install_sequence",
            () => dlt.InstallSequence);
        decimal? rawInstallSequence = ParseOptionalDecimal(
            panel,
            issues,
            "logistics.install_sequence",
            installSequence);
        if (rawInstallSequence.HasValue
            && decimal.Truncate(rawInstallSequence.Value)
               == rawInstallSequence.Value)
        {
            panel.Logistics.InstallSequence =
                decimal.ToInt32(rawInstallSequence.Value);
        }
        return rawInstallSequence;
    }

    private static void NormalizeInstallSequences(
        IReadOnlyCollection<PanelExtraction> panels)
    {
        List<PanelExtraction> present = panels
            .Where(panel => panel.RawInstallSequence.HasValue)
            .ToList();
        if (present.Count == 0)
            return;

        bool containsFractionalOrdinal = present.Any(panel =>
            decimal.Truncate(panel.RawInstallSequence.Value)
            != panel.RawInstallSequence.Value);
        if (!containsFractionalOrdinal)
            return;

        // Branch insertion ordinals can be fractional (for example 3.5).
        // Schema 1.0 intentionally stores the resulting sequence as an
        // integer. Dense-ranking the complete native ordering preserves every
        // relative position and produces the fixture's 1..N sequence without
        // rounding or discarding an ordinal.
        Dictionary<decimal, int> ranks = present
            .Select(panel => panel.RawInstallSequence.Value)
            .Distinct()
            .OrderBy(value => value)
            .Select((value, index) => new { value, rank = index + 1 })
            .ToDictionary(item => item.value, item => item.rank);
        foreach (PanelExtraction panel in present)
        {
            panel.Panel.Logistics.InstallSequence =
                ranks[panel.RawInstallSequence.Value];
        }
    }

    private static HashSet<Guid> ExtractCounts(
        DLT dlt,
        PanelRecord panel,
        Guid panelId,
        IReadOnlyDictionary<Guid, int> planarCutsByHost,
        IssueTracker issues)
    {
        HashSet<Guid> fastenerDapIds = new();

        try
        {
            var daps = dlt.CollectDaps(true);
            if (daps == null)
                throw new InvalidOperationException("returned null");
            panel.Counts.DapsTotal = daps.Count;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "counts.daps_total",
                $"DLT.CollectDaps(true) {Describe(ex)}.");
        }

        try
        {
            var daps = dlt.CollectFastenerDaps();
            if (daps == null)
                throw new InvalidOperationException("returned null");
            panel.Counts.DapsFastener = daps.Count;
            foreach (object dap in daps)
            {
                if (TryReadUniqueId(dap, out Guid id, out _) && id != Guid.Empty)
                    fastenerDapIds.Add(id);
            }
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "counts.daps_fastener",
                $"DLT.CollectFastenerDaps() {Describe(ex)}.");
        }

        try
        {
            var daps = dlt.CollectNonFastenerDaps();
            if (daps == null)
                throw new InvalidOperationException("returned null");
            panel.Counts.DapsNonFastener = daps.Count;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "counts.daps_non_fastener",
                $"DLT.CollectNonFastenerDaps() {Describe(ex)}.");
        }

        try
        {
            var fasteners = dlt.CollectFasteners();
            if (fasteners == null)
                throw new InvalidOperationException("returned null");
            panel.Counts.Fasteners = fasteners.Count;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "counts.fasteners",
                $"DLT.CollectFasteners() {Describe(ex)}.");
        }

        try
        {
            var supports = dlt.PanelSupports;
            if (supports == null)
                throw new InvalidOperationException("returned null");
            panel.Counts.PanelSupports = supports.Count;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "counts.panel_supports",
                $"DLT.PanelSupports {Describe(ex)}.");
        }

        panel.Counts.PlanarCuts = planarCutsByHost.TryGetValue(
            panelId,
            out int planarCutCount)
            ? planarCutCount
            : 0;
        return fastenerDapIds;
    }

    private static void ExtractComplexity(
        DLT dlt,
        PanelRecord panel,
        UnitConversions units,
        double modelTolerance,
        double? grossAreaRaw,
        double? netAreaRaw,
        IssueTracker issues)
    {
        panel.Complexity.PenetrationAreaSqft =
            grossAreaRaw.HasValue && netAreaRaw.HasValue
                ? Round(
                    grossAreaRaw.Value - netAreaRaw.Value,
                    2)
                : null;
        if (!panel.Complexity.PenetrationAreaSqft.HasValue)
        {
            AddInvalid(
                panel,
                issues,
                "complexity.penetration_area_sqft",
                "gross_area_sqft or net_area_sqft is unavailable.");
        }

        panel.Complexity.PenetrationCount =
            panel.Counts.DapsNonFastener.HasValue
            && panel.Counts.PlanarCuts.HasValue
                ? panel.Counts.DapsNonFastener.Value
                  + panel.Counts.PlanarCuts.Value
                : null;
        if (!panel.Complexity.PenetrationCount.HasValue)
        {
            AddInvalid(
                panel,
                issues,
                "complexity.penetration_count",
                "counts.daps_non_fastener or counts.planar_cuts is unavailable.");
        }
        // Derived from AREA REMOVED, not from the dap count. A dap is a surface recess, so
        // counting daps made this true on every panel of every model - a flag that never
        // varies carries no information. Gross minus Net is material actually gone, which on
        // a reference model is non-zero for 66 of 138 panels.
        const double AreaEpsilonSqft = 0.01;
        panel.Complexity.HasPenetrations =
            panel.Complexity.PenetrationAreaSqft.HasValue
                ? panel.Complexity.PenetrationAreaSqft.Value > AreaEpsilonSqft
                : null;
        if (!panel.Complexity.HasPenetrations.HasValue)
        {
            AddInvalid(
                panel,
                issues,
                "complexity.has_penetrations",
                "complexity.penetration_area_sqft is unavailable.");
        }

        Curve boundary;
        try
        {
            boundary = dlt.Boundary;
            if (boundary == null)
                throw new InvalidOperationException("returned null");
            if (!boundary.IsValid)
                throw new InvalidOperationException("returned an invalid curve");
        }
        catch (Exception ex)
        {
            // Only the perimeter depends on Boundary now; corner_count and is_rectangular
            // come from NetAreaGeometry below. Do NOT return here - a real model lost
            // Boundary.GetLength() to a NotLicensedException on all 105 panels while its
            // solid geometry was perfectly readable, and bailing out threw away corner data
            // that was still obtainable.
            string reason = $"DLT.Boundary {Describe(ex)}.";
            AddInvalid(panel, issues, "complexity.perimeter_m", reason);
            AddInvalid(panel, issues, "complexity.corners_per_m", reason);
            boundary = null;
        }

        double? perimeterRaw = null;
        try
        {
            // Null only when the catch above already recorded why. Skip rather than let it
            // resurface as a misleading NullReferenceException.
            if (boundary == null)
                throw new SkipFieldException();

            double length = boundary.GetLength() * units.LengthToMetres;
            if (!double.IsFinite(length) || length <= 0.0)
            {
                AddInvalid(
                    panel,
                    issues,
                    "complexity.perimeter_m",
                    $"DLT.Boundary.GetLength() converted to invalid value {FormatRaw(length)}.");
            }
            else
            {
                perimeterRaw = length;
                panel.Complexity.PerimeterM = Round(length, 3);
            }
        }
        catch (SkipFieldException)
        {
            // reason already recorded where boundary was nulled
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                "complexity.perimeter_m",
                $"DLT.Boundary.GetLength() threw {Describe(ex)}.");
        }

        // Corner count comes from DLT.NetAreaGeometry, NOT from DLT.Boundary.
        //
        // Boundary is the panel's NOMINAL RECTANGLE - always 4 corners, on every panel of
        // every model tested. Its LENGTH is still the true perimeter (a rectilinear notch
        // replaces two removed edges with two of equal length, so perimeter is preserved),
        // which is why perimeter_m above is correct while corner_count from the same curve
        // was not.
        //
        // NetAreaGeometry is a closed solid: a plain box has 6 faces and each notch adds 2.
        // Its largest planar face is the panel's net plan view, and that face's area equals
        // DLT.NetArea exactly. Measured on a reference model, this yields a real distribution
        // of 4/5/6/8 corners with 67 of 138 panels non-rectangular, where Boundary reported 4
        // for all 138.
        try
        {
            Brep net = dlt.NetAreaGeometry;
            if (net == null)
                throw new InvalidOperationException("returned null");

            BrepFace plan = null;
            double planArea = 0.0;
            foreach (BrepFace face in net.Faces)
            {
                AreaMassProperties fa = AreaMassProperties.Compute(face);
                if (fa == null)
                    continue;
                if (fa.Area > planArea)
                {
                    planArea = fa.Area;
                    plan = face;
                }
            }

            BrepLoop outer = plan?.Loops
                ?.FirstOrDefault(loop => loop.LoopType == BrepLoopType.Outer);
            Curve outline = outer?.To3dCurve();
            if (outline == null)
                throw new InvalidOperationException("has no outer loop on its largest planar face");

            BoundaryShape shape = BoundaryShape.Read(
                outline,
                Math.Max(modelTolerance, RhinoMath.ZeroTolerance));
            panel.Complexity.CornerCount = shape.CornerCount;
            panel.Complexity.IsRectangular = shape.IsRectangular;
        }
        catch (Exception ex)
        {
            string reason = $"DLT.NetAreaGeometry plan-face corner analysis {Describe(ex)}.";
            AddInvalid(
                panel,
                issues,
                "complexity.corner_count",
                reason);
            AddInvalid(
                panel,
                issues,
                "complexity.is_rectangular",
                reason);
        }

        panel.Complexity.CornersPerM =
            panel.Complexity.CornerCount.HasValue
            && perimeterRaw.HasValue
            && perimeterRaw.Value > 0.0
                ? Round(
                    panel.Complexity.CornerCount.Value
                    / perimeterRaw.Value,
                    3)
                : null;

        if (panel.Complexity.CornersPerM == null
            && !panel.FieldErrors.ContainsKey("complexity.corners_per_m"))
        {
            AddInvalid(
                panel,
                issues,
                "complexity.corners_per_m",
                "corner_count or perimeter_m is unavailable.");
        }
    }

    private static ElementRecord ExtractElement(
        LocatedObject located,
        IReadOnlySet<Guid> fastenerDapIds,
        IssueTracker issues)
    {
        bool isDap = ImplementsInterface(
            located.Instance.GetType(),
            "IDap");
        ElementRecord element = new()
        {
            Type = located.TypeName,
            Id = located.Id.ToString(),
            InBlock = located.InBlock,
            BlockId = located.InBlock ? located.BlockId.ToString() : null,
            IsFastenerDap = isDap
                ? fastenerDapIds.Contains(located.Id)
                : null
        };

        if (!located.HasValidId)
        {
            AddElementInvalid(
                element,
                issues,
                "id",
                "UniqueId was Guid.Empty.");
        }

        if (TryReadGuidProperty(
                located.Instance,
                "HostId",
                out Guid hostId,
                out string hostError))
        {
            element.HostId = hostId == Guid.Empty ? null : hostId.ToString();
        }
        else
        {
            AddElementInvalid(element, issues, "host_id", hostError);
        }

        bool isWeighable = ImplementsInterface(
            located.Instance.GetType(),
            "IWeighable");
        if (isWeighable)
        {
            MethodInfo getWeight = located.Instance.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    method.Name == "GetWeight"
                    && method.GetParameters().Length == 0
                    && method.ReturnType == typeof(double));
            if (getWeight == null)
            {
                AddElementInvalid(
                    element,
                    issues,
                    "weight_kg",
                    "The runtime type implements IWeighable but exposes no public parameterless double GetWeight().");
            }
            else
            {
                try
                {
                    double value = (double)getWeight.Invoke(
                        located.Instance,
                        null);
                    if (!double.IsFinite(value) || value < 0.0)
                    {
                        AddElementInvalid(
                            element,
                            issues,
                            "weight_kg",
                            $"GetWeight() returned invalid value {FormatRaw(value)}.");
                    }
                    else
                    {
                        element.WeightKg = Round(value, 2);
                    }
                }
                catch (Exception ex)
                {
                    AddElementInvalid(
                        element,
                        issues,
                        "weight_kg",
                        $"GetWeight() threw {Describe(ex)}.");
                }
            }
        }

        return element;
    }

    private static void SetOutcome(
        ExtractionDocument output,
        ObjectCensus census,
        IssueTracker issues)
    {
        if (issues.HasTruncation)
        {
            output.Extraction.Status = "partial_truncated";
            output.Extraction.StatusDetail = issues.Detail;
            output.Extraction.Dropped = issues.ToDroppedRecord();
            return;
        }

        output.Extraction.Dropped = null;
        if (output.Panels.Count == 0)
        {
            output.Extraction.Status = "no_branch_content";
            output.Extraction.StatusDetail =
                census.TotalObjectsEncountered == 0
                    ? "No Branch objects were found at top level or in block definitions."
                    : "Branch objects were found, but the complete walk contained no DLT panels.";
            return;
        }

        if (issues.HasInvalid)
        {
            output.Extraction.Status = "partial_invalid";
            output.Extraction.StatusDetail = issues.Detail;
        }
        else if (issues.HasPartial
                 || output.Panels.Any(panel => panel.FieldErrors.Count > 0))
        {
            output.Extraction.Status = "partial";
            output.Extraction.StatusDetail =
                issues.UnassignedMaterialPanels == output.Panels.Count
                && output.Panels.Count > 0
                    ? $"{output.Panels.Count} of {output.Panels.Count} panels carry an unassigned material; native species, grade, and assigned density are unavailable."
                    : issues.Detail;
        }
        else
        {
            output.Extraction.Status = "ok";
            output.Extraction.StatusDetail = null;
        }
    }

    private static void ApplyBranchProjectMetadata(
        ExtractionDocument output,
        BranchDoc document)
    {
        try
        {
            string number = Clean(document.Number);
            if (number != null)
            {
                string candidate = number.ToUpperInvariant();
                if (candidate.StartsWith("S", StringComparison.Ordinal)
                    && candidate.Skip(1).All(char.IsDigit))
                {
                    output.Job = candidate;
                }
            }
        }
        catch
        {
            // Path-derived job remains authoritative when metadata is absent.
        }

        if (output.JobName != null)
            return;
        try
        {
            string name = Clean(document.Name);
            if (name == null)
                return;
            if (name.StartsWith(output.Job, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(output.Job.Length).Trim(' ', '-', '_');
            output.JobName = Clean(name);
        }
        catch
        {
            // job_name is nullable in the frozen contract.
        }
    }

    private static double? ReadFinite(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        Func<double> reader,
        double factor,
        bool allowZero)
    {
        try
        {
            double raw = reader();
            double value = raw * factor;
            if (!double.IsFinite(raw)
                || !double.IsFinite(factor)
                || !double.IsFinite(value))
            {
                AddInvalid(
                    panel,
                    issues,
                    field,
                    $"Native value {FormatRaw(raw)} or conversion factor {FormatRaw(factor)} was not finite.");
                return null;
            }

            if (value < 0.0 || (!allowZero && value == 0.0))
            {
                AddInvalid(
                    panel,
                    issues,
                    field,
                    $"Native read converted to nonsensical value {FormatRaw(value)}.");
                return null;
            }

            return value;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                field,
                $"Native read threw {Describe(ex)}.");
            return null;
        }
    }

    private static string ReadRequiredString(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        Func<string> reader,
        string missingReason)
    {
        try
        {
            string value = Clean(reader());
            if (value == null)
                AddPartial(panel, issues, field, missingReason);
            return value;
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                field,
                $"Native read threw {Describe(ex)}.");
            return null;
        }
    }

    private static string ReadOptionalString(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        Func<string> reader)
    {
        try
        {
            return Clean(reader());
        }
        catch (Exception ex)
        {
            AddInvalid(
                panel,
                issues,
                field,
                $"Native read threw {Describe(ex)}.");
            return null;
        }
    }

    private static int? ParseOptionalInteger(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        string value)
    {
        if (value == null)
            return null;
        if (int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
            && parsed >= 0)
        {
            return parsed;
        }

        AddInvalid(
            panel,
            issues,
            field,
            $"ILogistic value '{value}' is not a non-negative integer.");
        return null;
    }

    private static decimal? ParseOptionalDecimal(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        string value)
    {
        if (value == null)
            return null;
        if (decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal parsed)
            && parsed >= 0
            && parsed <= int.MaxValue)
        {
            return parsed;
        }

        AddInvalid(
            panel,
            issues,
            field,
            $"ILogistic value '{value}' is not a non-negative sequence ordinal.");
        return null;
    }

    private static bool CanModelType(Type type)
    {
        if (type == null)
            return false;
        if (typeof(DLT).IsAssignableFrom(type))
            return true;

        PropertyInfo uniqueId = type.GetProperty(
            "UniqueId",
            BindingFlags.Public | BindingFlags.Instance);
        PropertyInfo hostId = type.GetProperty(
            "HostId",
            BindingFlags.Public | BindingFlags.Instance);
        return IsGuidProperty(uniqueId) && IsGuidProperty(hostId);
    }

    private static bool IsGuidProperty(PropertyInfo property)
    {
        return property != null
               && (property.PropertyType == typeof(Guid)
                   || property.PropertyType == typeof(string));
    }

    private static bool ImplementsInterface(Type type, string interfaceName)
    {
        return type != null
               && type.GetInterfaces().Any(candidate =>
                   candidate.Name.Equals(
                       interfaceName,
                       StringComparison.Ordinal));
    }

    private static bool IsConnectionObject(Type type)
    {
        return ImplementsInterface(type, "IBranchCollection")
               && type.GetProperty(
                   "Connection",
                   BindingFlags.Public | BindingFlags.Instance) != null;
    }

    private static bool TryReadUniqueId(
        object instance,
        out Guid id,
        out string error)
    {
        return TryReadGuidProperty(instance, "UniqueId", out id, out error);
    }

    private static bool TryReadGuidProperty(
        object instance,
        string propertyName,
        out Guid id,
        out string error)
    {
        id = Guid.Empty;
        error = null;
        if (instance == null)
        {
            error = $"{propertyName} owner was null.";
            return false;
        }

        try
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                error = $"{instance.GetType().FullName}.{propertyName} was not found.";
                return false;
            }

            object value = property.GetValue(instance);
            if (value is Guid guid)
            {
                id = guid;
                return true;
            }
            if (value is string text && Guid.TryParse(text, out guid))
            {
                id = guid;
                return true;
            }

            error = $"{instance.GetType().FullName}.{propertyName} did not return a Guid.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{instance.GetType().FullName}.{propertyName} threw {Describe(ex)}.";
            return false;
        }
    }

    private static void AddPartial(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        string reason)
    {
        panel.FieldErrors[field] = reason;
        issues.Partial(field, reason);
    }

    private static void AddInvalid(
        PanelRecord panel,
        IssueTracker issues,
        string field,
        string reason)
    {
        panel.FieldErrors[field] = reason;
        issues.Invalid(field, reason);
    }

    private static void AddElementInvalid(
        ElementRecord element,
        IssueTracker issues,
        string field,
        string reason)
    {
        element.FieldErrors[field] = reason;
        issues.Invalid($"{element.Type}.{field}", reason);
    }

    private static string Clean(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string CleanEnum(string value)
    {
        string cleaned = Clean(value);
        return cleaned != null
               && !cleaned.Equals("Unset", StringComparison.OrdinalIgnoreCase)
               && !cleaned.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : null;
    }

    internal static double? Round(double? value, int decimals)
    {
        return value.HasValue
            ? Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string FormatRaw(double value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string Describe(Exception ex)
    {
        Exception actual = Unwrap(ex);
        return $"{actual.GetType().Name}: {actual.Message}";
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is TargetInvocationException invocation
               && invocation.InnerException != null)
        {
            ex = invocation.InnerException;
        }
        return ex;
    }

    private static Dictionary<string, int> SortCounts(
        IDictionary<string, int> values)
    {
        Dictionary<string, int> sorted = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, int> pair in values
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            sorted[pair.Key] = pair.Value;
        }
        return sorted;
    }

    private sealed class ObjectCensus
    {
        private readonly Dictionary<string, LocatedObject> _deduplicated = new(
            StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<Guid>> _topIds = new(
            StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<Guid>> _blockIds = new(
            StringComparer.Ordinal);
        private readonly HashSet<string> _referenceKeys = new(
            StringComparer.Ordinal);
        private int _missingIdOrdinal;

        internal List<LocatedObject> Objects => _deduplicated.Values.ToList();
        internal int BlockDefinitionCount { get; set; }
        internal int ReferenceTypeCount => _referenceKeys.Count;
        internal int TotalObjectsEncountered { get; private set; }
        internal Dictionary<string, int> ObjectsInBlocks { get; } = new(
            StringComparer.Ordinal);
        internal Dictionary<string, int> OverlapWithTopLevel { get; } = new(
            StringComparer.Ordinal);

        internal void Register(
            object instance,
            bool inBlock,
            Guid blockId,
            IssueTracker issues)
        {
            if (instance == null)
            {
                issues.Truncated(
                    inBlock ? $"block:{blockId}:members" : "top_level",
                    1,
                    "Collection contained a null Branch object.");
                return;
            }

            TotalObjectsEncountered++;
            string typeName = instance.GetType().Name;
            bool hasId = TryReadUniqueId(instance, out Guid id, out string idError)
                         && id != Guid.Empty;
            string key = hasId
                ? $"{typeName}:{id:D}"
                : $"missing:{RuntimeHelpers.GetHashCode(instance)}:{_missingIdOrdinal++}";

            if (!hasId)
            {
                issues.Invalid(
                    $"{typeName}.UniqueId",
                    idError ?? "UniqueId was Guid.Empty.");
            }

            Dictionary<string, HashSet<Guid>> ids = inBlock
                ? _blockIds
                : _topIds;
            if (hasId)
            {
                if (!ids.TryGetValue(typeName, out HashSet<Guid> set))
                {
                    set = new HashSet<Guid>();
                    ids[typeName] = set;
                }
                set.Add(id);
            }

            if (inBlock && !ReferenceTypes.Contains(typeName))
            {
                ObjectsInBlocks[typeName] = ObjectsInBlocks.TryGetValue(
                    typeName,
                    out int count)
                    ? count + 1
                    : 1;
            }

            if (ReferenceTypes.Contains(typeName))
            {
                _referenceKeys.Add(key);
                return;
            }

            if (!_deduplicated.ContainsKey(key))
            {
                _deduplicated[key] = new LocatedObject(
                    instance,
                    typeName,
                    id,
                    hasId,
                    inBlock,
                    blockId);
            }
        }

        internal void FinalizeCounts()
        {
            foreach (string typeName in ObjectsInBlocks.Keys)
            {
                int overlap = 0;
                if (_topIds.TryGetValue(typeName, out HashSet<Guid> top)
                    && _blockIds.TryGetValue(typeName, out HashSet<Guid> blocks))
                {
                    overlap = top.Count <= blocks.Count
                        ? top.Count(blocks.Contains)
                        : blocks.Count(top.Contains);
                }
                OverlapWithTopLevel[typeName] = overlap;
            }
        }
    }

    private sealed class LocatedObject
    {
        internal LocatedObject(
            object instance,
            string typeName,
            Guid id,
            bool hasValidId,
            bool inBlock,
            Guid blockId)
        {
            Instance = instance;
            TypeName = typeName;
            Id = id;
            HasValidId = hasValidId;
            InBlock = inBlock;
            BlockId = blockId;
        }

        internal object Instance { get; }
        internal string TypeName { get; }
        internal Guid Id { get; }
        internal bool HasValidId { get; }
        internal bool InBlock { get; }
        internal Guid BlockId { get; }
    }

    private sealed class PanelExtraction
    {
        internal PanelExtraction(
            PanelRecord panel,
            HashSet<Guid> fastenerDapIds,
            decimal? rawInstallSequence)
        {
            Panel = panel;
            FastenerDapIds = fastenerDapIds;
            RawInstallSequence = rawInstallSequence;
        }

        internal PanelRecord Panel { get; }
        internal HashSet<Guid> FastenerDapIds { get; }
        internal decimal? RawInstallSequence { get; }
    }
}

internal sealed class UnitConversions
{
    internal static readonly UnitConversions Invalid = new();

    private UnitConversions()
    {
        LengthToMillimetres = double.NaN;
        LengthToMetres = double.NaN;
        AreaToSquareFeet = double.NaN;
        VolumeToM3 = double.NaN;
    }

    internal UnitConversions(UnitSystem modelUnits)
    {
        LengthToMillimetres = RhinoMath.UnitScale(
            modelUnits,
            UnitSystem.Millimeters);
        if (!double.IsFinite(LengthToMillimetres)
            || LengthToMillimetres <= 0.0)
        {
            throw new InvalidOperationException(
                $"RhinoMath.UnitScale({modelUnits}, Millimeters) returned {LengthToMillimetres}.");
        }

        LengthToMetres = LengthToMillimetres / 1000.0;
        AreaToSquareFeet =
            LengthToMillimetres * LengthToMillimetres / 92_903.04;
        VolumeToM3 =
            LengthToMillimetres * LengthToMillimetres * LengthToMillimetres
            / 1_000_000_000.0;
    }

    internal double LengthToMillimetres { get; }
    internal double LengthToMetres { get; }
    internal double AreaToSquareFeet { get; }
    internal double VolumeToM3 { get; }
}

internal sealed class BoundaryShape
{
    private BoundaryShape(int cornerCount, bool isRectangular)
    {
        CornerCount = cornerCount;
        IsRectangular = isRectangular;
    }

    internal int CornerCount { get; }
    internal bool IsRectangular { get; }

    internal static BoundaryShape Read(Curve boundary, double tolerance)
    {
        if (boundary == null)
            throw new ArgumentNullException(nameof(boundary));

        List<Point3d> points;
        if (boundary.TryGetPolyline(out Polyline polyline))
        {
            points = polyline.ToList();
        }
        else
        {
            Curve[] segments = boundary.DuplicateSegments();
            if (segments == null || segments.Length == 0)
            {
                throw new InvalidOperationException(
                    "Boundary was neither a polyline nor a segmented curve.");
            }
            points = segments.Select(segment => segment.PointAtStart).ToList();
            if (!boundary.IsClosed)
                points.Add(segments[^1].PointAtEnd);
        }

        if (points.Count > 1
            && points[0].DistanceTo(points[^1]) <= tolerance)
        {
            points.RemoveAt(points.Count - 1);
        }

        points = RemoveCollinearVertices(points, boundary.IsClosed, tolerance);
        int cornerCount = boundary.IsClosed
            ? points.Count
            : Math.Max(0, points.Count - 2);
        bool rectangular = boundary.IsClosed
                           && points.Count == 4
                           && HasFourRightAngles(points, tolerance);
        return new BoundaryShape(cornerCount, rectangular);
    }

    private static List<Point3d> RemoveCollinearVertices(
        List<Point3d> input,
        bool closed,
        double tolerance)
    {
        if (input.Count < 3)
            return input;

        List<Point3d> points = new(input);
        bool changed;
        do
        {
            changed = false;
            int start = closed ? 0 : 1;
            int end = closed ? points.Count : points.Count - 1;
            for (int index = start; index < end && points.Count >= 3; index++)
            {
                int previous = (index - 1 + points.Count) % points.Count;
                int next = (index + 1) % points.Count;
                Vector3d a = points[index] - points[previous];
                Vector3d b = points[next] - points[index];
                double aLength = a.Length;
                double bLength = b.Length;
                if (aLength <= tolerance || bLength <= tolerance)
                {
                    points.RemoveAt(index);
                    changed = true;
                    break;
                }

                a.Unitize();
                b.Unitize();
                if (Vector3d.CrossProduct(a, b).Length <= 1e-8
                    && Vector3d.Multiply(a, b) > 0.0)
                {
                    points.RemoveAt(index);
                    changed = true;
                    break;
                }
            }
        } while (changed);

        return points;
    }

    private static bool HasFourRightAngles(
        IReadOnlyList<Point3d> points,
        double tolerance)
    {
        for (int index = 0; index < 4; index++)
        {
            Vector3d incoming =
                points[(index - 1 + 4) % 4] - points[index];
            Vector3d outgoing = points[(index + 1) % 4] - points[index];
            if (incoming.Length <= tolerance || outgoing.Length <= tolerance)
                return false;
            incoming.Unitize();
            outgoing.Unitize();
            if (Math.Abs(Vector3d.Multiply(incoming, outgoing)) > 1e-6)
                return false;
        }
        return true;
    }
}

internal sealed class IssueTracker
{
    private readonly List<string> _details = new();
    private readonly List<string> _droppedWhere = new();
    private readonly List<string> _droppedExceptions = new();
    private int _droppedCount;

    internal bool HasPartial { get; private set; }
    internal bool HasInvalid { get; private set; }
    internal bool HasTruncation { get; private set; }
    internal int UnassignedMaterialPanels { get; set; }

    internal string Detail
    {
        get
        {
            if (_details.Count == 0)
                return null;
            return string.Join(" | ", _details.Distinct().Take(8));
        }
    }

    internal void Partial(string where, string reason)
    {
        HasPartial = true;
        AddDetail(where, reason);
    }

    internal void Invalid(string where, string reason)
    {
        HasInvalid = true;
        AddDetail(where, reason);
    }

    internal void Truncated(string where, int count, string exception)
    {
        HasTruncation = true;
        _droppedCount += Math.Max(1, count);
        _droppedWhere.Add(where);
        if (!string.IsNullOrWhiteSpace(exception))
            _droppedExceptions.Add(exception);
        AddDetail(where, exception);
    }

    internal DroppedRecord ToDroppedRecord()
    {
        return new DroppedRecord
        {
            Where = string.Join(", ", _droppedWhere.Distinct()),
            Count = Math.Max(1, _droppedCount),
            Exception = _droppedExceptions.Count == 0
                ? null
                : string.Join(" | ", _droppedExceptions.Distinct().Take(4))
        };
    }

    private void AddDetail(string where, string reason)
    {
        if (_details.Count >= 32)
            return;
        _details.Add($"{where}: {reason}");
    }
}

internal static class BranchRuntimeInfo
{
    internal static string BranchDirectory
    {
        get
        {
            try
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate =>
                        candidate.GetName().Name == "Branch");
                return assembly == null
                    ? null
                    : System.IO.Path.GetDirectoryName(assembly.Location);
            }
            catch
            {
                return null;
            }
        }
    }
}

internal static class SummaryBuilder
{
    internal static SummaryRecord Build(
        IReadOnlyCollection<PanelRecord> panels)
    {
        List<PanelRecord> materialized =
            panels?.ToList() ?? new List<PanelRecord>();
        SummaryRecord summary = new()
        {
            PanelCount = materialized.Count,
            // Sums the EMITTED per-panel values at 1dp, not the raw ones. Two reasons:
            // the summary is specified as a pure rollup of panels[] (so it must be
            // reproducible by adding up the published column), and this keeps the
            // schema-0.3 total bit-identical alongside the per-panel invariant (§6.7).
            // Summing raw and rounding to 2dp gave 32743.82 against a baseline of 32744.1.
            TotalNetAreaSqft = SumComplete(
                materialized.Select(panel => panel.NetAreaSqft),
                1),
            TotalVolumeM3 = SumComplete(
                materialized.Select(panel => panel.RawVolumeM3),
                3),
            TotalWeightKg = SumComplete(
                materialized.Select(panel => panel.RawWeightKg),
                2),
            PanelsWithErrors = materialized.Count(
                panel => panel.FieldErrors.Count > 0)
        };

        foreach (IGrouping<string, PanelRecord> group in materialized
                     .GroupBy(panel => panel.Species, StringComparer.Ordinal)
                     .OrderBy(
                         group => group.Key ?? "\uffff",
                         StringComparer.Ordinal))
        {
            List<PanelRecord> groupedPanels = group.ToList();
            summary.MaterialBreakdown.Add(new MaterialBreakdownRecord
            {
                Species = group.Key,
                Layup = null,
                PanelCount = groupedPanels.Count,
                NetAreaSqft = SumComplete(
                    groupedPanels.Select(panel => panel.RawNetAreaSqft),
                    2),
                WeightKg = SumComplete(
                    groupedPanels.Select(panel => panel.RawWeightKg),
                    2)
            });
        }

        HashSet<string> trucks = new(
            materialized
                .Select(panel => panel.Logistics?.Truck)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> bundles = new(
            materialized
                .Where(panel =>
                    !string.IsNullOrWhiteSpace(panel.Logistics?.Bundle))
                .Select(panel =>
                    $"{panel.Logistics?.Truck ?? string.Empty}\u001f{panel.Logistics.Bundle}"),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> fabricationPackages = new(
            materialized
                .Select(panel => panel.Logistics?.FabricationPackage)
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        List<int> installSequences = materialized
            .Where(panel =>
                panel.Logistics?.InstallSequence.HasValue == true)
            .Select(panel => panel.Logistics.InstallSequence.Value)
            .ToList();
        long? installSequenceSpan = installSequences.Count == 0
            ? null
            : (long)installSequences.Max() - installSequences.Min() + 1L;

        summary.Logistics = new SummaryLogisticsRecord
        {
            TruckCount = trucks.Count,
            BundleCount = bundles.Count,
            FabricationPackageCount = fabricationPackages.Count,
            InstallSequenceSpan =
                installSequenceSpan is >= 0 and <= int.MaxValue
                    ? (int)installSequenceSpan.Value
                    : null,
            InstallSequenceCoveragePct = materialized.Count == 0
                ? 0.0
                : Math.Round(
                    100.0 * installSequences.Count / materialized.Count,
                    2,
                    MidpointRounding.AwayFromZero)
        };

        return summary;
    }

    private static double? SumComplete(
        IEnumerable<double?> source,
        int decimals)
    {
        List<double?> values = source.ToList();
        if (values.Count == 0 || values.Any(value => !value.HasValue))
            return null;

        return Math.Round(
            values.Sum(value => value.Value),
            decimals,
            MidpointRounding.AwayFromZero);
    }
}

internal sealed class LodSignals
{
    internal LodSignals(
        bool dapsPresent,
        bool fastenersPresent,
        bool connectionsPresent)
    {
        DapsPresent = dapsPresent;
        FastenersPresent = fastenersPresent;
        ConnectionsPresent = connectionsPresent;
    }

    internal bool DapsPresent { get; }
    internal bool FastenersPresent { get; }
    internal bool ConnectionsPresent { get; }
}

internal static class LodBuilder
{
    internal static LodRecord Build(
        IReadOnlyCollection<PanelRecord> panels,
        LodSignals signals)
    {
        List<PanelRecord> materialized =
            panels?.ToList() ?? new List<PanelRecord>();

        bool fastenerDapsPresent = materialized.Any(panel =>
            panel.Counts?.DapsFastener > 0);
        bool connectionEvidence = signals.ConnectionsPresent
                                  || signals.FastenersPresent
                                  || fastenerDapsPresent;
        bool penetrationEvidence = signals.DapsPresent
                                   || materialized.Any(panel =>
                                       panel.Complexity?.PenetrationCount > 0);
        bool materialAndLaminationPresent = materialized.Any(panel =>
            panel.MaterialName != null
            && panel.Grade != null
            && (panel.Lam?.Count.HasValue == true
                || panel.Lam?.Notation != null));
        bool installSequencePresent = materialized.Any(panel =>
            panel.Logistics?.InstallSequence.HasValue == true);
        bool numberingPresent = materialized.Any(panel =>
            !string.IsNullOrWhiteSpace(panel.Mark));
        bool truckingPresent = materialized.Any(panel =>
            !string.IsNullOrWhiteSpace(panel.Logistics?.Truck)
            || !string.IsNullOrWhiteSpace(panel.Logistics?.Bundle));

        int geometry = connectionEvidence && penetrationEvidence
            ? 400
            : materialAndLaminationPresent
                ? 350
                : 300;
        int? sequencing = numberingPresent && truckingPresent
            ? 450
            : installSequencePresent
                ? 350
                : null;
        int observed = Math.Max(geometry, sequencing ?? 0);

        List<string> evidence = new();
        if (signals.DapsPresent)
            evidence.Add("daps_present");
        if (signals.FastenersPresent)
            evidence.Add("fasteners_present");
        if (signals.ConnectionsPresent)
            evidence.Add("connections_present");
        if (materialAndLaminationPresent)
            evidence.Add("material_and_lamination_present");
        if (installSequencePresent)
            evidence.Add("install_sequence_present");
        evidence.Add(numberingPresent
            ? "numbering_present"
            : "no_numbering");
        evidence.Add(truckingPresent
            ? "trucking_data_present"
            : "no_trucking_data");

        return new LodRecord
        {
            Observed = observed,
            Declared = null,
            Geometry = geometry,
            Sequencing = sequencing,
            Evidence = evidence
        };
    }
}

/// <summary>Signals a field was deliberately skipped because its reason is already recorded.</summary>
internal sealed class SkipFieldException : Exception
{
}
