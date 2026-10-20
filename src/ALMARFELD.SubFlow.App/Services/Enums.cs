using ALMARFELD.SubFlow.Core.Jobs;

namespace ALMARFELD.SubFlow.App.Services;

/// <summary>Enum values exposed for XAML ComboBox items (x:Static).</summary>
public static class Enums
{
    public static readonly ExistingTrackPolicy TrackSkip = ExistingTrackPolicy.Skip;
    public static readonly ExistingTrackPolicy TrackKeepBoth = ExistingTrackPolicy.KeepBoth;
    public static readonly ExistingTrackPolicy TrackReplace = ExistingTrackPolicy.Replace;
    public static readonly ExistingOutputPolicy OutSkip = ExistingOutputPolicy.Skip;
    public static readonly ExistingOutputPolicy OutRename = ExistingOutputPolicy.Rename;
    public static readonly ExistingOutputPolicy OutOverwrite = ExistingOutputPolicy.Overwrite;
}
