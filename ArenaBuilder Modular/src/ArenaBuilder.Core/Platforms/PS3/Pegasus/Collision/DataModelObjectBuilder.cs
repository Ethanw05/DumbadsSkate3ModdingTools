namespace ArenaBuilder.Core.Platforms.PS3.Pegasus.Collision;

/// <summary>
/// Builds the DMO (Data Model Object) <c>pegasus::tDMOData</c> payload embedded in collision PSGs.
/// Currently emits a zeroed header: no DMO rows and no string table, so the runtime does not
/// register picnic-table (or any) static DMO census from this chunk. Replace with real placement
/// data when you have a proper exporter.
/// </summary>
public static class DataModelObjectBuilder
{
    /// <summary>
    /// sizeof(pegasus::tDMOData) = 20 in Skate 2/3: TypeId, NumDMOs, NumStrings, DMOs ptr, StringList ptr (BE).
    /// </summary>
    private static readonly byte[] EmptyDmoData = new byte[20];

    public static byte[] Build()
    {
        return (byte[])EmptyDmoData.Clone();
    }
}
