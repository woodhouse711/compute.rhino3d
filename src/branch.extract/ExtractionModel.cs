using System;
using System.Collections.Generic;

namespace BranchExtract;

internal sealed class ExtractionRecord
{
    public string SchemaVersion { get; set; } = "1.0";
    public string GeneratedAt { get; set; }
    public string SnapshotDate { get; set; }
    public string Job { get; set; }
    public string JobName { get; set; }
    public string ProductType { get; set; } = "DLT";
    public string ProductSubtype { get; set; }
    public SourceRecord Source { get; set; }
    public ExtractionMetadata Extraction { get; set; }
    public LodInfo Lod { get; set; }
    public List<Panel> Panels { get; set; } = new();
    public List<Element> Elements { get; set; } = new();
    public Summary Summary { get; set; }
    public UnitsRecord Units { get; set; } = new();
}

internal sealed class SourceRecord
{
    public string Extractor { get; set; } = "branch.extract";
    public string ExtractorVersion { get; set; }
    public string BranchPluginVersion { get; set; }
    public string RhinoVersion { get; set; }
    public string ModelFile { get; set; }
    public string ModelRevision { get; set; }
    public string BranchProjectGuid { get; set; }
    public string WeightSource { get; set; } = "branch_native_getweight";
    public string ComplexityConfigVersion { get; set; } = "cx-2026-07-30";
}

internal sealed class ExtractionMetadata
{
    public string Status { get; set; }
    public string StatusDetail { get; set; }
    public Dictionary<string, int> TypesRecognised { get; set; } = new();
    public Dictionary<string, int> TypesUnrecognised { get; set; } = new();
    public BlockRecord Blocks { get; set; } = new();
    public DroppedRecord Dropped { get; set; }
    public ExcludedRecord Excluded { get; set; } = new();
    public long? ElapsedMs { get; set; }
}

internal sealed class BlockRecord
{
    public int DefinitionCount { get; set; }
    public Dictionary<string, int> ObjectsInBlocks { get; set; } = new();
    public Dictionary<string, int> OverlapWithTopLevel { get; set; } = new();
}

internal sealed class DroppedRecord
{
    public string Where { get; set; }
    public int Count { get; set; }
    public string Exception { get; set; }
}

internal sealed class ExcludedRecord
{
    public List<string> ByLayerPolicy { get; set; } = new();
    public int ReferenceTypes { get; set; }
}

internal sealed class LodInfo
{
    public int? Observed { get; set; }
    public int? Declared { get; set; }
    public int? Geometry { get; set; }
    public int? Sequencing { get; set; }
    public List<string> Evidence { get; set; } = new();
}

internal sealed class Panel
{
    public string Mark { get; set; }
    public string BranchId { get; set; }
    public string InstanceMark { get; set; }
    public bool InBlock { get; set; }
    public string BlockId { get; set; }

    public double? WeightKg { get; set; }
    public double? VolumeM3 { get; set; }

    public double? GrossAreaSqft { get; set; }
    public double? NetAreaSqft { get; set; }
    public double? NetAreaSqftNative { get; set; }
    public double? RoughAreaSqft { get; set; }

    public string Species { get; set; }
    public string Grade { get; set; }
    public string MaterialName { get; set; }
    public string MaterialId { get; set; }

    public double? DensityAssignedKgPerM3 { get; set; }

    // GetWeight() is a COMPOSITE: the sum of subpanel weights plus sheathing. DLT.Volume
    // covers the subpanels only, so weight/volume mixes scopes and is not a density of
    // anything. Confirmed by the plugin author and measured: on a reference model the
    // subpanel-scope density lands on exactly 450.0 kg/m3 (the assigned SPF value) while
    // the panel-scope figure reads 519.1, the difference being 13.3% sheathing.
    public double? DensitySubpanelsKgPerM3 { get; set; }
    public double? WeightSubpanelsKg { get; set; }
    public double? WeightSheathingKg { get; set; }
    public double? VolumeSheathingM3 { get; set; }
    public double? VolumeTotalM3 { get; set; }
    public double? DensitySheathingKgPerM3 { get; set; }
    public string SheathingMaterialName { get; set; }
    public bool? SheathingPresent { get; set; }

    public double? LengthMm { get; set; }
    public double? WidthMm { get; set; }
    public double? DepthMm { get; set; }

    public LamRecord Lam { get; set; }
    public LogisticsRecord Logistics { get; set; }
    public CountRecord Counts { get; set; }
    public ComplexityDescriptors Complexity { get; set; }
    public List<SubpanelRecord> Subpanels { get; set; } = new();
    public Dictionary<string, string> FieldErrors { get; set; } = new();

    // Native, converted values retained only long enough to round summary
    // aggregates once. System.Text.Json ignores non-public properties.
    internal double? RawWeightKg { get; set; }
    internal double? RawVolumeM3 { get; set; }
    internal double? RawNetAreaSqft { get; set; }
}

internal sealed class LamRecord
{
    public string Profile { get; set; }
    public string Arrangement { get; set; }
    public double? ThicknessMm { get; set; }
    public double? HeightMm { get; set; }
    public string Notation { get; set; }
    public int? Count { get; set; }
}

internal sealed class LogisticsRecord
{
    public string FabricationPackage { get; set; }
    public string Truck { get; set; }
    public string Bundle { get; set; }
    public int? BundleLevel { get; set; }
    public int? InstallSequence { get; set; }
}

internal sealed class CountRecord
{
    public int? DapsTotal { get; set; }
    public int? DapsFastener { get; set; }
    public int? DapsNonFastener { get; set; }
    public int? Fasteners { get; set; }
    public int? PanelSupports { get; set; }
    public int? PlanarCuts { get; set; }
}

internal sealed class ComplexityDescriptors
{
    public double? PenetrationAreaSqft { get; set; }
    public int? PenetrationCount { get; set; }
    public int? CornerCount { get; set; }
    public double? PerimeterM { get; set; }
    public double? CornersPerM { get; set; }
    public bool? IsRectangular { get; set; }
    public bool? HasPenetrations { get; set; }

    // Machining removed from the blank. PenetrationAreaSqft only sees material missing
    // from the PLAN outline, so a panel machined all over its faces still reports 0 - on
    // one reference job that was every one of 105 panels. These measure the solid instead.
    public double? MachinedVolumeM3 { get; set; }
    public double? MachinedPct { get; set; }
    public double? BlankVolumeM3 { get; set; }

    // Dap character. Counts of collection events on THIS panel, which is the honest
    // per-panel figure; a single dap can be shared by many panels, so these must never be
    // summed across a job to count physical daps.
    public int? DapsThrough { get; set; }
    public int? DapsSurface { get; set; }
    public int? DapsSheathing { get; set; }
    public int? DapToolSetups { get; set; }
    public double? MaxDapDepthMm { get; set; }
}

internal sealed class SubpanelRecord
{
    public string TypeLetter { get; set; }
    public double? NetAreaSqft { get; set; }
    public double? VolumeM3 { get; set; }
}

internal sealed class Element
{
    public string Type { get; set; }
    public string Id { get; set; }
    public string HostId { get; set; }
    public bool InBlock { get; set; }
    public string BlockId { get; set; }
    public bool? IsFastenerDap { get; set; }
    public double? WeightKg { get; set; }
    public Dictionary<string, string> FieldErrors { get; set; } = new();
}

internal sealed class Summary
{
    public int PanelCount { get; set; }
    public double? TotalNetAreaSqft { get; set; }
    public double? TotalVolumeM3 { get; set; }
    public double? TotalWeightKg { get; set; }
    public int PanelsWithErrors { get; set; }
    public List<MaterialBreakdownRecord> MaterialBreakdown { get; set; } = new();
    public SummaryLogisticsRecord Logistics { get; set; } = new();
}

internal sealed class MaterialBreakdownRecord
{
    public string Species { get; set; }
    public string Layup { get; set; }
    public int PanelCount { get; set; }
    public double? NetAreaSqft { get; set; }
    public double? WeightKg { get; set; }
}

internal sealed class SummaryLogisticsRecord
{
    public int TruckCount { get; set; }
    public int BundleCount { get; set; }
    public int FabricationPackageCount { get; set; }
    public int? InstallSequenceSpan { get; set; }
    public double InstallSequenceCoveragePct { get; set; }
}

internal sealed class UnitsRecord
{
    public string Area { get; set; } = "sqft";
    public string Volume { get; set; } = "m3";
    public string Weight { get; set; } = "kg";
    public string Length { get; set; } = "mm";
}
